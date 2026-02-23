using System.Text;
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
    private readonly Object _lock = new();
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
        var header = new Byte[HeaderSize];
        Array.Copy(BinlogMagic, 0, header, 0, 4);
        header[4] = 1; // Version

        // 写入文件索引
        var indexBytes = BitConverter.GetBytes(_fileIndex);
        Array.Copy(indexBytes, 0, header, 8, 4);

        _stream!.Position = 0;
        _stream.Write(header, 0, header.Length);
        _stream.Flush();
    }

    /// <summary>校验文件头</summary>
    private void ValidateHeader()
    {
        if (_stream!.Length < HeaderSize) return;

        _stream.Position = 0;
        var header = new Byte[HeaderSize];
        if (_stream.Read(header, 0, header.Length) < HeaderSize) return;

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

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write((Byte)eventType);
        bw.Write(DateTime.UtcNow.Ticks);

        // 数据库名
        var dbBytes = Encoding.UTF8.GetBytes(_database);
        bw.Write(dbBytes.Length);
        bw.Write(dbBytes);

        // SQL 文本
        var sqlBytes = Encoding.UTF8.GetBytes(sql ?? "");
        bw.Write(sqlBytes.Length);
        bw.Write(sqlBytes);

        bw.Write(affectedRows);

        var data = ms.ToArray();

        // 计算 CRC32
        var checksum = Crc32.Compute(data, 0, data.Length);

        // 写入记录：[RecordLength: 4B] [Data] [Checksum: 4B]
        var recordLength = data.Length + 4;

        _stream.Position = _stream.Length;

        var lenBuf = BitConverter.GetBytes(recordLength);
        _stream.Write(lenBuf, 0, 4);
        _stream.Write(data, 0, data.Length);

        var csumBuf = BitConverter.GetBytes(checksum);
        _stream.Write(csumBuf, 0, 4);
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

        while (fs.Position < fs.Length)
        {
            var lenBuf = new Byte[4];
            if (fs.Read(lenBuf, 0, 4) < 4) break;
            var recordLength = BitConverter.ToInt32(lenBuf, 0);
            if (recordLength < 5) break;

            var body = new Byte[recordLength];
            if (fs.Read(body, 0, recordLength) < recordLength) break;

            var dataLength = recordLength - 4;
            if (dataLength < 1) break;

            // 校验 CRC32
            var expectedChecksum = BitConverter.ToUInt32(body, dataLength);
            var actualChecksum = Crc32.Compute(body, 0, dataLength);
            if (expectedChecksum != actualChecksum) continue;

            // 解析事件
            using var ms = new MemoryStream(body, 0, dataLength);
            using var br = new BinaryReader(ms);

            var evt = new BinlogEvent
            {
                Position = pos++,
                EventType = (BinlogEventType)br.ReadByte(),
                Timestamp = br.ReadInt64()
            };

            var dbLen = br.ReadInt32();
            evt.Database = Encoding.UTF8.GetString(br.ReadBytes(dbLen));

            var sqlLen = br.ReadInt32();
            evt.Sql = Encoding.UTF8.GetString(br.ReadBytes(sqlLen));

            evt.AffectedRows = br.ReadInt32();

            events.Add(evt);
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
