using NewLife.Buffers;
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
            Log.XTrace.WriteLine("WAL file not found, no recovery needed");
            return;
        }

        using var fs = new FileStream(_walPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var committedTxs = new HashSet<UInt64>();
        var pageUpdates = new List<(UInt64 txId, UInt64 pageId, Byte[] data)>();

        Log.XTrace.WriteLine($"Starting WAL recovery from {_walPath}");

        // 扫描所有记录，通过 SpanReader 流模式仅读取头部，按需读取负载
        while (fs.Position < fs.Length)
        {
            try
            {
                var recordStart = fs.Position;

                // 通过 SpanReader 流模式按需读取，内部缓冲区仅 256 字节
                var reader = new SpanReader(fs, bufferSize: 256);

                // 读取 4 字节长度前缀
                var totalLength = reader.ReadInt32();
                if (totalLength <= 0 || totalLength > 10 * 1024 * 1024) break;
                if (totalLength < WalRecord.HeaderSize) break;

                // 读取头部
                var record = new WalRecord();
                record.Read(ref reader);

                if (record.RecordType == WalRecordType.CommitTx)
                {
                    committedTxs.Add(record.TxId);
                }
                else if (record.RecordType == WalRecordType.UpdatePage && record.DataLength > 0)
                {
                    // 定位到负载数据位置，仅在需要时才从流中读取
                    fs.Position = recordStart + 4 + WalRecord.HeaderSize;
                    var data = new Byte[record.DataLength];
                    if (fs.Read(data, 0, data.Length) != data.Length) break;
                    pageUpdates.Add((record.TxId, record.PageId, data));
                }

                LastCommittedLsn = Math.Max(LastCommittedLsn, record.Lsn);

                // 定位到下一条记录
                fs.Position = recordStart + 4 + totalLength;
            }
            catch (Exception ex)
            {
                Log.XTrace.WriteLine($"WAL record read error (position {fs.Position}): {ex.Message}");
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
                    Log.XTrace.WriteException(ex);
                    throw new NovaException(ErrorCode.IoError,
                        $"Failed to apply page update during recovery: pageId={pageId}, txId={txId}", ex);
                }
            }
        }

        Log.XTrace.WriteLine($"WAL recovery completed: {committedTxs.Count} committed transactions, " +
            $"{appliedCount} page updates applied, last LSN={LastCommittedLsn}");
    }
}
