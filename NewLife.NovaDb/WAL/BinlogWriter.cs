using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using NewLife.Buffers;
using NewLife.NovaDb.Utilities;
using NewLife.Security;

namespace NewLife.NovaDb.WAL;

/// <summary>Binlog 记录类型</summary>
public enum BinlogEventType : Byte
{
    /// <summary>INSERT 操作</summary>
    Insert = 1,

    /// <summary>UPDATE 操作</summary>
    Update = 2,

    /// <summary>DELETE 操作</summary>
    Delete = 3,

    /// <summary>DDL 操作（CREATE/DROP/ALTER TABLE 等）</summary>
    Ddl = 4,

    /// <summary>事务提交标记</summary>
    Commit = 5,

    /// <summary>文件轮转标记</summary>
    Rotate = 6
}

/// <summary>Binlog 事件</summary>
public class BinlogEvent
{
    /// <summary>事件序号（自增）</summary>
    public Int64 Position { get; set; }

    /// <summary>事件类型</summary>
    public BinlogEventType EventType { get; set; }

    /// <summary>UTC 时间戳（Ticks）</summary>
    public Int64 Timestamp { get; set; }

    /// <summary>数据库名</summary>
    public String Database { get; set; } = "";

    /// <summary>SQL 文本</summary>
    public String Sql { get; set; } = "";

    /// <summary>受影响行数</summary>
    public Int32 AffectedRows { get; set; }
}

/// <summary>Binlog 写入器，记录已提交的 SQL 变更用于复制与审计</summary>
/// <remarks>
/// 记录格式：
/// [RecordLength: 4B] [EventType: 1B] [Timestamp: 8B] [DbNameLen: 4B] [DbName: UTF-8]
/// [SqlLen: 4B] [Sql: UTF-8] [AffectedRows: 4B] [Checksum: 4B]
/// 
/// 文件头：32 字节，魔数 "NBLG" + Version(1B) + 保留
/// </remarks>
public class BinlogWriter : IDisposable
{
    private static readonly Byte[] BinlogMagic = [(Byte)'N', (Byte)'B', (Byte)'L', (Byte)'G'];
    private const Int32 HeaderSize = 32;

    private readonly String _basePath;
    private readonly String _database;
    private readonly Int64 _maxFileSize;
    private FileStream? _stream;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private Int64 _position;
    private Int32 _fileIndex;
    private Boolean _disposed;

    /// <summary>当前文件索引</summary>
    public Int32 FileIndex => _fileIndex;

    /// <summary>当前写入位置（事件序号）</summary>
    public Int64 Position => _position;

    /// <summary>Binlog 是否已启用</summary>
    public Boolean Enabled { get; set; } = true;

    /// <summary>创建 Binlog 写入器</summary>
    /// <param name="basePath">Binlog 文件存储目录</param>
    /// <param name="database">数据库名称</param>
    /// <param name="maxFileSize">单个 Binlog 文件最大大小（字节），默认 64MB</param>
    public BinlogWriter(String basePath, String database, Int64 maxFileSize = 64 * 1024 * 1024)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _maxFileSize = maxFileSize;

        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);

        // 查找最新的 Binlog 文件
        _fileIndex = FindLatestFileIndex();
        OpenBinlogFile();
    }

    #region 文件管理

    /// <summary>获取 Binlog 文件路径</summary>
    /// <param name="index">文件索引</param>
    private String GetFilePath(Int32 index) => Path.Combine(_basePath, $"binlog.{index:D6}");

    /// <summary>查找最新的 Binlog 文件索引</summary>
    private Int32 FindLatestFileIndex()
    {
        var maxIndex = 0;
        var pattern = "binlog.*";

        if (Directory.Exists(_basePath))
        {
            foreach (var file in Directory.GetFiles(_basePath, pattern))
            {
                var name = Path.GetFileName(file);
                var dot = name.LastIndexOf('.');
                if (dot >= 0)
                {
                    var indexStr = name.Substring(dot + 1);
                    if (Int32.TryParse(indexStr, out var idx) && idx > maxIndex)
                        maxIndex = idx;
                }
            }
        }

        return maxIndex > 0 ? maxIndex : 1;
    }

    /// <summary>打开或创建 Binlog 文件</summary>
    private void OpenBinlogFile()
    {
        var path = GetFilePath(_fileIndex);
        var isNew = !File.Exists(path) || new FileInfo(path).Length < HeaderSize;

        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        if (isNew)
        {
            WriteHeader();
        }
        else
        {
            ValidateHeader();
            // 定位到文件末尾
            _stream.Position = _stream.Length;
        }
    }

    /// <summary>写入文件头</summary>
    private void WriteHeader()
    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Byte> header = stackalloc Byte[HeaderSize];
        BinlogMagic.AsSpan().CopyTo(header.Slice(0, 4));
        header[4] = 1; // Version

        // 写入文件索引
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), _fileIndex);

        _stream!.Position = 0;
        _stream.Write(header);
        _stream.Flush();
#else
        var header = new Byte[HeaderSize];
        BinlogMagic.AsSpan().CopyTo(header.AsSpan(0, 4));
        header[4] = 1; // Version

        // 写入文件索引
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), _fileIndex);

        _stream!.Position = 0;
        _stream.Write(header, 0, header.Length);
        _stream.Flush();
#endif
    }

    /// <summary>校验文件头</summary>
    private void ValidateHeader()
    {
        if (_stream!.Length < HeaderSize) return;

        _stream.Position = 0;
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Byte> header = stackalloc Byte[HeaderSize];
        if (_stream.Read(header) < HeaderSize) return;
#else
        var header = new Byte[HeaderSize];
        if (_stream.Read(header, 0, header.Length) < HeaderSize) return;
#endif
        if (header[0] != BinlogMagic[0] || header[1] != BinlogMagic[1] ||
            header[2] != BinlogMagic[2] || header[3] != BinlogMagic[3])
            throw new InvalidOperationException("Invalid Binlog file header");
    }

    /// <summary>检查是否需要轮转文件</summary>
    private void RotateIfNeeded()
    {
        if (_stream == null || _stream.Length < _maxFileSize) return;

        // 写入轮转标记
        WriteEvent(BinlogEventType.Rotate, "", 0);

        // 关闭旧文件，打开新文件
        _stream.Dispose();
        _fileIndex++;
        OpenBinlogFile();
    }

    #endregion

    #region 写入

    /// <summary>写入一条 Binlog 事件</summary>
    /// <param name="eventType">事件类型</param>
    /// <param name="sql">SQL 文本</param>
    /// <param name="affectedRows">受影响行数</param>
    public void Write(BinlogEventType eventType, String sql, Int32 affectedRows = 0)
    {
        if (!Enabled || _disposed) return;

        lock (_lock)
        {
            RotateIfNeeded();
            WriteEvent(eventType, sql, affectedRows);
        }
    }

    /// <summary>写入事件到文件</summary>
    private void WriteEvent(BinlogEventType eventType, String sql, Int32 affectedRows)
    {
        if (_stream == null) return;

        sql ??= String.Empty;

        var encoding = Encoding.UTF8;

        // 先构造 Data 部分（不含 RecordLength 和 checksum）
        using var dataWriter = new PooledBufferWriter(initialCapacity: 1024);

        dataWriter.WriteByte((Byte)eventType);
        dataWriter.WriteInt64(DateTime.UtcNow.Ticks);

        // 数据库名
        var dbByteLength = encoding.GetByteCount(_database);
        dataWriter.WriteInt32(dbByteLength);
        var dbBuffer = dataWriter.GetWritableSpan(dbByteLength);
        encoding.GetBytes(_database, dbBuffer);

        // SQL 文本
        var sqlByteLength = encoding.GetByteCount(sql);
        dataWriter.WriteInt32(sqlByteLength);
        var sqlBuffer = dataWriter.GetWritableSpan(sqlByteLength);
        encoding.GetBytes(sql, sqlBuffer);

        dataWriter.WriteInt32(affectedRows);

        // 计算 CRC32
        var dataLen = dataWriter.WrittenCount;
        var checksum = Crc32.Compute(dataWriter.Buffer.AsSpan(0, dataLen));

        // 写入记录：[RecordLength: 4B] [Data] [Checksum: 4B]
        var recordLength = dataLen + 4;
        var totalLen = checked(4 + dataLen + 4);

        using var recordWriter = new PooledBufferWriter(initialCapacity: totalLen);

        BinaryPrimitives.WriteInt32LittleEndian(recordWriter.GetWritableSpan(4), recordLength);

        // 拷贝 Data（一次）
        recordWriter.WriteBytes(dataWriter.Buffer, 0, dataLen);

        BinaryPrimitives.WriteUInt32LittleEndian(recordWriter.GetWritableSpan(4), checksum);

        _stream.Position = _stream.Length;
        _stream.Write(recordWriter.Buffer, 0, recordWriter.WrittenCount);
        _stream.Flush();

        _position++;
    }

    #endregion

    #region 读取

    /// <summary>获取所有 Binlog 文件列表</summary>
    /// <returns>文件信息列表（文件名, 大小, 事件数）</returns>
    public List<(String FileName, Int64 Size)> ListFiles()
    {
        var result = new List<(String, Int64)>();

        if (!Directory.Exists(_basePath)) return result;

        var files = Directory.GetFiles(_basePath, "binlog.*");
        Array.Sort(files, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            result.Add((Path.GetFileName(file), info.Length));
        }

        return result;
    }

    /// <summary>清理指定索引之前的 Binlog 文件</summary>
    /// <param name="beforeIndex">清理此索引之前的文件（不含此索引）</param>
    /// <returns>清理的文件数量</returns>
    public Int32 Purge(Int32 beforeIndex)
    {
        var count = 0;

        for (var i = 1; i < beforeIndex; i++)
        {
            var path = GetFilePath(i);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    count++;
                }
                catch
                {
                    // 忽略文件删除失败
                }
            }
        }

        return count;
    }

    /// <summary>读取指定文件中的所有事件</summary>
    /// <param name="fileIndex">文件索引</param>
    /// <returns>事件列表</returns>
    public List<BinlogEvent> ReadEvents(Int32 fileIndex)
    {
        var events = new List<BinlogEvent>();
        var path = GetFilePath(fileIndex);
        if (!File.Exists(path)) return events;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < HeaderSize) return events;

        fs.Position = HeaderSize;
        var pos = 0L;

        // 复用长度前缀缓冲区
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
        Span<Byte> lenBuf = stackalloc Byte[4];
#else
        var lenBuf = new Byte[4];
#endif
        // 复用记录体缓冲区
        var bodyBuf = ArrayPool<Byte>.Shared.Rent(4096);
        try
        {
            while (fs.Position < fs.Length)
            {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
                if (fs.Read(lenBuf) < 4) break;
                var recordLength = BitConverter.ToInt32(lenBuf);
#else
                if (fs.Read(lenBuf, 0, 4) < 4) break;
                var recordLength = BitConverter.ToInt32(lenBuf, 0);
#endif
                if (recordLength < 5) break;

                // 确保缓冲区足够大
                if (bodyBuf.Length < recordLength)
                {
                    ArrayPool<Byte>.Shared.Return(bodyBuf);
                    bodyBuf = ArrayPool<Byte>.Shared.Rent(recordLength);
                }

                if (fs.Read(bodyBuf, 0, recordLength) < recordLength) break;

                var dataLength = recordLength - 4;
                if (dataLength < 1) break;

                // 校验 CRC32
                var expectedChecksum = BitConverter.ToUInt32(bodyBuf, dataLength);
                var actualChecksum = Crc32.Compute(bodyBuf, 0, dataLength);
                if (expectedChecksum != actualChecksum) continue;

                // 使用 SpanReader 解析事件，避免 MemoryStream + BinaryReader 分配
                var reader = new SpanReader(bodyBuf, 0, dataLength);

                var evt = new BinlogEvent
                {
                    Position = pos++,
                    EventType = (BinlogEventType)reader.ReadByte(),
                    Timestamp = reader.ReadInt64()
                };

                var dbLen = reader.ReadInt32();
                evt.Database = Encoding.UTF8.GetString(bodyBuf, reader.Position, dbLen);
                reader.Advance(dbLen);

                var sqlLen = reader.ReadInt32();
                evt.Sql = Encoding.UTF8.GetString(bodyBuf, reader.Position, sqlLen);
                reader.Advance(sqlLen);

                evt.AffectedRows = reader.ReadInt32();

                events.Add(evt);
            }
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(bodyBuf);
        }

        return events;
    }

    #endregion

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
        }

        _disposed = true;
    }
}
