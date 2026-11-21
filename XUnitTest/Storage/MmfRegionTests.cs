using System;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using Xunit;

namespace XUnitTest.Storage;

public class MmfRegionTests : IDisposable
{
    private readonly String _testFile;

    public MmfRegionTests()
    {
        _testFile = Path.Combine(Path.GetTempPath(), $"region_{Guid.NewGuid():N}.hidx");
    }

    public void Dispose()
    {
        if (File.Exists(_testFile)) File.Delete(_testFile);
        if (File.Exists(_testFile + ".moved")) File.Delete(_testFile + ".moved");
    }

    [Fact(DisplayName = "写入后重开仍能读到数据")]
    public void WriteAndReopen()
    {
        using (var region = new MmfRegion(_testFile))
        {
            region.Open();
            region.WriteInt64(0, 1234567890123L);
            region.WriteInt32(8, 42);
            region.Write(16, new Byte[] { 1, 2, 3, 4 }, 0, 4);
            region.Flush();
        }

        using var reopened = new MmfRegion(_testFile);
        reopened.Open();

        Assert.Equal(1234567890123L, reopened.ReadInt64(0));
        Assert.Equal(42, reopened.ReadInt32(8));
        Assert.Equal(new Byte[] { 1, 2, 3, 4 }, reopened.ReadBytes(16, 4));
    }

    [Fact(DisplayName = "跨越初始容量写入会自动扩容并保持数据")]
    public void GrowBeyondInitialCapacity()
    {
        var size = 3 * 1024 * 1024;
        var payload = new Byte[size];
        for (var i = 0; i < size; i++) payload[i] = (Byte)(i & 0xFF);

        using (var region = new MmfRegion(_testFile))
        {
            region.Open();
            region.Write(0, payload, 0, payload.Length);
            region.Flush();

            Assert.True(region.Length >= size);
        }

        using var reopened = new MmfRegion(_testFile);
        reopened.Open();

        var read = reopened.ReadBytes(0, size);
        Assert.Equal(payload, read);
    }

    [Fact(DisplayName = "读取超出文件长度时抛文件损坏异常")]
    public void ReadBeyondLengthThrows()
    {
        using var region = new MmfRegion(_testFile);
        region.Open();

        var ex = Assert.Throws<NovaException>(() => region.ReadInt64(region.Length));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
    }

    [Fact(DisplayName = "只读打开可读不可写")]
    public void ReadOnlyMode()
    {
        using (var region = new MmfRegion(_testFile))
        {
            region.Open();
            region.WriteInt32(0, 7);
            region.Flush();
        }

        using var readOnly = new MmfRegion(_testFile, readOnly: true);
        readOnly.Open();

        Assert.Equal(7, readOnly.ReadInt32(0));

        var ex = Assert.Throws<NovaException>(() => readOnly.WriteInt32(0, 8));
        Assert.Equal(ErrorCode.ReadOnlyViolation, ex.Code);
    }

    [Fact(DisplayName = "截断后文件长度收紧，超界读取失败")]
    public void SetLengthShrinks()
    {
        using var region = new MmfRegion(_testFile);
        region.Open();
        region.WriteInt64(0, 99);

        region.SetLength(MmfRegion.MinCapacity);

        Assert.Equal(MmfRegion.MinCapacity, region.Length);
        Assert.Equal(99, region.ReadInt64(0));

        var ex = Assert.Throws<NovaException>(() => region.ReadInt64(MmfRegion.MinCapacity));
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
    }

    [Fact(DisplayName = "释放映射后可删除文件")]
    public void ResetThenDelete()
    {
        var region = new MmfRegion(_testFile);
        region.Open();
        region.WriteInt64(0, 1);

        region.Reset();
        region.SetLength(MmfRegion.MinCapacity);
        region.Dispose();

        File.Delete(_testFile);
        Assert.False(File.Exists(_testFile));
    }

    [Fact(DisplayName = "映射期间文件可改名移走，视图继续可读写")]
    public void RenameWhileMapped()
    {
        var moved = _testFile + ".moved";

        var region = new MmfRegion(_testFile);
        region.Open();
        region.WriteInt64(0, 42);

        // Windows 有映射视图时不允许删除，但允许改名；Linux 两者都可以
        File.Move(_testFile, moved);
        Assert.False(File.Exists(_testFile));

        Assert.Equal(42L, region.ReadInt64(0));
        region.WriteInt64(8, 99);
        Assert.Equal(99L, region.ReadInt64(8));

        region.Dispose();

        // 释放映射后，被移走的文件才能被真正删除
        File.Delete(moved);
        Assert.False(File.Exists(moved));
    }

    [Fact(DisplayName = "只读打开不存在的文件时抛异常")]
    public void ReadOnlyMissingFileThrows()
    {
        using var region = new MmfRegion(_testFile, readOnly: true);

        var ex = Assert.Throws<NovaException>(() => region.Open());
        Assert.Equal(ErrorCode.FileCorrupted, ex.Code);
    }
}
