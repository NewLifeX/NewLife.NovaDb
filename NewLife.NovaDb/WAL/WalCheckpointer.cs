using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.WAL;

/// <summary>WAL 检查点管理器</summary>
/// <remarks>
/// 负责定期检查 WAL 文件大小，当超过阈值时触发检查点：
/// 1. 通知上层刷新脏数据（通过 FlushCallback）
/// 2. 截断 WAL 文件
/// 
/// 支持两种触发方式：
/// - 手动调用 TryCheckpoint()
/// - 后台定时检查（通过 Start/Stop）
/// </remarks>
public class WalCheckpointer : IDisposable
{
    private readonly WalWriter _walWriter;
    private readonly String _walPath;
    private readonly Int64 _maxWalSize;
    private readonly TimeSpan _checkInterval;
    private Timer? _timer;
    private Boolean _disposed;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Int64 _lastCheckpointLsn;

    /// <summary>刷盘回调，在截断 WAL 前调用，由上层实现脏数据落盘</summary>
    public Action? FlushCallback { get; set; }

    /// <summary>检查点完成后的通知回调</summary>
    public Action<Int64>? CheckpointCallback { get; set; }

    /// <summary>上次检查点的 LSN</summary>
    public Int64 LastCheckpointLsn => Interlocked.Read(ref _lastCheckpointLsn);

    /// <summary>检查点执行次数</summary>
    public Int64 CheckpointCount { get; private set; }

    /// <summary>创建 WAL 检查点管理器</summary>
    /// <param name="walWriter">WAL 写入器</param>
    /// <param name="walPath">WAL 文件路径</param>
    /// <param name="maxWalSize">WAL 文件最大大小（字节），超过此值触发检查点，默认 64MB</param>
    /// <param name="checkInterval">后台检查间隔，默认 30 秒</param>
    public WalCheckpointer(WalWriter walWriter, String walPath, Int64 maxWalSize = 64 * 1024 * 1024, TimeSpan? checkInterval = null)
    {
        _walWriter = walWriter ?? throw new ArgumentNullException(nameof(walWriter));
        _walPath = walPath ?? throw new ArgumentNullException(nameof(walPath));
        _maxWalSize = maxWalSize > 0 ? maxWalSize : 64 * 1024 * 1024;
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>启动后台定时检查</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WalCheckpointer));
            if (_timer != null) return;

            _timer = new Timer(OnTimerTick, null, _checkInterval, _checkInterval);
        }
    }

    /// <summary>停止后台定时检查</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>尝试执行检查点（仅当 WAL 超过阈值时）</summary>
    /// <returns>是否执行了检查点</returns>
    public Boolean TryCheckpoint()
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (!ShouldCheckpoint()) return false;

            return DoCheckpoint();
        }
    }

    /// <summary>强制执行检查点（不检查阈值）</summary>
    /// <returns>是否执行成功</returns>
    public Boolean ForceCheckpoint()
    {
        lock (_lock)
        {
            if (_disposed) return false;

            return DoCheckpoint();
        }
    }

    /// <summary>检查是否需要执行检查点</summary>
    /// <returns>WAL 文件是否超过阈值</returns>
    public Boolean ShouldCheckpoint()
    {
        try
        {
            if (!File.Exists(_walPath)) return false;

            var fileInfo = new FileInfo(_walPath);
            return fileInfo.Length >= _maxWalSize;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>执行检查点</summary>
    private Boolean DoCheckpoint()
    {
        try
        {
            // 第一步：通知上层刷新脏数据到数据文件
            FlushCallback?.Invoke();

            // 第二步：刷盘 WAL
            _walWriter.Flush();

            // 第三步：记录当前 LSN
            var nextLsn = _walWriter.NextLsn;
            var checkpointLsn = nextLsn > 0 ? nextLsn - 1 : 0UL;

            // 第四步：截断 WAL 文件
            _walWriter.Truncate(checkpointLsn);

            // 更新状态
            Interlocked.Exchange(ref _lastCheckpointLsn, (Int64)checkpointLsn);
            CheckpointCount++;

            // 通知检查点完成
            CheckpointCallback?.Invoke((Int64)checkpointLsn);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>定时器回调</summary>
    private void OnTimerTick(Object? state)
    {
        TryCheckpoint();
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;

            _timer?.Dispose();
            _timer = null;
            _disposed = true;
        }
    }
}
