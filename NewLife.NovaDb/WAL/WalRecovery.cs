using System.Buffers;
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

        // 复用长度前缀缓冲区
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Byte> lengthPrefix = stackalloc Byte[4];
#else
        var lengthPrefix = new Byte[4];
#endif
        // 复用记录体缓冲区
        var dataBuf = ArrayPool<Byte>.Shared.Rent(4096);
        try
        {
            // 第一遍：扫描所有记录，找出已提交的事务
            while (fs.Position < fs.Length)
            {
                try
                {
                    // 读取长度前缀
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
                    if (fs.Read(lengthPrefix) != 4) break;
                    var length = BitConverter.ToInt32(lengthPrefix);
#else
                    if (fs.Read(lengthPrefix, 0, 4) != 4) break;
                    var length = BitConverter.ToInt32(lengthPrefix, 0);
#endif

                    if (length <= 0 || length > 10 * 1024 * 1024)
                    {
                        NewLife.Log.XTrace.WriteLine($"Invalid WAL record length: {length}");
                        break;
                    }

                    // 确保缓冲区足够大
                    if (dataBuf.Length < length)
                    {
                        ArrayPool<Byte>.Shared.Return(dataBuf);
                        dataBuf = ArrayPool<Byte>.Shared.Rent(length);
                    }

                    // 读取记录数据
                    var bytesRead = fs.Read(dataBuf, 0, length);
                    if (bytesRead != length)
                    {
                        NewLife.Log.XTrace.WriteLine($"Incomplete WAL record: expected {length} bytes, got {bytesRead}");
                        break;
                    }

                    var record = WalRecord.Read(new ArrayPacket(dataBuf, 0, length));

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
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(dataBuf);
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
}
