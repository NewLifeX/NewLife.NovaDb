using NewLife.Data;
using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.WAL;

/// <summary>WAL 恢复管理器</summary>
public class WalRecovery
{
    private readonly String _walPath;
    private readonly Action<UInt64, Byte[]> _applyPageUpdate;

    /// <summary>最后一个已提交事务的 LSN</summary>
    public UInt64 LastCommittedLsn { get; private set; }

    /// <summary>实例化 WAL 恢复管理器</summary>
    /// <param name="walPath">WAL 文件路径</param>
    /// <param name="applyPageUpdate">应用页更新的回调方法</param>
    public WalRecovery(String walPath, Action<UInt64, Byte[]> applyPageUpdate)
    {
        _walPath = walPath ?? throw new ArgumentNullException(nameof(walPath));
        _applyPageUpdate = applyPageUpdate ?? throw new ArgumentNullException(nameof(applyPageUpdate));
    }

    /// <summary>执行恢复（重放 WAL）</summary>
    public void Recover()
    {
        if (!File.Exists(_walPath))
        {
            NewLife.Log.XTrace.WriteLine("WAL file not found, no recovery needed");
            return;
        }

        using var fs = new FileStream(_walPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var committedTxs = new HashSet<UInt64>();
        var pageUpdates = new List<(UInt64 txId, UInt64 pageId, Byte[] data)>();

        NewLife.Log.XTrace.WriteLine($"Starting WAL recovery from {_walPath}");

        // 第一遍：扫描所有记录，找出已提交的事务
        while (fs.Position < fs.Length)
        {
            try
            {
                var record = ReadWalRecord(fs);
                if (record == null)
                    break;

                if (record.RecordType == WalRecordType.CommitTx)
                {
                    committedTxs.Add(record.TxId);
                }
                else if (record.RecordType == WalRecordType.UpdatePage)
                {
                    pageUpdates.Add((record.TxId, record.PageId, record.Data));
                }

                LastCommittedLsn = Math.Max(LastCommittedLsn, record.Lsn);
            }
            catch (Exception ex)
            {
                NewLife.Log.XTrace.WriteLine($"WAL record read error (position {fs.Position}): {ex.Message}");
                break;
            }
        }

        // 第二遍：重放已提交事务的页更新
        var appliedCount = 0;
        foreach (var (txId, pageId, data) in pageUpdates)
        {
            if (committedTxs.Contains(txId))
            {
                try
                {
                    _applyPageUpdate(pageId, data);
                    appliedCount++;
                }
                catch (Exception ex)
                {
                    NewLife.Log.XTrace.WriteException(ex);
                    throw new NovaException(ErrorCode.IoError,
                        $"Failed to apply page update during recovery: pageId={pageId}, txId={txId}", ex);
                }
            }
        }

        NewLife.Log.XTrace.WriteLine($"WAL recovery completed: {committedTxs.Count} committed transactions, " +
            $"{appliedCount} page updates applied, last LSN={LastCommittedLsn}");
    }

    /// <summary>读取单个 WAL 记录</summary>
    private WalRecord? ReadWalRecord(FileStream fs)
    {
        // 读取长度前缀
        var lengthPrefix = new Byte[4];
        var bytesRead = fs.Read(lengthPrefix, 0, 4);
        if (bytesRead != 4)
            return null;

        var length = BitConverter.ToInt32(lengthPrefix, 0);
        if (length <= 0 || length > 10 * 1024 * 1024) // 最大 10MB
        {
            NewLife.Log.XTrace.WriteLine($"Invalid WAL record length: {length}");
            return null;
        }

        // 读取记录数据
        var data = new Byte[length];
        bytesRead = fs.Read(data, 0, length);
        if (bytesRead != length)
        {
            NewLife.Log.XTrace.WriteLine($"Incomplete WAL record: expected {length} bytes, got {bytesRead}");
            return null;
        }

        return WalRecord.Read(new ArrayPacket(data));
    }
}
