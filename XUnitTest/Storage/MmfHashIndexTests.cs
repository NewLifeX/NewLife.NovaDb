using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

#nullable enable

namespace XUnitTest.Storage;

public class MmfHashIndexTests : IDisposable
{
    private readonly String _testFile;
    private readonly IndexKeyCodec _codec = new(new DefaultDataCodec(), [DataType.Int32]);

    public MmfHashIndexTests()
    {
        _testFile = Path.Combine(Path.GetTempPath(), $"hash_{Guid.NewGuid():N}.hidx");
    }

    public void Dispose()
    {
        if (File.Exists(_testFile)) File.Delete(_testFile);
        var temp = _testFile + ".compact";
        if (File.Exists(temp)) File.Delete(temp);
    }

    private Byte[] Key(Int32 value)
    {
        var buffer = new Byte[_codec.GetEncodedLength(value)];
        _codec.Encode(value, buffer, 0);
        return buffer;
    }

    private static Byte[] Payload(Int32 value) => BitConverter.GetBytes(value);

    private static List<Int32> ReadValues(MmfHashIndex index, Byte[] key)
    {
        var result = new List<Int32>();
        var value = index.GetFirstValue(key);
        while (value != 0)
        {
            result.Add(BitConverter.ToInt32(index.ReadValue(value), 0));
            value = index.GetNextValue(value);
        }
        return result;
    }

    private static void AssertValues(MmfHashIndex index, Byte[] key, params Int32[] expected)
        => Assert.Equal(expected, ReadValues(index, key));

    [Fact(DisplayName = "新建索引可写入并读回")]
    public void AddAndRead()
    {
        using var index = MmfHashIndex.Open(_testFile, _codec);

        index.Apply([MmfHashChange.Add(Key(1), Payload(100)), MmfHashChange.Add(Key(2), Payload(200))]);

        Assert.Equal(2, index.KeyCount);
        Assert.Equal(2, index.ValueCount);
        AssertValues(index, Key(1), 100);
        AssertValues(index, Key(2), 200);
        AssertValues(index, Key(3));
    }

    [Fact(DisplayName = "一个键可挂多个值并按顺序读出")]
    public void MultipleValues()
    {
        using var index = MmfHashIndex.Open(_testFile, _codec);

        index.Apply([
            MmfHashChange.Add(Key(1), Payload(10)),
            MmfHashChange.Add(Key(1), Payload(20)),
            MmfHashChange.Add(Key(1), Payload(30)),
        ]);

        AssertValues(index, Key(1), 10, 20, 30);
        Assert.Equal(3, index.GetValueCount(Key(1)));
    }

    [Fact(DisplayName = "重开后数据仍然存在")]
    public void ReopenPersists()
    {
        using (var index = MmfHashIndex.Open(_testFile, _codec))
        {
            index.Apply([MmfHashChange.Add(Key(1), Payload(11)), MmfHashChange.Add(Key(2), Payload(22))], appliedOffset: 4096);
        }

        using var reopened = MmfHashIndex.Open(_testFile, _codec);

        Assert.Equal(2, reopened.KeyCount);
        Assert.Equal(4096, reopened.AppliedOffset);
        AssertValues(reopened, Key(1), 11);
        AssertValues(reopened, Key(2), 22);
    }

    [Fact(DisplayName = "移除单个值后其余值保留")]
    public void RemoveValue()
    {
        using var index = MmfHashIndex.Open(_testFile, _codec);
        index.Apply([
            MmfHashChange.Add(Key(1), Payload(10)),
            MmfHashChange.Add(Key(1), Payload(20)),
        ]);

        index.Apply([MmfHashChange.RemoveValue(Key(1), Payload(10))]);

        AssertValues(index, Key(1), 20);
        Assert.Equal(1, index.ValueCount);
        Assert.Equal(1, index.KeyCount);
    }

    [Fact(DisplayName = "移除最后一个值后键节点一并摘除")]
    public void RemoveLastValueDropsKey()
    {
        using var index = MmfHashIndex.Open(_testFile, _codec);
        index.Apply([MmfHashChange.Add(Key(1), Payload(10))]);

        index.Apply([MmfHashChange.RemoveValue(Key(1), Payload(10))]);

        Assert.Equal(0, index.KeyCount);
        Assert.Equal(0, index.ValueCount);
        AssertValues(index, Key(1));
    }

    [Fact(DisplayName = "移除整个键")]
    public void RemoveKey()
    {
        using var index = MmfHashIndex.Open(_testFile, _codec);
        index.Apply([MmfHashChange.Add(Key(1), Payload(10)), MmfHashChange.Add(Key(1), Payload(20)), MmfHashChange.Add(Key(2), Payload(30))]);

        index.Apply([MmfHashChange.RemoveKey(Key(1))]);

        Assert.Equal(1, index.KeyCount);
        Assert.Equal(1, index.ValueCount);
        AssertValues(index, Key(1));
        AssertValues(index, Key(2), 30);
    }

    [Fact(DisplayName = "写入超过初始容量后自动扩容且不丢键")]
    public void RehashGrowth()
    {
        const Int32 count = 5000;
        using (var index = MmfHashIndex.Open(_testFile, _codec))
        {
            var changes = new List<MmfHashChange>(count);
            for (var i = 1; i <= count; i++) changes.Add(MmfHashChange.Add(Key(i), Payload(i)));

            index.Apply(changes);

            Assert.Equal(count, index.KeyCount);
        }

        using var reopened = MmfHashIndex.Open(_testFile, _codec);
        Assert.Equal(count, reopened.KeyCount);

        for (var i = 1; i <= count; i++) AssertValues(reopened, Key(i), i);
    }

    [Fact(DisplayName = "唯一写入会跳过重复值")]
    public void UniqueSkipsDuplicate()
    {
        using var index = MmfHashIndex.Open(_testFile, _codec);
        index.Apply([MmfHashChange.Add(Key(1), Payload(10), unique: true)]);
        index.Apply([MmfHashChange.Add(Key(1), Payload(10), unique: true)]);
        index.Apply([MmfHashChange.Add(Key(1), Payload(20), unique: true)]);

        AssertValues(index, Key(1), 10, 20);
    }

    [Fact(DisplayName = "超长键可正确比较与读回")]
    public void LargeKey()
    {
        var codec = new IndexKeyCodec(new DefaultDataCodec(), [DataType.String]);
        var key1 = new Byte[512];
        var key2 = new Byte[512];
        for (var i = 0; i < key1.Length; i++) key1[i] = (Byte)(i & 0xFF);
        Array.Copy(key1, key2, key1.Length);
        key2[511] = 1;

        using var index = MmfHashIndex.Open(_testFile, codec);
        index.Apply([MmfHashChange.Add(key1, Payload(1)), MmfHashChange.Add(key2, Payload(2))]);

        Assert.Equal(2, index.KeyCount);
        Assert.Equal(1, BitConverter.ToInt32(index.ReadValue(index.GetFirstValue(key1)), 0));
        Assert.Equal(2, BitConverter.ToInt32(index.ReadValue(index.GetFirstValue(key2)), 0));
    }

    [Fact(DisplayName = "枚举可读出全部键值")]
    public void EnumerateAll()
    {
        using var index = MmfHashIndex.Open(_testFile, _codec);
        index.Apply([MmfHashChange.Add(Key(1), Payload(10)), MmfHashChange.Add(Key(1), Payload(20)), MmfHashChange.Add(Key(2), Payload(30))]);

        var payloads = index.Enumerate().Select(x => BitConverter.ToInt32(x.Payload, 0)).OrderBy(x => x).ToList();

        Assert.Equal(new[] { 10, 20, 30 }, payloads);
    }

    [Fact(DisplayName = "压缩丢弃指定条目并重写文件")]
    public void CompactDropsEntries()
    {
        using (var index = MmfHashIndex.Open(_testFile, _codec))
        {
            index.Apply([
                MmfHashChange.Add(Key(1), Payload(10)),
                MmfHashChange.Add(Key(2), Payload(20)),
                MmfHashChange.Add(Key(3), Payload(30)),
            ], appliedOffset: 100);

            index.Compact((key, payload) => BitConverter.ToInt32(payload, 0) != 20, appliedOffset: 200);

            Assert.Equal(2, index.KeyCount);
            Assert.Equal(200, index.AppliedOffset);
            AssertValues(index, Key(2));
        }

        using var reopened = MmfHashIndex.Open(_testFile, _codec);
        Assert.Equal(2, reopened.KeyCount);
        AssertValues(reopened, Key(1), 10);
        AssertValues(reopened, Key(3), 30);
    }

    [Fact(DisplayName = "元数据损坏时打开失败，交由上层重建")]
    public void CorruptedMetaFails()
    {
        using (var index = MmfHashIndex.Open(_testFile, _codec))
        {
            index.Apply([MmfHashChange.Add(Key(1), Payload(10))]);
        }

        // 抹掉两个元数据槽
        using (var stream = new FileStream(_testFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = FileHeader.HeaderSize;
            var zeros = new Byte[320];
            stream.Write(zeros, 0, zeros.Length);
        }

        Assert.Throws<NovaException>(() => MmfHashIndex.Open(_testFile, _codec));
    }

    [Fact(DisplayName = "只读打开可读不可写")]
    public void ReadOnlyMode()
    {
        using (var index = MmfHashIndex.Open(_testFile, _codec))
        {
            index.Apply([MmfHashChange.Add(Key(1), Payload(10))]);
        }

        using var readOnly = MmfHashIndex.Open(_testFile, _codec, readOnly: true);

        AssertValues(readOnly, Key(1), 10);

        var ex = Assert.Throws<NovaException>(() => readOnly.Apply([MmfHashChange.Add(Key(2), Payload(20))]));
        Assert.Equal(ErrorCode.ReadOnlyViolation, ex.Code);
    }
}
