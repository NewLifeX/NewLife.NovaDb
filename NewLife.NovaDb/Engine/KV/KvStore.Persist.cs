using System.Text;
using NewLife.Security;

namespace NewLife.NovaDb.Engine.KV;

/// <summary>KvStore 数据持久化逻辑</summary>
/// <remarks>
/// 采用 Append-Only Log (AOF) 方式持久化 KV 数据。
/// 每次 Set/Add/Inc 写入一条 KvSet 记录，Delete 写入一条 KvDel 记录。
/// 启动时顺序回放所有记录，重建内存 Dictionary。
/// 
/// 记录格式：
/// [RecordLength: 4B] [RecordType: 1B] [Data: variable] [Checksum: 4B]
/// 
/// KvSet Data = [KeyLen: 4B] [Key: UTF-8] [HasExpiry: 1B] [ExpiresAt: 8B (可选)] [ValueLen: 4B] [Value]
/// KvDel Data = [KeyLen: 4B] [Key: UTF-8]
/// </remarks>
public partial class KvStore
{
    private const Byte RecordType_KvSet = 1;
    private const Byte RecordType_KvDel = 2;

    private static readonly Byte[] KvLogMagic = [(Byte)'N', (Byte)'K', (Byte)'V', (Byte)'L'];
    private const Int32 KvLogHeaderSize = 32;

    private FileStream? _kvLogStream;

    #region 文件管理

    /// <summary>获取 KV 日志文件路径</summary>
    private String GetKvLogPath() => Path.Combine(_storePath!, "kv.rlog");

    /// <summary>打开 KV 日志文件</summary>
    private void OpenKvLog()
    {
        if (String.IsNullOrEmpty(_storePath)) return;

        if (!Directory.Exists(_storePath))
            Directory.CreateDirectory(_storePath);

        var path = GetKvLogPath();
        var isNew = !File.Exists(path) || new FileInfo(path).Length < KvLogHeaderSize;

        _kvLogStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        if (isNew)
        {
            WriteKvLogHeader();
        }
        else
        {
            ValidateKvLogHeader();
            LoadFromKvLog();
        }
    }

    /// <summary>写入文件头</summary>
    private void WriteKvLogHeader()
    {
        var header = new Byte[KvLogHeaderSize];
        Array.Copy(KvLogMagic, 0, header, 0, 4);
        header[4] = 1; // Version

        _kvLogStream!.Position = 0;
        _kvLogStream.Write(header, 0, header.Length);
        _kvLogStream.Flush();
    }

    /// <summary>校验文件头</summary>
    private void ValidateKvLogHeader()
    {
        if (_kvLogStream!.Length < KvLogHeaderSize) return;

        _kvLogStream.Position = 0;
        var header = new Byte[KvLogHeaderSize];
        if (_kvLogStream.Read(header, 0, header.Length) < KvLogHeaderSize) return;

        if (header[0] != KvLogMagic[0] || header[1] != KvLogMagic[1] ||
            header[2] != KvLogMagic[2] || header[3] != KvLogMagic[3])
            throw new InvalidOperationException("Invalid KV log file header");
    }

    #endregion

    #region 持久化写入

    /// <summary>持久化 Set 记录</summary>
    /// <param name="key">键</param>
    /// <param name="value">值</param>
    /// <param name="expiresAt">过期时间</param>
    private void PersistKvSet(String key, Byte[]? value, DateTime? expiresAt)
    {
        if (_kvLogStream == null) return;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Key
        var keyBytes = Encoding.UTF8.GetBytes(key);
        bw.Write(keyBytes.Length);
        bw.Write(keyBytes);

        // 过期时间
        if (expiresAt.HasValue)
        {
            bw.Write((Byte)1);
            bw.Write(expiresAt.Value.Ticks);
        }
        else
        {
            bw.Write((Byte)0);
        }

        // Value
        var valueBytes = value ?? [];
        bw.Write(valueBytes.Length);
        bw.Write(valueBytes);

        WriteKvRecord(RecordType_KvSet, ms.ToArray());
    }

    /// <summary>持久化 Delete 记录</summary>
    /// <param name="key">键</param>
    private void PersistKvDelete(String key)
    {
        if (_kvLogStream == null) return;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var keyBytes = Encoding.UTF8.GetBytes(key);
        bw.Write(keyBytes.Length);
        bw.Write(keyBytes);

        WriteKvRecord(RecordType_KvDel, ms.ToArray());
    }

    /// <summary>写入一条记录</summary>
    private void WriteKvRecord(Byte recordType, Byte[] data)
    {
        var recordLength = 1 + data.Length + 4;

        using var ms = new MemoryStream(4 + recordLength);
        using var bw = new BinaryWriter(ms);

        bw.Write(recordLength);
        bw.Write(recordType);
        bw.Write(data);

        // CRC32 校验
        var checkBuffer = new Byte[1 + data.Length];
        checkBuffer[0] = recordType;
        Array.Copy(data, 0, checkBuffer, 1, data.Length);
        var checksum = Crc32.Compute(checkBuffer, 0, checkBuffer.Length);
        bw.Write(checksum);

        var buffer = ms.ToArray();
        _kvLogStream!.Position = _kvLogStream.Length;
        _kvLogStream.Write(buffer, 0, buffer.Length);
        _kvLogStream.Flush();
    }

    #endregion

    #region 启动恢复

    /// <summary>从日志文件恢复数据</summary>
    private void LoadFromKvLog()
    {
        if (_kvLogStream == null) return;

        _kvLogStream.Position = KvLogHeaderSize;

        while (_kvLogStream.Position < _kvLogStream.Length)
        {
            var lenBuf = new Byte[4];
            if (_kvLogStream.Read(lenBuf, 0, 4) < 4) break;
            var recordLength = BitConverter.ToInt32(lenBuf, 0);
            if (recordLength < 5) break;

            var body = new Byte[recordLength];
            if (_kvLogStream.Read(body, 0, recordLength) < recordLength) break;

            var recordType = body[0];
            var dataLength = recordLength - 1 - 4;
            if (dataLength < 0) break;

            // 校验 CRC32
            var expectedChecksum = BitConverter.ToUInt32(body, recordLength - 4);
            var actualChecksum = Crc32.Compute(body, 0, 1 + dataLength);
            if (expectedChecksum != actualChecksum) continue;

            if (recordType == RecordType_KvSet && dataLength >= 4)
            {
                ReplayKvSet(body, 1, dataLength);
            }
            else if (recordType == RecordType_KvDel && dataLength >= 4)
            {
                ReplayKvDel(body, 1, dataLength);
            }
        }
    }

    /// <summary>回放 Set 记录</summary>
    private void ReplayKvSet(Byte[] body, Int32 offset, Int32 dataLength)
    {
        using var ms = new MemoryStream(body, offset, dataLength);
        using var br = new BinaryReader(ms);

        var keyLen = br.ReadInt32();
        var keyBytes = br.ReadBytes(keyLen);
        var key = Encoding.UTF8.GetString(keyBytes);

        var hasExpiry = br.ReadByte();
        DateTime? expiresAt = null;
        if (hasExpiry == 1)
            expiresAt = new DateTime(br.ReadInt64(), DateTimeKind.Utc);

        var valueLen = br.ReadInt32();
        var value = br.ReadBytes(valueLen);

        // 过期的 key 不加载
        if (expiresAt.HasValue && DateTime.UtcNow >= expiresAt.Value) return;

        _data[key] = new KvEntry
        {
            Key = key,
            Value = value,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        };
    }

    /// <summary>回放 Delete 记录</summary>
    private void ReplayKvDel(Byte[] body, Int32 offset, Int32 dataLength)
    {
        using var ms = new MemoryStream(body, offset, dataLength);
        using var br = new BinaryReader(ms);

        var keyLen = br.ReadInt32();
        var keyBytes = br.ReadBytes(keyLen);
        var key = Encoding.UTF8.GetString(keyBytes);

        _data.Remove(key);
    }

    /// <summary>压缩 KV 日志（重写仅包含存活键的日志）</summary>
    public void CompactKvLog()
    {
        if (_kvLogStream == null) return;

        lock (_lock)
        {
            _kvLogStream.SetLength(KvLogHeaderSize);
            _kvLogStream.Position = KvLogHeaderSize;

            foreach (var entry in _data.Values)
            {
                if (!entry.IsExpired())
                {
                    PersistKvSet(entry.Key, entry.Value, entry.ExpiresAt);
                }
            }

            _kvLogStream.Flush();
        }
    }

    /// <summary>关闭 KV 日志文件</summary>
    internal void CloseKvLog()
    {
        _kvLogStream?.Dispose();
        _kvLogStream = null;
    }

    #endregion
}
