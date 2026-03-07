using System;
using System.IO;
using System.Collections.Generic;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.WAL;
using Xunit;

namespace XUnitTest.WAL;

public class WalRecoveryTests : IDisposable
{
    private readonly string _walFile;
    private readonly Dictionary<ulong, byte[]> _pages;

    public WalRecoveryTests()
    {
        _walFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.wal");
        _pages = new Dictionary<ulong, byte[]>();
    }

    public void Dispose()
    {
        if (File.Exists(_walFile))
        {
            File.Delete(_walFile);
        }
    }

    [Fact]
    public void TestRecoveryWithCommittedTransaction()
    {
        using (var writer = new WalWriter(_walFile, WalMode.Full))
        {
            writer.Open();

            writer.Write(new WalRecord { TxId = 1, RecordType = WalRecordType.BeginTx });
            writer.Write(new WalRecord
            {
                TxId = 1,
                RecordType = WalRecordType.UpdatePage,
                PageId = 10,
            }, [1, 2, 3, 4, 5]);
            writer.Write(new WalRecord { TxId = 1, RecordType = WalRecordType.CommitTx });
        }

        var recovery = new WalRecovery(_walFile, (pageId, data) =>
        {
            _pages[pageId] = data;
        });

        recovery.Recover();

        Assert.True(_pages.ContainsKey(10));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, _pages[10]);
    }

    [Fact]
    public void TestRecoveryWithUncommittedTransaction()
    {
        using (var writer = new WalWriter(_walFile, WalMode.Full))
        {
            writer.Open();

            writer.Write(new WalRecord { TxId = 1, RecordType = WalRecordType.BeginTx });
            writer.Write(new WalRecord
            {
                TxId = 1,
                RecordType = WalRecordType.UpdatePage,
                PageId = 10,
            }, [1, 2, 3, 4, 5]);
        }

        var recovery = new WalRecovery(_walFile, (pageId, data) =>
        {
            _pages[pageId] = data;
        });

        recovery.Recover();

        Assert.False(_pages.ContainsKey(10));
    }

    [Fact]
    public void TestRecoveryWithMultipleTransactions()
    {
        using (var writer = new WalWriter(_walFile, WalMode.Full))
        {
            writer.Open();

            writer.Write(new WalRecord { TxId = 1, RecordType = WalRecordType.BeginTx });
            writer.Write(new WalRecord { TxId = 1, RecordType = WalRecordType.UpdatePage, PageId = 1 }, [1]);
            writer.Write(new WalRecord { TxId = 1, RecordType = WalRecordType.CommitTx });

            writer.Write(new WalRecord { TxId = 2, RecordType = WalRecordType.BeginTx });
            writer.Write(new WalRecord { TxId = 2, RecordType = WalRecordType.UpdatePage, PageId = 2 }, [2]);

            writer.Write(new WalRecord { TxId = 3, RecordType = WalRecordType.BeginTx });
            writer.Write(new WalRecord { TxId = 3, RecordType = WalRecordType.UpdatePage, PageId = 3 }, [3]);
            writer.Write(new WalRecord { TxId = 3, RecordType = WalRecordType.CommitTx });
        }

        var recovery = new WalRecovery(_walFile, (pageId, data) =>
        {
            _pages[pageId] = data;
        });

        recovery.Recover();

        Assert.True(_pages.ContainsKey(1));
        Assert.False(_pages.ContainsKey(2));
        Assert.True(_pages.ContainsKey(3));
    }
}
