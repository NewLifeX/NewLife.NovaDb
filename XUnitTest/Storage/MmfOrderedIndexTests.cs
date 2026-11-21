using System;
using System.Collections.Generic;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

#nullable enable

namespace XUnitTest.Storage;

public class MmfOrderedIndexTests : IDisposable
{
    private readonly String _testFile;
    private readonly IndexKeyCodec _codec = new(new DefaultDataCodec(), [DataType.Int32]);

    public MmfOrderedIndexTests()
    {
        _testFile = Path.Combine(Path.GetTempPath(), $"ordered_{Guid.NewGuid():N}.oidx");
    }

    public void Dispose()
    {
        if (File.Exists(_testFile)) File.Delete(_testFile);
        var temp = _testFile + ".compact";
        if (File.Exists(temp)) File.Delete(temp);
    }

    private Byte[] Encode(Int32 value)
    {
        var buffer = new Byte[_codec.GetEncodedLength(value)];
        _codec.Encode(value, buffer, 0);
        return buffer;
    }

    private Int32 Decode(Byte[] key) => Assert.IsType<Int32>(_codec.Decode(key, 0, key.Length));

    private OrderedIndexEntry Entry(Int32 key, UInt64 createdTx, UInt64 deletedTx = 0, Int64 dataOffset = 0)
    {
        var hasPrefix = _codec.TryGetSortPrefix(key, out var prefix);

        return new OrderedIndexEntry
        {
            Key = Encode(key),
            SortPrefix = prefix,
            HasPrefix = hasPrefix,
            CreatedTx = createdTx,
            DeletedTx = deletedTx,
            DataOffset = dataOffset,
            DataLength = 16,
        };
    }

    private List<Int32> ScanValues(MmfOrderedIndex index, Int32? from = null, Boolean includeFrom = true, Int32? to = null, Boolean includeTo = true, Boolean descending = false, Int32 maxRows = 0)
    {
        var result = new List<Int32>();
        index.Scan(
            from.HasValue ? Encode(from.Value) : null, includeFrom,
            to.HasValue ? Encode(to.Value) : null, includeTo,
            descending, maxRows,
            entry =>
            {
                result.Add(Decode(entry.Key));
                return true;
            });

        return result;
    }

    [Fact(DisplayName = "写入后按升序扫描")]
    public void ScanAscending()
    {
        using var index = MmfOrderedIndex.Open(_testFile, _codec);
        index.Add([Entry(3, 1), Entry(1, 1), Entry(2, 1)], appliedOffset: 10);
        index.Flush();

        Assert.Equal(new[] { 1, 2, 3 }, ScanValues(index));
        Assert.Equal(10, index.AppliedOffset);
        Assert.Equal(1, index.RunCount);
    }

    [Fact(DisplayName = "重开后条目与偏移保留")]
    public void ReopenPersists()
    {
        using (var index = MmfOrderedIndex.Open(_testFile, _codec))
        {
            index.Add([Entry(1, 1), Entry(2, 1)], appliedOffset: 100);
            index.Flush();
        }

        using var reopened = MmfOrderedIndex.Open(_testFile, _codec);

        Assert.Equal(100, reopened.AppliedOffset);
        Assert.Equal(new[] { 1, 2 }, ScanValues(reopened));
    }

    [Fact(DisplayName = "范围扫描的边界包含与排除正确")]
    public void RangeBounds()
    {
        using var index = MmfOrderedIndex.Open(_testFile, _codec);
        index.Add([Entry(1, 1), Entry(2, 1), Entry(3, 1), Entry(4, 1), Entry(5, 1)]);
        index.Flush();

        Assert.Equal(new[] { 2, 3, 4 }, ScanValues(index, from: 2, to: 4));
        Assert.Equal(new[] { 3, 4 }, ScanValues(index, from: 2, includeFrom: false, to: 4));
        Assert.Equal(new[] { 2, 3 }, ScanValues(index, from: 2, to: 4, includeTo: false));
        Assert.Equal(new[] { 3, 4, 5 }, ScanValues(index, from: 3));
        Assert.Equal(new[] { 1, 2 }, ScanValues(index, to: 2));
    }

    [Fact(DisplayName = "降序扫描与 maxRows 生效")]
    public void DescendingWithLimit()
    {
        using var index = MmfOrderedIndex.Open(_testFile, _codec);
        index.Add([Entry(1, 1), Entry(2, 1), Entry(3, 1), Entry(4, 1)]);
        index.Flush();

        Assert.Equal(new[] { 4, 3, 2, 1 }, ScanValues(index, descending: true));
        Assert.Equal(new[] { 4, 3 }, ScanValues(index, descending: true, maxRows: 2));
        Assert.Equal(new[] { 3, 2 }, ScanValues(index, from: 2, to: 3, descending: true));
    }

    [Fact(DisplayName = "跨 run 的同键多版本只输出最新存活版本")]
    public void MergeVersions()
    {
        using var index = MmfOrderedIndex.Open(_testFile, _codec);

        index.Add([Entry(1, 1), Entry(2, 1)]);
        index.Flush();

        // 第二个 run：键 1 被事务 2 更新，旧版本标记删除
        index.Add([Entry(1, 2), Entry(1, 1, deletedTx: 2)]);
        index.Flush();

        Assert.Equal(new[] { 1, 2 }, ScanValues(index));
        Assert.Equal(2, index.RunCount);

        // 键 1 的最新版本来自事务 2
        Assert.True(index.TryGetLive(Encode(1), out var live));
        Assert.Equal(2UL, live.CreatedTx);
    }

    [Fact(DisplayName = "写缓冲与已落盘 run 一起归并")]
    public void BufferMergesWithRuns()
    {
        using var index = MmfOrderedIndex.Open(_testFile, _codec);

        index.Add([Entry(2, 1), Entry(4, 1)]);
        index.Flush();
        index.Add([Entry(1, 1), Entry(3, 1)]);

        Assert.Equal(2, index.BufferCount);

        Assert.Equal(new[] { 1, 2, 3, 4 }, ScanValues(index));
    }

    [Fact(DisplayName = "同一批次内的重复键只输出一个")]
    public void DuplicateKeysInOneBatch()
    {
        using var index = MmfOrderedIndex.Open(_testFile, _codec);

        index.Add([Entry(1, 1), Entry(1, 2), Entry(1, 1, deletedTx: 2), Entry(2, 1)]);
        index.Flush();

        Assert.Equal(new[] { 1, 2 }, ScanValues(index));
    }

    [Fact(DisplayName = "压缩合并 run 并丢弃历史版本")]
    public void CompactMergesRuns()
    {
        using (var index = MmfOrderedIndex.Open(_testFile, _codec))
        {
            index.Add([Entry(1, 1), Entry(2, 1)]);
            index.Flush();
            index.Add([Entry(1, 2), Entry(1, 1, deletedTx: 2)]);
            index.Flush();

            Assert.Equal(2, index.RunCount);

            index.Compact();

            Assert.Equal(1, index.RunCount);
            Assert.Equal(2, index.EntryCount);
            Assert.Equal(new[] { 1, 2 }, ScanValues(index));
        }

        using var reopened = MmfOrderedIndex.Open(_testFile, _codec);
        Assert.Equal(1, reopened.RunCount);
        Assert.Equal(new[] { 1, 2 }, ScanValues(reopened));
    }

    [Fact(DisplayName = "run 数量超阈值时自动压缩")]
    public void AutoCompactWhenTooManyRuns()
    {
        using var index = MmfOrderedIndex.Open(_testFile, _codec, flushEntries: 1);

        for (var i = 1; i <= 20; i++)
        {
            index.Add([Entry(i, (UInt64)i)]);
            index.Flush();
        }

        Assert.True(index.RunCount <= 8);
        Assert.Equal(20, ScanValues(index).Count);
    }

    [Fact(DisplayName = "清空后扫描为空")]
    public void TruncateClears()
    {
        using var index = MmfOrderedIndex.Open(_testFile, _codec);
        index.Add([Entry(1, 1), Entry(2, 1)], appliedOffset: 50);
        index.Flush();

        index.Truncate();

        Assert.Equal(0, index.EntryCount);
        Assert.Equal(0, index.AppliedOffset);
        Assert.Empty(ScanValues(index));
    }

    [Fact(DisplayName = "元数据损坏时打开失败")]
    public void CorruptedMetaFails()
    {
        using (var index = MmfOrderedIndex.Open(_testFile, _codec))
        {
            index.Add([Entry(1, 1)]);
            index.Flush();
        }

        using (var stream = new FileStream(_testFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = FileHeader.HeaderSize;
            var zeros = new Byte[320];
            stream.Write(zeros, 0, zeros.Length);
        }

        Assert.Throws<NovaException>(() => MmfOrderedIndex.Open(_testFile, _codec));
    }
}
