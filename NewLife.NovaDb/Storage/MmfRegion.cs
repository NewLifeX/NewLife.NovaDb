using System.IO.MemoryMappedFiles;
using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Storage;

/// <summary>内存映射区域：单一映射视图按需扩容，提供按偏移的定长读写</summary>
/// <remarks>
/// 索引结构按"文件偏移 + 长度"互相引用，读写都直接落在映射视图上：
/// - 视图覆盖整份文件；容量不足时先扩展文件再重建视图
/// - 读取不产生托管对象，写入不经过文件流
/// - Windows 下存在映射视图时不能缩短文件，截断/替换前必须先 <see cref="Reset"/>
/// 并发由调用方保证（NovaTable 在表锁内访问）。
/// </remarks>
internal sealed class MmfRegion : IDisposable
{
    /// <summary>最小映射容量，空文件无法直接映射</summary>
    public const Int32 MinCapacity = 64 * 1024;

    /// <summary>最小扩容增量</summary>
    public const Int64 MinGrowSize = 1024 * 1024;

    private readonly String _path;
    private readonly Boolean _readOnly;
    private readonly Object _sync = new();

    private FileStream? _stream;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private Int64 _length;
    private Int64 _viewCapacity;
    private Boolean _disposed;

    /// <summary>文件路径</summary>
    public String FilePath => _path;

    /// <summary>是否只读</summary>
    public Boolean ReadOnly => _readOnly;

    /// <summary>当前文件长度</summary>
    public Int64 Length => _length;

    /// <summary>当前映射视图容量</summary>
    public Int64 ViewCapacity => _viewCapacity;

    /// <summary>实例化映射区域</summary>
    /// <param name="path">文件路径</param>
    /// <param name="readOnly">是否只读打开</param>
    public MmfRegion(String path, Boolean readOnly = false)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _readOnly = readOnly;
    }

    /// <summary>打开文件并建立映射</summary>
    /// <param name="createLength">新建文件时的初始长度，至少 <see cref="MinCapacity"/></param>
    /// <param name="create">文件不存在时是否创建</param>
    /// <returns>文件是否新建</returns>
    public Boolean Open(Int64 createLength = MinCapacity, Boolean create = true)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfRegion));

        var exists = File.Exists(_path);
        if (!exists && (!create || _readOnly))
            throw new NovaException(ErrorCode.FileCorrupted, "Index file does not exist: {0}", _path);

        var mode = exists ? FileMode.Open : FileMode.CreateNew;
        var access = _readOnly ? FileAccess.Read : FileAccess.ReadWrite;

        // 允许运维在运行期改名移走索引文件：Windows 存在映射视图时不能删除，但带 FileShare.Delete 可以改名
        var share = FileShare.Read | FileShare.Delete;

        _stream = new FileStream(_path, mode, access, share);
        if (_stream.Length < MinCapacity && !_readOnly)
            _stream.SetLength(Math.Max(createLength, MinCapacity));

        _length = _stream.Length;
        if (_length <= 0)
            throw new NovaException(ErrorCode.FileCorrupted, "Index file is empty: {0}", _path);

        Map();

        return !exists;
    }

    /// <summary>确保视图覆盖指定区间，必要时扩容并重建映射</summary>
    /// <param name="required">需要的最小文件长度</param>
    public void EnsureCapacity(Int64 required)
    {
        if (required <= _viewCapacity) return;
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot grow index file in read-only mode: {0}", _path);

        lock (_sync)
        {
            if (required <= _viewCapacity) return;

            var grow = _length / 4;
            if (grow < MinGrowSize) grow = MinGrowSize;

            var length = required > _length + grow ? required : _length + grow;
            Remap(length);
        }
    }

    /// <summary>设置文件长度（截断或扩展）</summary>
    /// <param name="length">新长度</param>
    public void SetLength(Int64 length)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfRegion));
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot resize index file in read-only mode: {0}", _path);
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

        lock (_sync)
        {
            Remap(length);
        }
    }

    /// <summary>读取字节</summary>
    /// <param name="offset">文件偏移</param>
    /// <param name="buffer">目标缓冲区</param>
    /// <param name="index">目标起始下标</param>
    /// <param name="count">字节数</param>
    public void Read(Int64 offset, Byte[] buffer, Int32 index, Int32 count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (buffer.Length < index + count) throw new ArgumentOutOfRangeException(nameof(count));

        if (count == 0) return;

        EnsureReadRange(offset, count);
        _view!.ReadArray(offset, buffer, index, count);
    }

    /// <summary>读取字节并返回数组</summary>
    /// <param name="offset">文件偏移</param>
    /// <param name="count">字节数</param>
    /// <returns>读到的数据</returns>
    public Byte[] ReadBytes(Int64 offset, Int32 count)
    {
        var buffer = new Byte[count];
        Read(offset, buffer, 0, count);
        return buffer;
    }

    /// <summary>写入字节</summary>
    /// <param name="offset">文件偏移</param>
    /// <param name="buffer">源缓冲区</param>
    /// <param name="index">源起始下标</param>
    /// <param name="count">字节数</param>
    public void Write(Int64 offset, Byte[] buffer, Int32 index, Int32 count)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot write index file in read-only mode: {0}", _path);
        if (offset < 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (buffer.Length < index + count) throw new ArgumentOutOfRangeException(nameof(count));

        if (count == 0) return;

        EnsureCapacity(offset + count);
        _view!.WriteArray(offset, buffer, index, count);
    }

    /// <summary>读取 Int64（小端）</summary>
    /// <param name="offset">文件偏移</param>
    /// <returns>值</returns>
    public Int64 ReadInt64(Int64 offset)
    {
        EnsureReadRange(offset, sizeof(Int64));
        return _view!.ReadInt64(offset);
    }

    /// <summary>写入 Int64（小端）</summary>
    /// <param name="offset">文件偏移</param>
    /// <param name="value">值</param>
    public void WriteInt64(Int64 offset, Int64 value)
    {
        EnsureWriteRange(offset, sizeof(Int64));
        _view!.Write(offset, value);
    }

    /// <summary>读取 UInt32（小端）</summary>
    /// <param name="offset">文件偏移</param>
    /// <returns>值</returns>
    public UInt32 ReadUInt32(Int64 offset)
    {
        EnsureReadRange(offset, sizeof(UInt32));
        return _view!.ReadUInt32(offset);
    }

    /// <summary>读取 Int32（小端）</summary>
    /// <param name="offset">文件偏移</param>
    /// <returns>值</returns>
    public Int32 ReadInt32(Int64 offset)
    {
        EnsureReadRange(offset, sizeof(Int32));
        return _view!.ReadInt32(offset);
    }

    /// <summary>写入 Int32（小端）</summary>
    /// <param name="offset">文件偏移</param>
    /// <param name="value">值</param>
    public void WriteInt32(Int64 offset, Int32 value)
    {
        EnsureWriteRange(offset, sizeof(Int32));
        _view!.Write(offset, value);
    }

    /// <summary>读取单字节</summary>
    /// <param name="offset">文件偏移</param>
    /// <returns>值</returns>
    public Byte ReadByte(Int64 offset)
    {
        EnsureReadRange(offset, 1);
        return _view!.ReadByte(offset);
    }

    /// <summary>写入单字节</summary>
    /// <param name="offset">文件偏移</param>
    /// <param name="value">值</param>
    public void WriteByte(Int64 offset, Byte value)
    {
        EnsureWriteRange(offset, 1);
        _view!.Write(offset, value);
    }

    /// <summary>把映射视图的脏页刷到磁盘</summary>
    /// <param name="syncView">是否强制回写映射视图。索引是派生数据，周期性刷盘只需保证元数据落盘，崩溃后按行日志重建尾部</param>
    public void Flush(Boolean syncView = true)
    {
        if (_disposed) return;

        if (syncView) _view?.Flush();
        _stream?.Flush();
    }

    /// <summary>丢弃映射，下次访问时按文件最新长度重建</summary>
    /// <remarks>文件被截断或整体替换后必须调用，否则旧视图与文件长度不一致</remarks>
    public void Reset()
    {
        lock (_sync)
        {
            DisposeView();
            _viewCapacity = 0;
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;

            DisposeView();
            _stream?.Dispose();
            _stream = null;
            _disposed = true;
        }
    }

    #region 辅助

    /// <summary>建立映射</summary>
    private void Map()
    {
        DisposeView();

        if (_stream == null || _stream.Length <= 0) return;

        var access = _readOnly ? MemoryMappedFileAccess.Read : MemoryMappedFileAccess.ReadWrite;
#if NET45
        _mmf = MemoryMappedFile.CreateFromFile(_stream, null, 0, access, null, HandleInheritability.None, true);
#else
        _mmf = MemoryMappedFile.CreateFromFile(_stream, null, 0, access, HandleInheritability.None, true);
#endif
        _view = _mmf.CreateViewAccessor(0, 0, access);
        _viewCapacity = _stream.Length;
        _length = _stream.Length;
    }

    /// <summary>释放映射视图</summary>
    private void DisposeView()
    {
        _view?.Dispose();
        _view = null;
        _mmf?.Dispose();
        _mmf = null;
    }

    /// <summary>重建映射并调整文件长度</summary>
    /// <param name="length">新文件长度</param>
    private void Remap(Int64 length)
    {
        if (_stream == null) throw new ObjectDisposedException(nameof(MmfRegion));

        if (length < MinCapacity) length = MinCapacity;

        DisposeView();

        if (_stream.Length != length)
        {
            _stream.SetLength(length);
            _stream.Flush();
        }

        Map();
    }

    /// <summary>确保读区间已映射</summary>
    private void EnsureReadRange(Int64 offset, Int32 count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfRegion));

        var end = offset + count;
        if (end > _viewCapacity)
            throw new NovaException(ErrorCode.FileCorrupted, "Index read [{0},{1}) exceeds mapped length {2}: {3}", offset, end, _viewCapacity, _path);
    }

    /// <summary>确保写区间已映射</summary>
    private void EnsureWriteRange(Int64 offset, Int32 count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MmfRegion));
        if (_readOnly) throw new NovaException(ErrorCode.ReadOnlyViolation, "Cannot write index file in read-only mode: {0}", _path);

        EnsureCapacity(offset + count);
    }

    #endregion
}
