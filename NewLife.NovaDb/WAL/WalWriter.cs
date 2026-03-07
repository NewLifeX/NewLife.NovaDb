using NewLife.Buffers;
using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.WAL;

/// <summary>WAL 写入器</summary>
public class WalWriter : IDisposable
{
    private readonly String _walPath;
    private readonly WalMode _mode;
    private FileStream? _fileStream;
    private UInt64 _nextLsn;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Boolean _disposed;
    private DateTime _lastFlush;

    /// <summary>WAL 文件路径</summary>
    public String WalPath => _walPath;

    /// <summary>WAL 模式</summary>
    public WalMode Mode => _mode;

    /// <summary>下一个 LSN</summary>
    public UInt64 NextLsn
    {
        get
        {
            lock (_lock)
            {
                return _nextLsn;
            }
        }
    }

    /// <summary>实例化 WAL 写入器</summary>
    /// <param name="walPath">WAL 文件路径</param>
    /// <param name="mode">WAL 模式</param>
    public WalWriter(String walPath, WalMode mode)
    {
        _walPath = walPath ?? throw new ArgumentNullException(nameof(walPath));
        _mode = mode;
        _nextLsn = 1;
        _lastFlush = DateTime.UtcNow;
    }

    /// <summary>打开 WAL 文件</summary>
    public void Open()
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WalWriter));

            var isNewFile = !File.Exists(_walPath);

            _fileStream = new FileStream(_walPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

            if (!isNewFile)
            {
                // 扫描现有 WAL 以确定下一个 LSN
                _nextLsn = ScanWalForMaxLsn() + 1;
                // 定位到文件末尾以便追加
                _fileStream.Seek(0, SeekOrigin.End);
            }
        }
    }

    /// <summary>写入 WAL 记录（头部+负载分离）</summary>
    /// <param name="record">WAL 记录头</param>
    /// <param name="data">负载数据，可为空</param>
    /// <returns>分配的 LSN</returns>
    public UInt64 Write(WalRecord record, Byte[]? data = null)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WalWriter));

            if (_fileStream == null)
                throw new InvalidOperationException("WAL not opened");

            // 分配 LSN
            record.Lsn = _nextLsn++;
            record.Timestamp = DateTime.UtcNow.Ticks;
            record.DataLength = data?.Length ?? 0;

            // 使用 SpanWriter 流模式写入长度前缀和头部，缓冲区满时自动刷入流
            var totalLength = WalRecord.HeaderSize + record.DataLength;
            Span<Byte> headerBuf = stackalloc Byte[256];
            var writer = new SpanWriter(headerBuf, _fileStream);
            writer.Write(totalLength);
            record.Write(ref writer);
            writer.Flush();

            // 写入负载数据
            if (data != null && data.Length > 0)
                _fileStream.Write(data, 0, data.Length);

            // 根据模式刷盘
            if (_mode == WalMode.Full)
            {
                _fileStream.Flush(true);
                _lastFlush = DateTime.UtcNow;
            }
            else if (_mode == WalMode.Normal)
            {
                // 异步模式，每秒刷一次
                if ((DateTime.UtcNow - _lastFlush).TotalSeconds >= 1)
                {
                    _fileStream.Flush(true);
                    _lastFlush = DateTime.UtcNow;
                }
            }
            // WalMode.None 不刷盘

            return record.Lsn;
        }
    }

    /// <summary>强制刷新到磁盘</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_fileStream != null)
            {
                _fileStream.Flush(true);
                _lastFlush = DateTime.UtcNow;
            }
        }
    }

    /// <summary>截断 WAL（在检查点之后）</summary>
    public void Truncate(UInt64 checkpointLsn)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WalWriter));

            if (_fileStream == null)
                throw new InvalidOperationException("WAL not opened");

            // 关闭当前文件
            _fileStream.Dispose();

            // 创建备份
            var backupPath = _walPath + ".bak";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            File.Move(_walPath, backupPath);

            // 重新创建 WAL 文件
            _fileStream = new FileStream(_walPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            _nextLsn = checkpointLsn + 1;

            // 删除备份
            File.Delete(backupPath);
        }
    }

    /// <summary>扫描 WAL 文件以找到最大 LSN</summary>
    /// <remarks>使用 SpanReader 流模式，仅读取每条记录的头部（37 字节），跳过负载数据，避免大缓冲区分配</remarks>
    private UInt64 ScanWalForMaxLsn()
    {
        if (_fileStream == null || _fileStream.Length == 0)
            return 0;

        UInt64 maxLsn = 0;
        _fileStream.Seek(0, SeekOrigin.Begin);

        while (_fileStream.Position < _fileStream.Length)
        {
            try
            {
                var recordStart = _fileStream.Position;

                // 通过 SpanReader 流模式按需读取，内部缓冲区仅 256 字节
                var reader = new SpanReader(_fileStream, bufferSize: 256);

                // 读取 4 字节长度前缀
                var totalLength = reader.ReadInt32();
                if (totalLength <= 0 || totalLength > 1024 * 1024) break;
                if (totalLength < WalRecord.HeaderSize) break;

                // 仅读取头部获取 LSN，不读取负载数据
                var record = new WalRecord();
                record.Read(ref reader);

                if (record.Lsn > maxLsn)
                    maxLsn = record.Lsn;

                // 直接定位到下一条记录，跳过负载数据
                _fileStream.Position = recordStart + 4 + totalLength;
            }
            catch
            {
                // 忽略损坏的记录
                break;
            }
        }

        return maxLsn;
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;

            Flush();
            _fileStream?.Dispose();
            _disposed = true;
        }
    }
}
