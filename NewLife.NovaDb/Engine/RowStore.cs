using System.IO.MemoryMappedFiles;
using NewLife.NovaDb.Core;

#nullable enable

namespace NewLife.NovaDb.Engine;

/// <summary>行载荷存储：写入走文件流，读取走内存映射窗口</summary>
/// <remarks>
/// 行日志是只追加的，每条 Put 记录的载荷在文件中的位置固定不变，因此行版本只需要保存（偏移，长度）。
/// 读取时按 8MB 窗口映射文件，顺序扫描时窗口几乎不重建，随机点查只映射用到的窗口。
/// 相比把每条行的 byte[] 常驻托管堆，映射窗口占用的物理内存由操作系统按需回收，行数增长时托管堆不再线性膨胀。
/// 本类不做并发保护：调用方（NovaTable）已在持有表锁的路径上串行访问。
/// </remarks>
public sealed class RowStore : IDisposable
{
    private readonly FileStream _stream;
    private readonly Func<Int64> _lengthProvider;
    private readonly Int64 _windowSize;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private Int64 _mappedCapacity;
    private Int64 _windowStart;
    private Int64 _windowLength;
    private Boolean _disposed;

    /// <summary>映射窗口大小（字节）</summary>
    public Int64 WindowSize => _windowSize;

    /// <summary>当前映射窗口的起始偏移</summary>
    public Int64 WindowStart => _windowStart;

    /// <summary>当前映射窗口长度</summary>
    public Int64 WindowLength => _windowLength;

    /// <summary>实例化行载荷存储</summary>
    /// <param name="stream">行日志文件流（只追加，读写打开）</param>
    /// <param name="lengthProvider">逻辑长度来源，为空时取文件流长度</param>
    /// <param name="windowSize">映射窗口大小，默认 8MB</param>
    public RowStore(FileStream stream, Func<Int64>? lengthProvider = null, Int32 windowSize = 8 * 1024 * 1024)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (stream.CanRead == false) throw new NovaException(ErrorCode.InvalidArgument, "Row log stream must be readable");
        if (windowSize <= 0) throw new NovaException(ErrorCode.InvalidArgument, "Row store window size must be positive");

        _lengthProvider = lengthProvider ?? (() => stream.Length);
        _windowSize = windowSize;
    }

    /// <summary>读取载荷</summary>
    /// <param name="offset">载荷在文件中的绝对偏移</param>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="count">读取字节数</param>
    public void Read(Int64 offset, Byte[] buffer, Int32 count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset <= 0) throw new NovaException(ErrorCode.FileCorrupted, "Invalid row payload offset {0}", offset);
        if (count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));

        EnsureWindow(offset, count);
        _view!.ReadArray(offset - _windowStart, buffer, 0, count);
    }

    /// <summary>读取载荷并返回独立数组</summary>
    /// <param name="offset">载荷在文件中的绝对偏移</param>
    /// <param name="count">读取字节数</param>
    /// <returns>载荷数据</returns>
    public Byte[] Read(Int64 offset, Int32 count)
    {
        var buffer = new Byte[count];
        Read(offset, buffer, count);
        return buffer;
    }

    /// <summary>确保映射窗口覆盖指定区间</summary>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">长度</param>
    private void EnsureWindow(Int64 offset, Int32 count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RowStore));

        var end = offset + count;
        if (_view != null && offset >= _windowStart && end <= _windowStart + _windowLength) return;

        if (end > _lengthProvider())
            throw new NovaException(ErrorCode.FileCorrupted, "Row payload [{0},{1}) is beyond row log length {2}", offset, end, _lengthProvider());

        // 缓冲写入可能还没落到文件，映射前先刷新一次（顺序扫描时不会走到这里）
        var physical = _stream.Length;
        if (end > physical)
        {
            _stream.Flush();
            physical = _stream.Length;
            if (end > physical)
                throw new NovaException(ErrorCode.FileCorrupted, "Row payload [{0},{1}) is beyond row log file length {2}", offset, end, physical);
        }

        // 文件增长后原有映射容量不足，必须重建映射对象
        if (_mmf == null || end > _mappedCapacity)
        {
            _view?.Dispose();
            _mmf?.Dispose();
            _view = null;
#if NET45
            _mmf = MemoryMappedFile.CreateFromFile(_stream, null, 0, MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, true);
#else
            _mmf = MemoryMappedFile.CreateFromFile(_stream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
#endif
            _mappedCapacity = physical;
        }
        else
        {
            _view?.Dispose();
            _view = null;
        }

        // 窗口按窗口大小对齐，顺序扫描时同一窗口可服务多条记录
        var start = offset / _windowSize * _windowSize;
        if (start < 0) start = 0;
        var size = Math.Min(_windowSize, _mappedCapacity - start);
        if (size < end - start) size = end - start;

        _view = _mmf.CreateViewAccessor(start, size, MemoryMappedFileAccess.Read);
        _windowStart = start;
        _windowLength = size;
    }

    /// <summary>释放映射资源</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _disposed = true;
    }

    /// <summary>丢弃当前映射，下次读取时按文件最新长度重新映射</summary>
    /// <remarks>文件被截断或整体替换后必须调用，否则旧映射的容量与文件实际长度不一致</remarks>
    public void Reset()
    {
        if (_disposed) return;

        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _mappedCapacity = 0;
        _windowStart = 0;
        _windowLength = 0;
    }
}
