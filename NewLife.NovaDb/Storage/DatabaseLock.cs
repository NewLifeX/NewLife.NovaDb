using NewLife.NovaDb.Utilities;

namespace NewLife.NovaDb.Storage;

/// <summary>数据库文件锁，实现跨进程单写者协调</summary>
/// <remarks>
/// 使用 .lock 文件 + FileStream 独占模式实现：
/// - 嵌入模式下防止多进程同时写入同一数据库
/// - 获取锁时创建排他锁文件
/// - 释放锁时删除锁文件
/// - 支持只读模式跳过锁获取
/// </remarks>
public class DatabaseLock : IDisposable
{
    private readonly String _lockPath;
    private FileStream? _lockStream;
    private Boolean _disposed;

    /// <summary>是否已获取锁</summary>
    public Boolean IsLocked => _lockStream != null;

    /// <summary>锁文件路径</summary>
    public String LockPath => _lockPath;

    /// <summary>创建数据库文件锁</summary>
    /// <param name="dbPath">数据库目录路径</param>
    public DatabaseLock(String dbPath)
    {
        if (dbPath == null) throw new ArgumentNullException(nameof(dbPath));
        _lockPath = Path.Combine(dbPath, ".lock");
    }

    /// <summary>尝试获取写锁</summary>
    /// <returns>是否成功获取</returns>
    public Boolean TryAcquire()
    {
        if (_disposed) return false;
        if (_lockStream != null) return true;

        try
        {
            // 确保目录存在
            var dir = Path.GetDirectoryName(_lockPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // 以排他模式打开文件，阻止其他进程同时获取
            _lockStream = new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            // 写入进程信息
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            using (var info = $"PID={pid}, Time={DateTime.Now:yyyy-MM-dd HH:mm:ss}".ToPooledUtf8Bytes())
                _lockStream.Write(info.Buffer, 0, info.Length);
            _lockStream.Flush();

            return true;
        }
        catch (IOException)
        {
            // 其他进程已持有锁
            return false;
        }
    }

    /// <summary>获取写锁，失败则抛出异常</summary>
    /// <exception cref="Core.NovaException">无法获取锁时抛出</exception>
    public void Acquire()
    {
        if (!TryAcquire())
            throw new Core.NovaException(Core.ErrorCode.FileLocked, $"Database is locked by another process: {_lockPath}");
    }

    /// <summary>释放锁</summary>
    public void Release()
    {
        if (_lockStream == null) return;

        _lockStream.Dispose();
        _lockStream = null;

        // 尝试删除锁文件
        try
        {
            if (File.Exists(_lockPath))
                File.Delete(_lockPath);
        }
        catch
        {
            // 删除失败不影响逻辑
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;

        Release();
        _disposed = true;
    }
}
