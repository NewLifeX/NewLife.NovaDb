using System.Threading;

namespace NewLife.NovaDb.Core;

/// <summary>元数据读写锁，用于 DDL 与 DML/SELECT 的并发控制</summary>
/// <remarks>
/// DDL 操作（CREATE/DROP/ALTER TABLE 等）获取写锁，独占访问表元数据。
/// DML/SELECT 操作获取读锁，可并发执行，但 DDL 执行期间被阻塞。
/// 
/// 使用方式：
/// <code>
/// using var _ = _metaLock.AcquireRead();   // DML/SELECT
/// using var _ = _metaLock.AcquireWrite();  // DDL
/// </code>
/// </remarks>
public class MetadataLock : IDisposable
{
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
    private Boolean _disposed;

    /// <summary>获取读锁（DML/SELECT 用）</summary>
    /// <returns>释放时自动退出读锁的句柄</returns>
    public IDisposable AcquireRead()
    {
        _rwLock.EnterReadLock();
        return new ReadLockScope(_rwLock);
    }

    /// <summary>获取写锁（DDL 用）</summary>
    /// <returns>释放时自动退出写锁的句柄</returns>
    public IDisposable AcquireWrite()
    {
        _rwLock.EnterWriteLock();
        return new WriteLockScope(_rwLock);
    }

    /// <summary>当前是否有写锁被持有</summary>
    public Boolean IsWriteLockHeld => _rwLock.IsWriteLockHeld;

    /// <summary>当前是否有读锁被持有</summary>
    public Boolean IsReadLockHeld => _rwLock.IsReadLockHeld;

    /// <summary>当前等待读锁的线程数</summary>
    public Int32 WaitingReadCount => _rwLock.WaitingReadCount;

    /// <summary>当前等待写锁的线程数</summary>
    public Int32 WaitingWriteCount => _rwLock.WaitingWriteCount;

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _rwLock.Dispose();
        _disposed = true;
    }

    #region 内部锁作用域

    private sealed class ReadLockScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock;
        private Boolean _disposed;

        public ReadLockScope(ReaderWriterLockSlim rwLock) => _rwLock = rwLock;

        public void Dispose()
        {
            if (_disposed) return;
            _rwLock.ExitReadLock();
            _disposed = true;
        }
    }

    private sealed class WriteLockScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock;
        private Boolean _disposed;

        public WriteLockScope(ReaderWriterLockSlim rwLock) => _rwLock = rwLock;

        public void Dispose()
        {
            if (_disposed) return;
            _rwLock.ExitWriteLock();
            _disposed = true;
        }
    }

    #endregion
}
