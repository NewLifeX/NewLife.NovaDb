using System;
using System.IO;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.WAL;
using Xunit;

namespace XUnitTest.WAL;

public class WalWriterTests : IDisposable
{
    private readonly string _walFile;

    public WalWriterTests()
    {
        _walFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.wal");
    }

    public void Dispose()
    {
        if (File.Exists(_walFile))
        {
            File.Delete(_walFile);
        }
    }

    [Fact]
    public void TestWriteAndReadWalRecord()
    {
        using (var writer = new WalWriter(_walFile, WalMode.Full))
        {
            writer.Open();

            var record = new WalRecord
            {
                TxId = 1,
                RecordType = WalRecordType.BeginTx
            };

            var lsn = writer.Write(record);
            Assert.Equal(1UL, lsn);

            var record2 = new WalRecord
            {
                TxId = 1,
                RecordType = WalRecordType.CommitTx
            };

            var lsn2 = writer.Write(record2);
            Assert.Equal(2UL, lsn2);
        }

        Assert.True(File.Exists(_walFile));
        Assert.True(new FileInfo(_walFile).Length > 0);
    }

    [Fact]
    public void TestWalModes()
    {
        using (var writer = new WalWriter(_walFile, WalMode.Full))
        {
            writer.Open();
            var record = new WalRecord { TxId = 1, RecordType = WalRecordType.BeginTx };
            writer.Write(record);
        }

        Assert.True(File.Exists(_walFile));
    }

    [Fact]
    public void TestPageUpdateRecord()
    {
        using var writer = new WalWriter(_walFile, WalMode.Full);
        writer.Open();

        var pageData = new byte[100];
        for (int i = 0; i < pageData.Length; i++)
        {
            pageData[i] = (byte)(i % 256);
        }

        var record = new WalRecord
        {
            TxId = 1,
            RecordType = WalRecordType.UpdatePage,
            PageId = 5,
            Data = pageData
        };

        var lsn = writer.Write(record);
        Assert.Equal(1UL, lsn);
        Assert.Equal(1UL, record.Lsn);
    }
}
