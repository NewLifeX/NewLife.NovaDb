using System.IO.MemoryMappedFiles;
using NewLife.NovaDb.Core;

#nullable enable

namespace NewLife.NovaDb.Engine;

/// <summary>行载荷存储：写入走文件流，读取走内存映射</summary>
/// <remarks>
/// 行日志是只追加的，每条 Put 记录的载荷在文件中的位置固定不变，因此行版本只需要保存（偏移，长度）。
/// 读取时整份文件映射一次，内存映射按页按需装入：只有真正访问过的页占用物理内存，内存紧张时由操作系统回收。
/// 因此点查与全表扫描都不会把整个文件读进内存，也不会像滑动窗口那样在随机点查时反复重建映射。
/// 本类不做并发保护：调用方（NovaTable）已在持有表锁的路径上串行访问。
/// </remarks>
public sealed class RowStore : IDisposable
{
    private readonly FileStream _stream;
    private readonly Func<Int64> _lengthProvider;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private Int64 _mappedCapacity;
    private Boolean _disposed;

    /// <summary>当前映射容量（字节），0 表示尚未映射</summary>
    public Int64 MappedCapacity => _mappedCapacity;

    /// <summary>实例化行载荷存储</summary>
    /// <param name="stream">行日志文件流（只追加，读写打开）</param>
    /// <param name="lengthProvider">逻辑长度来源，为空时取文件流长度</param>
    public RowStore(FileStream stream, Func<Int64>? lengthProvider = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (stream.CanRead == false) throw new NovaException(ErrorCode.InvalidArgument, "Row log stream must be readable");

        _lengthProvider = lengthProvider ?? (() => stream.Length);
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

        EnsureMapping(offset, count);
        _view!.ReadArray(offset, buffer, 0, count);
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

    /// <summary>确保映射覆盖指定区间</summary>
    /// <param name="offset">起始偏移</param>
    /// <param name="count">长度</param>
    private void EnsureMapping(Int64 offset, Int32 count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RowStore));

        var end = offset + count;
        var length = _lengthProvider();
        if (end > length)
            throw new NovaException(ErrorCode.FileCorrupted, "Row payload [{0},{1}) is beyond row log length {2}", offset, end, length);

        if (_view != null && end <= _mappedCapacity) return;

        // 缓冲写入可能还没落到文件，映射前先刷新一次（顺序扫描时不会走到这里）
        var physical = _stream.Length;
        if (physical < length)
        {
            _stream.Flush();
            physical = _stream.Length;
        }
        if (end > physical)
            throw new NovaException(ErrorCode.FileCorrupted, "Row payload [{0},{1}) is beyond row log file length {2}", offset, end, physical);

        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;

        // capacity 传 0 表示映射整份文件；按页按需装入，不会一次性读入内存
#if NET45
        _mmf = MemoryMappedFile.CreateFromFile(_stream, null, 0, MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, true);
#else
        _mmf = MemoryMappedFile.CreateFromFile(_stream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
#endif
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _mappedCapacity = physical;
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
    }
}
