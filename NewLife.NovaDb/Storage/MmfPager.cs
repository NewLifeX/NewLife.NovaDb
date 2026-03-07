using System.IO.MemoryMappedFiles;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.Security;

namespace NewLife.NovaDb.Storage;

/// <summary>基于内存映射文件的分页访问器</summary>
/// <remarks>
/// 提供文件级别的分页读写能力，支持：
/// - 自动写入/验证 FileHeader
/// - 按 PageId 随机读写页面
/// - 可选的 CRC32 校验和验证
/// - 文件按需扩展
/// 页面偏移 = FileHeader(32B) + PageId * PageSize
/// </remarks>
public class MmfPager : IDisposable
{
    private readonly String _filePath;
    private readonly Int32 _pageSize;
    private readonly Boolean _enableChecksum;
    private FileStream? _fileStream;
    private MemoryMappedFile? _mmf;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Boolean _disposed;

    /// <summary>文件路径</summary>
    public String FilePath => _filePath;

    /// <summary>页大小（字节）</summary>
    public Int32 PageSize => _pageSize;

    /// <summary>当前页数量</summary>
    public UInt64 PageCount
    {
        get
        {
            lock (_lock)
            {
                if (_fileStream == null) return 0;
                var dataLen = _fileStream.Length - FileHeader.HeaderSize;
                if (dataLen <= 0) return 0;
                return (UInt64)(dataLen / _pageSize);
            }
        }
    }

    /// <summary>实例化分页访问器</summary>
    /// <param name="filePath">数据文件路径</param>
    /// <param name="pageSize">页大小（字节）</param>
    /// <param name="enableChecksum">是否启用页校验和</param>
    public MmfPager(String filePath, Int32 pageSize, Boolean enableChecksum = true)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be positive");

        _pageSize = pageSize;
        _enableChecksum = enableChecksum;
    }

    /// <summary>打开或创建数据文件</summary>
    /// <param name="header">新文件时写入的文件头（已有文件则忽略）</param>
    /// <exception cref="ObjectDisposedException">已释放时抛出</exception>
    /// <exception cref="NovaException">PageSize 与文件不匹配时抛出</exception>
    public void Open(FileHeader? header = null)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MmfPager));

            var isNewFile = !File.Exists(_filePath);

            _fileStream = new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

            if (isNewFile && header != null)
            {
                // 新文件写入文件头
                using var pk = header.ToPacket();
                if (pk.TryGetArray(out var segment))
                    _fileStream.Write(segment.Array!, segment.Offset, segment.Count);
                _fileStream.Flush();
            }
            else if (!isNewFile)
            {
                // 已有文件验证文件头
                var headerBytes = new Byte[FileHeader.HeaderSize];
                var bytesRead = _fileStream.Read(headerBytes, 0, FileHeader.HeaderSize);
                if (bytesRead < FileHeader.HeaderSize)
                    throw new NovaException(ErrorCode.FileCorrupted, $"File too short, cannot read header: {_filePath}");

                var fileHeader = FileHeader.Read(new ArrayPacket(headerBytes));

                if (fileHeader.PageSize != _pageSize)
                    throw new NovaException(ErrorCode.IncompatibleFileFormat, $"Page size mismatch: file={fileHeader.PageSize}, expected={_pageSize}");
            }

            // 创建内存映射文件（leaveOpen=true，不让 MMF 关闭 FileStream）
            if (_fileStream.Length > 0)
            {
#if NET45
                _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0,
                    MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, true);
#else
                _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
#endif
            }
        }
    }

    /// <summary>读取指定页面</summary>
    /// <param name="pageId">页 ID（从 0 开始）</param>
    /// <returns>页面数据（长度等于 PageSize）</returns>
    /// <exception cref="ObjectDisposedException">已释放时抛出</exception>
    /// <exception cref="InvalidOperationException">未打开时抛出</exception>
    /// <exception cref="ArgumentOutOfRangeException">页 ID 超出范围时抛出</exception>
    /// <exception cref="NovaException">校验和不匹配时抛出</exception>
    public Byte[] ReadPage(UInt64 pageId)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MmfPager));

            if (_fileStream == null)
                throw new InvalidOperationException("Pager not opened");

            // 偏移 = FileHeader(32B) + PageId * PageSize
            var offset = FileHeader.HeaderSize + (Int64)(pageId * (UInt64)_pageSize);
            if (offset + _pageSize > _fileStream.Length)
                throw new ArgumentOutOfRangeException(nameof(pageId), $"Page {pageId} is out of range");

            var buffer = new Byte[_pageSize];

            // 优先使用 MMF 读取（零拷贝）
            if (_mmf != null)
            {
                using var accessor = _mmf.CreateViewAccessor(offset, _pageSize, MemoryMappedFileAccess.Read);
                accessor.ReadArray(0, buffer, 0, _pageSize);
            }
            else
            {
                _fileStream.Seek(offset, SeekOrigin.Begin);
                var bytesRead = _fileStream.Read(buffer, 0, _pageSize);
                if (bytesRead != _pageSize)
                    throw new IOException($"Failed to read complete page: expected {_pageSize}, got {bytesRead}");
            }

            // 校验和验证
            if (_enableChecksum)
            {
                var pageHeader = PageHeader.Read(new ArrayPacket(buffer, 0, PageHeader.HeaderSize));
                if (pageHeader.DataLength > 0)
                {
                    var computedChecksum = ComputeChecksum(buffer, PageHeader.HeaderSize, pageHeader.DataLength);
                    if (pageHeader.Checksum != computedChecksum)
                        throw new NovaException(ErrorCode.ChecksumFailed, $"Checksum mismatch for page {pageId}");
                }
            }

            return buffer;
        }
    }

    /// <summary>写入指定页面</summary>
    /// <param name="pageId">页 ID（从 0 开始）</param>
    /// <param name="data">页面数据（长度必须等于 PageSize）</param>
    /// <exception cref="ArgumentException">数据长度不匹配时抛出</exception>
    /// <exception cref="ObjectDisposedException">已释放时抛出</exception>
    /// <exception cref="InvalidOperationException">未打开时抛出</exception>
    public void WritePage(UInt64 pageId, Byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (data.Length != _pageSize)
            throw new ArgumentException($"Page data must be exactly {_pageSize} bytes, got {data.Length}", nameof(data));

        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MmfPager));

            if (_fileStream == null)
                throw new InvalidOperationException("Pager not opened");

            var offset = FileHeader.HeaderSize + (Int64)(pageId * (UInt64)_pageSize);

            // 计算并设置校验和
            if (_enableChecksum)
            {
                var pageHeader = PageHeader.Read(new ArrayPacket(data, 0, PageHeader.HeaderSize));
                var checksum = ComputeChecksum(data, PageHeader.HeaderSize, pageHeader.DataLength);
                pageHeader.Checksum = checksum;

                using var headerPk = pageHeader.ToPacket();
                if (headerPk.TryGetArray(out var headerSeg))
                    Buffer.BlockCopy(headerSeg.Array!, headerSeg.Offset, data, 0, PageHeader.HeaderSize);
            }

            // 按需扩展文件并重建 MMF
            var needExtend = offset + _pageSize > _fileStream.Length;
            if (needExtend)
            {
                // 先释放旧 MMF，再扩展文件
                _mmf?.Dispose();
                _mmf = null;

                _fileStream.SetLength(offset + _pageSize);
            }

            // 优先使用 MMF 写入
            if (_mmf != null)
            {
                using var accessor = _mmf.CreateViewAccessor(offset, _pageSize, MemoryMappedFileAccess.ReadWrite);
                accessor.WriteArray(0, data, 0, _pageSize);
            }
            else
            {
                _fileStream.Seek(offset, SeekOrigin.Begin);
                _fileStream.Write(data, 0, _pageSize);
                _fileStream.Flush();
            }

            // 扩展后重建 MMF
            if (needExtend)
                RebuildMmf();
        }
    }

    /// <summary>重建内存映射文件</summary>
    private void RebuildMmf()
    {
        if (_fileStream == null || _fileStream.Length <= 0) return;

        _mmf?.Dispose();
#if NET45
        _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0,
            MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, true);
#else
        _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0,
            MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
#endif
    }

    /// <summary>刷新缓冲区到磁盘</summary>
    public void Flush()
    {
        lock (_lock)
        {
            _fileStream?.Flush(true);
        }
    }

    /// <summary>计算页数据校验和（CRC32，与 FileHeader 保持一致）</summary>
    /// <param name="buffer">数据缓冲区</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="length">数据长度</param>
    /// <returns>CRC32 校验和</returns>
    private static UInt32 ComputeChecksum(Byte[] buffer, Int32 offset, UInt32 length)
    {
        var len = (Int32)Math.Min(length, buffer.Length - offset);
        if (len <= 0) return 0;

#if NET45
        var data = new Byte[len];
        Buffer.BlockCopy(buffer, offset, data, 0, len);
        return Crc32.Compute(data);
#else
        return Crc32.Compute(buffer.AsSpan(offset, len));
#endif
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;

            _mmf?.Dispose();
            _fileStream?.Dispose();
            _disposed = true;
        }
    }
}
