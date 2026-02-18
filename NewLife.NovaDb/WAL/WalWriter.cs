using NewLife.Data;
using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.WAL;

/// <summary>
/// WAL 写入器
/// </summary>
public class WalWriter : IDisposable
{
    private readonly String _walPath;
    private readonly WalMode _mode;
    private FileStream? _fileStream;
    private UInt64 _nextLsn;
    private readonly Object _lock = new();
    private Boolean _disposed;
    private DateTime _lastFlush;

    /// <summary>
    /// WAL 文件路径
    /// </summary>
    public String WalPath => _walPath;

    /// <summary>
    /// WAL 模式
    /// </summary>
    public WalMode Mode => _mode;

    /// <summary>
    /// 下一个 LSN
    /// </summary>
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

    public WalWriter(String walPath, WalMode mode)
    {
        _walPath = walPath ?? throw new ArgumentNullException(nameof(walPath));
        _mode = mode;
        _nextLsn = 1;
        _lastFlush = DateTime.UtcNow;
    }

    /// <summary>
    /// 打开 WAL 文件
    /// </summary>
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

    /// <summary>
    /// 写入 WAL 记录
    /// </summary>
    public UInt64 Write(WalRecord record)
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

            // 序列化记录
            using var pk = record.ToPacket();
            pk.TryGetArray(out var segment);

            // 写入长度前缀（4 字节）
            var lengthPrefix = BitConverter.GetBytes(pk.Length);
            _fileStream.Write(lengthPrefix, 0, 4);

            // 写入记录数据
            _fileStream.Write(segment.Array!, segment.Offset, segment.Count);

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

    /// <summary>
    /// 强制刷新到磁盘
    /// </summary>
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

    /// <summary>
    /// 截断 WAL（在检查点之后）
    /// </summary>
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

    /// <summary>
    /// 扫描 WAL 文件以找到最大 LSN
    /// </summary>
    private UInt64 ScanWalForMaxLsn()
    {
        if (_fileStream == null || _fileStream.Length == 0)
        {
            return 0;
        }

        UInt64 maxLsn = 0;
        _fileStream.Seek(0, SeekOrigin.Begin);

        while (_fileStream.Position < _fileStream.Length)
        {
            try
            {
                // 读取长度前缀
                var lengthPrefix = new Byte[4];
                if (_fileStream.Read(lengthPrefix, 0, 4) != 4)
                    break;

                var length = BitConverter.ToInt32(lengthPrefix, 0);
                if (length <= 0 || length > 1024 * 1024) // 最大 1MB
                    break;

                // 读取记录数据
                var data = new Byte[length];
                if (_fileStream.Read(data, 0, length) != length)
                    break;

                var record = WalRecord.Read(new ArrayPacket(data));
                if (record.Lsn > maxLsn)
                {
                    maxLsn = record.Lsn;
                }
            }
            catch
            {
                // 忽略损坏的记录
                break;
            }
        }

        return maxLsn;
    }

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
