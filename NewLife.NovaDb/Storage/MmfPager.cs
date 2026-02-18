using System.IO.MemoryMappedFiles;
using NewLife.NovaDb.Core;

namespace NewLife.NovaDb.Storage;

/// <summary>
/// 基于内存映射文件的分页访问器
/// </summary>
public class MmfPager : IDisposable
{
    private readonly String _filePath;
    private readonly Int32 _pageSize;
    private readonly Boolean _enableChecksum;
    private FileStream? _fileStream;
    private MemoryMappedFile? _mmf;
    private readonly Object _lock = new();
    private Boolean _disposed;

    /// <summary>
    /// 文件路径
    /// </summary>
    public String FilePath => _filePath;

    /// <summary>
    /// 页大小
    /// </summary>
    public Int32 PageSize => _pageSize;

    /// <summary>
    /// 页数量
    /// </summary>
    public UInt64 PageCount
    {
        get
        {
            lock (_lock)
            {
                if (_fileStream == null) return 0;
                return (UInt64)(_fileStream.Length / _pageSize);
            }
        }
    }

    public MmfPager(String filePath, Int32 pageSize, Boolean enableChecksum = true)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _pageSize = pageSize;
        _enableChecksum = enableChecksum;
    }

    /// <summary>
    /// 打开或创建文件
    /// </summary>
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
                // 写入文件头
                var headerBytes = header.ToBytes();
                _fileStream.Write(headerBytes, 0, headerBytes.Length);
                _fileStream.Flush();
            }
            else if (!isNewFile)
            {
                // 验证文件头
                var headerBytes = new Byte[32];
                _fileStream.Read(headerBytes, 0, 32);
                var fileHeader = FileHeader.FromBytes(headerBytes);

                if (fileHeader.PageSize != _pageSize)
                {
                    throw new NovaDbException(ErrorCode.IncompatibleFileFormat,
                        $"Page size mismatch: file={fileHeader.PageSize}, expected={_pageSize}");
                }
            }

            // 创建内存映射文件
            if (_fileStream.Length > 0)
            {
                _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            }
        }
    }

    /// <summary>
    /// 读取页
    /// </summary>
    public Byte[] ReadPage(UInt64 pageId)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MmfPager));

            if (_fileStream == null)
                throw new InvalidOperationException("Pager not opened");

            var offset = 32 + (Int64)(pageId * (UInt64)_pageSize); // 跳过文件头
            if (offset + _pageSize > _fileStream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(pageId),
                    $"Page {pageId} is out of range");
            }

            // 使用 FileStream 直接读取
            _fileStream.Seek(offset, SeekOrigin.Begin);
            var buffer = new Byte[_pageSize];
            var bytesRead = _fileStream.Read(buffer, 0, _pageSize);
            if (bytesRead != _pageSize)
            {
                throw new IOException($"Failed to read complete page: expected {_pageSize}, got {bytesRead}");
            }

            // 验证校验和
            if (_enableChecksum)
            {
                var pageHeader = PageHeader.FromBytes(buffer);
                var computedChecksum = ComputeChecksum(buffer, 32, pageHeader.DataLength);

                if (pageHeader.Checksum != computedChecksum)
                {
                    throw new NovaDbException(ErrorCode.ChecksumFailed,
                        $"Checksum mismatch for page {pageId}");
                }
            }

            return buffer;
        }
    }

    /// <summary>
    /// 写入页
    /// </summary>
    public void WritePage(UInt64 pageId, Byte[] data)
    {
        if (data == null || data.Length != _pageSize)
        {
            throw new ArgumentException($"Page data must be exactly {_pageSize} bytes", nameof(data));
        }

        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MmfPager));

            if (_fileStream == null)
                throw new InvalidOperationException("Pager not opened");

            var offset = 32 + (Int64)(pageId * (UInt64)_pageSize);

            // 计算并设置校验和
            if (_enableChecksum)
            {
                var pageHeader = PageHeader.FromBytes(data);
                var checksum = ComputeChecksum(data, 32, pageHeader.DataLength);
                pageHeader.Checksum = checksum;

                var headerBytes = pageHeader.ToBytes();
                Buffer.BlockCopy(headerBytes, 0, data, 0, 32);
            }

            // 扩展文件如果需要
            if (offset + _pageSize > _fileStream.Length)
            {
                _fileStream.SetLength(offset + _pageSize);
            }

            // 直接写入文件流（不使用MMF写入以避免重新映射问题）
            _fileStream.Seek(offset, SeekOrigin.Begin);
            _fileStream.Write(data, 0, _pageSize);
            _fileStream.Flush();
        }
    }

    /// <summary>
    /// 刷新到磁盘
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            _fileStream?.Flush(true);
        }
    }

    /// <summary>
    /// 计算校验和（CRC32 简化版）
    /// </summary>
    private UInt32 ComputeChecksum(Byte[] buffer, Int32 offset, UInt32 length)
    {
        UInt32 checksum = 0;
        for (var i = offset; i < offset + length && i < buffer.Length; i++)
        {
            checksum = (checksum << 1) ^ buffer[i];
        }
        return checksum;
    }

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
