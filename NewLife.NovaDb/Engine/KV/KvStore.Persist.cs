using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Text;
using NewLife.Buffers;
using NewLife.Data;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Storage;
using NewLife.NovaDb.Utilities;
using NewLife.Security;

namespace NewLife.NovaDb.Engine.KV;

/// <summary>KV 存储记录类型</summary>
enum KvRecordType : Byte
{
    /// <summary>设置键值对</summary>
    Set = 1,

    /// <summary>删除键</summary>
    Delete = 2,

    /// <summary>清空所有数据</summary>
    Clear = 3,
}

/// <summary>KvStore 数据持久化逻辑</summary>
/// <remarks>
/// <para>每个 KV 表对应一个 .kvd 文件，格式为 32 字节文件头 + 顺序追加的记录。</para>
/// <para>文件版本 3，ExpiresAt 始终写入，移除 Flags 字节。</para>
/// <para>Set 记录: [TotalLength: 4B] [RecordType: 1B] [KeyLen: 2B] [Key: UTF-8] [ExpiresAt: 8B] [ValueLen: 4B] [Value?] [CRC32: 4B]</para>
/// <para>Delete 记录: [TotalLength: 4B] [RecordType: 1B] [KeyLen: 2B] [Key: UTF-8] [CRC32: 4B]</para>
/// <para>Clear 记录: [TotalLength: 4B] [RecordType: 1B] [CRC32: 4B]</para>
/// <para></para>
/// <para>Bitcask 模型：内存仅保存键到文件偏移的索引（KvEntry），值留在磁盘按需读取。</para>
/// <para>启动恢复时扫描数据文件重建内存索引，不加载值数据，内存占用极低。</para>
/// <para>WAL 模式控制刷盘策略：Full=每次刷盘，Normal=定时刷盘（1秒），None=不主动刷盘。</para>
/// </remarks>
public partial class KvStore
{
    private const Int32 FileHeaderSize = FileHeader.HeaderSize;
    private const Byte FileVersion = 3;

    private FileStream? _fileStream;
    private Timer? _flushTimer;
    private volatile Boolean _needsFlush;
    private Int64 _writeCount;              // 从启动或上次 Compact 以来的写入记录数（Set/Delete/Clear）
    private volatile Boolean _compacting;   // 正在执行 Compact，避免重入

    #region 文件管理
    /// <summary>打开数据文件，恢复索引</summary>
    private void OpenDataFile()
    {
        // 确保目录存在
        var dir = Path.GetDirectoryName(_filePath);
        if (!String.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var isNew = !File.Exists(_filePath) || new FileInfo(_filePath).Length < FileHeaderSize;

        _fileStream = new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);

        if (isNew)
        {
            WriteFileHeader();
        }
        else
        {
            ValidateFileHeader();
            LoadFromFile();
        }

        // Normal 模式下启动定时刷盘
        var walMode = _options?.WalMode ?? WalMode.Normal;
        if (walMode == WalMode.Normal)
        {
            _flushTimer = new Timer(FlushTimerCallback, null, 1000, 1000);
        }
    }

    /// <summary>写入文件头（32 字节），使用统一的 FileHeader 结构</summary>
    private void WriteFileHeader()
    {
        var header = new FileHeader
        {
            Version = FileVersion,
            FileType = FileType.KvData,
            PageSize = 1,
            CreateTime = DateTime.UtcNow,
        };

        var buf = new Byte[FileHeaderSize];
        header.Write(buf);

        _fileStream!.Position = 0;
        _fileStream.Write(buf, 0, buf.Length);
        _fileStream.Flush();
    }

    /// <summary>校验文件头，使用 FileHeader 统一校验 Magic/CRC</summary>
    private void ValidateFileHeader()
    {
        if (_fileStream!.Length < FileHeaderSize) return;

        _fileStream.Position = 0;
        var buf = new Byte[FileHeaderSize];
        if (_fileStream.Read(buf, 0, buf.Length) < FileHeaderSize) return;

        var header = FileHeader.Read(buf);
        if (header.FileType != FileType.KvData)
            throw new InvalidOperationException($"无效的 KV 数据文件类型: {header.FileType}, 路径: {_filePath}");
    }

    /// <summary>关闭数据文件，释放资源</summary>
    private void CloseDataFile()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;

        if (_fileStream != null)
        {
            try { _fileStream.Flush(); } catch { }
            _fileStream.Dispose();
            _fileStream = null;
        }
    }

    /// <summary>定时刷盘回调（Normal 模式）</summary>
    private void FlushTimerCallback(Object? state)
    {
        if (!_needsFlush || _fileStream == null) return;

        try
        {
            _fileStream.Flush(true);
            _needsFlush = false;
        }
        catch { }
    }
    #endregion

    #region 持久化写入
    /// <summary>写入 Set 记录到文件，使用 ArrayPool 借出数组 + SpanWriter 填充。调用方需持有 _writeLock</summary>
    /// <param name="key">键</param>
    /// <param name="value">值，最少为空数组，不能为 null</param>
    /// <param name="expiresAt">过期时间（UTC）</param>
    /// <param name="autoFlush">是否按 WAL 模式自动刷盘</param>
    /// <returns>值在文件中的偏移（若值为空则返回 -1）</returns>
    private Int64 WriteSetRecordNoLock(String key, ReadOnlySpan<Byte> value, DateTime expiresAt, Boolean autoFlush = true)
    {
        var valueOffset = WriteSetRecordToStream(_fileStream!, key, value, expiresAt);
        _writeCount++;
        if (autoFlush) FlushByWalMode();
        return valueOffset;
    }

    /// <summary>向指定流写入 Set 记录，供普通写入和 Compact 共用</summary>
    /// <param name="target">目标文件流，写入位置定位到末尾</param>
    /// <param name="key">键</param>
    /// <param name="value">值，最少为空数组，不能为 null</param>
    /// <param name="expiresAt">过期时间（UTC）</param>
    /// <returns>值在文件中的偏移（若值为空则返回 -1）</returns>
    private static Int64 WriteSetRecordToStream(FileStream target, String key, ReadOnlySpan<Byte> value, DateTime expiresAt)
    {
        var pooledKeyBytes = _encoding.GetPooledEncodedBytes(key);
        var valueLen = value.Length;

        // Record: [TotalLength: 4B] [RecordType: 1B] [KeyLen: 2B] [Key] [ExpiresAt: 8B] [ValueLen: 4B] [Value] [CRC32: 4B]
        var totalLength = 1 + 2 + pooledKeyBytes.Length + 8 + 4 + valueLen + 4;
        var recordSize = 4 + totalLength;

        var buf = ArrayPool<Byte>.Shared.Rent(recordSize);
        try
        {
            var writer = new SpanWriter(buf, 0, recordSize);

            writer.Write(totalLength);
            writer.Write((Byte)KvRecordType.Set);
            writer.Write((UInt16)pooledKeyBytes.Length);
            writer.Write(pooledKeyBytes.AsSpan());
            writer.Write(expiresAt.Ticks);
            writer.Write(valueLen);
            writer.Write(value);

            // CRC32 覆盖 [RecordType..Value]
            var crc = Crc32.Compute(buf, 4, totalLength - 4);
            writer.Write(crc);

            var recordStart = target.Length;
            target.Position = recordStart;
            target.Write(buf, 0, recordSize);

            // 计算值在文件中的偏移: recordStart + 4(TotalLen) + 1(Type) + 2(KeyLen) + key + 8(ExpiresAt) + 4(ValueLen)
            return valueLen > 0 ? recordStart + 4 + 1 + 2 + pooledKeyBytes.Length + 8 + 4 : -1L;
        }
        finally
        {
            pooledKeyBytes.Dispose();
            ArrayPool<Byte>.Shared.Return(buf);
        }
    }

    /// <summary>写入 Delete 记录到文件，使用 ArrayPool 借出数组。调用方需持有 _writeLock</summary>
    /// <param name="key">键</param>
    private void WriteDeleteRecordNoLock(String key)
    {
        var pooledKeyBytes = _encoding.GetPooledEncodedBytes(key);

        // Delete: [TotalLength: 4B] [RecordType: 1B] [KeyLen: 2B] [Key] [CRC32: 4B]
        var totalLength = 1 + 2 + pooledKeyBytes.Length + 4;
        var recordSize = 4 + totalLength;

        var buf = ArrayPool<Byte>.Shared.Rent(recordSize);
        try
        {
            var writer = new SpanWriter(buf, 0, recordSize);

            writer.Write(totalLength);
            writer.Write((Byte)KvRecordType.Delete);
            writer.Write((UInt16)pooledKeyBytes.Length);
            writer.Write(pooledKeyBytes.AsSpan());

            var crc = Crc32.Compute(buf, 4, totalLength - 4);
            writer.Write(crc);

            _fileStream!.Position = _fileStream.Length;
            _fileStream.Write(buf, 0, recordSize);
        }
        finally
        {
            pooledKeyBytes.Dispose();
            ArrayPool<Byte>.Shared.Return(buf);
        }

        _writeCount++;
        FlushByWalMode();
    }

    /// <summary>写入 Clear 记录到文件，使用 ArrayPool 借出数组。调用方需持有 _writeLock</summary>
    private void WriteClearRecordNoLock()
    {
        // Clear: [TotalLength=5: 4B] [RecordType=Clear: 1B] [CRC32: 4B] = 9 bytes
        const Int32 recordSize = 9;
        var buf = ArrayPool<Byte>.Shared.Rent(recordSize);
        try
        {
            var writer = new SpanWriter(buf, 0, recordSize);

            writer.Write(5);
            writer.Write((Byte)KvRecordType.Clear);

            var crc = Crc32.Compute(buf, 4, 1);
            writer.Write(crc);

            _fileStream!.Position = _fileStream.Length;
            _fileStream.Write(buf, 0, recordSize);
        }
        finally
        {
            ArrayPool<Byte>.Shared.Return(buf);
        }

        _writeCount++;
        FlushByWalMode();
    }

    /// <summary>按 WAL 模式决定刷盘策略</summary>
    private void FlushByWalMode()
    {
        var walMode = _options?.WalMode ?? WalMode.Normal;
        if (walMode == WalMode.Full)
            _fileStream!.Flush(true);
        else if (walMode == WalMode.Normal)
            _needsFlush = true;
        // None 模式：不主动刷盘
    }
    #endregion

    #region 磁盘读取
    /// <summary>从磁盘读取值数据，返回池化数据包（调用方需持有 _writeLock）</summary>
    /// <param name="index">索引条目</param>
    /// <returns>池化数据包，null 表示存储的值为 null。调用方用完后需 Dispose 归还到池中</returns>
    private IOwnerPacket? ReadValueFromDiskNoLock(KvEntry index)
    {
        if (index.ValueLength <= 0) return null;

        var pk = new OwnerPacket(index.ValueLength);
        _fileStream!.Position = index.ValueOffset;
        var bytesRead = _fileStream.Read(pk.Buffer, pk.Offset, index.ValueLength);
        if (bytesRead < index.ValueLength)
            throw new InvalidOperationException("KV 数据文件损坏：无法读取完整值数据");

        return pk;
    }
    #endregion

    #region 启动恢复
    /// <summary>从数据文件恢复索引。优先使用 MemoryMappedFile 加速大文件读取</summary>
    private void LoadFromFile()
    {
        if (_fileStream == null) return;

        var fileLength = _fileStream.Length;
        if (fileLength <= FileHeaderSize) return;

        // 对大于 64KB 的文件使用 MMF 加速读取
        if (fileLength > 65536)
        {
            LoadFromFileMmf(fileLength);
        }
        else
        {
            LoadFromFileStream();
        }
    }

    /// <summary>通过 MemoryMappedFile 快速恢复大文件</summary>
    private void LoadFromFileMmf(Int64 fileLength)
    {
        // 关闭当前文件流，MMF 需要独占打开文件
        _fileStream!.Flush();
        _fileStream.Dispose();
        _fileStream = null;

        try
        {
            // 使用文件路径创建 MMF，兼容 .NET Framework 4.5+ 和 .NET Core/5+
            using var mmf = MemoryMappedFile.CreateFromFile(_filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

            var pos = (Int64)FileHeaderSize;
            while (pos + 8 < fileLength) // 至少需要 4B(长度) + 1B(类型) + 4B(CRC)
            {
                var totalLength = accessor.ReadInt32(pos);
                if (totalLength < 5 || pos + 4 + totalLength > fileLength) break;

                // 读取完整记录体
                var body = new Byte[totalLength];
                accessor.ReadArray(pos + 4, body, 0, totalLength);

                ReplayRecord(body, totalLength, pos);

                pos += 4 + totalLength;
            }
        }
        finally
        {
            // 恢复完成后重新打开文件流，供后续写入使用
            _fileStream = new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            _fileStream.Position = _fileStream.Length;
        }
    }

    /// <summary>通过 FileStream 恢复小文件</summary>
    private void LoadFromFileStream()
    {
        _fileStream!.Position = FileHeaderSize;

        while (_fileStream.Position < _fileStream.Length)
        {
            var recordStart = _fileStream.Position;

            var lenBuf = new Byte[4];
            if (_fileStream.Read(lenBuf, 0, 4) < 4) break;
            var totalLength = BitConverter.ToInt32(lenBuf, 0);
            if (totalLength < 5) break;

            var body = new Byte[totalLength];
            if (_fileStream.Read(body, 0, totalLength) < totalLength) break;

            ReplayRecord(body, totalLength, recordStart);
        }
    }

    /// <summary>回放一条记录</summary>
    /// <param name="body">记录体（不含 TotalLength 前缀）</param>
    /// <param name="totalLength">记录总长度</param>
    /// <param name="recordFileOffset">记录在文件中的起始位置（TotalLength 字段所在位置）</param>
    private void ReplayRecord(Byte[] body, Int32 totalLength, Int64 recordFileOffset)
    {
        var recordType = (KvRecordType)body[0];
        var dataLength = totalLength - 1 - 4;
        if (dataLength < 0) return;

        // 校验 CRC32
        var expectedChecksum = BitConverter.ToUInt32(body, totalLength - 4);
        var actualChecksum = Crc32.Compute(body, 0, 1 + dataLength);
        if (expectedChecksum != actualChecksum) return;

        switch (recordType)
        {
            case KvRecordType.Set:
                ReplaySet(body, 1, dataLength, recordFileOffset);
                break;
            case KvRecordType.Delete:
                ReplayDelete(body, 1, dataLength);
                break;
            case KvRecordType.Clear:
                _data.Clear();
                break;
        }
    }

    /// <summary>回放 Set 记录，重建内存索引（值留在磁盘）。跳过已过期的键以避免加载浪费内存</summary>
    /// <param name="body">记录体</param>
    /// <param name="offset">数据起始偏移</param>
    /// <param name="dataLength">数据长度</param>
    /// <param name="recordFileOffset">记录在文件中的起始位置</param>
    private void ReplaySet(Byte[] body, Int32 offset, Int32 dataLength, Int64 recordFileOffset)
    {
        var reader = new SpanReader(body, offset, dataLength);

        var keyLen = reader.ReadUInt16();
        var key = reader.ReadString(keyLen);

        var expiresAt = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        var valueLen = reader.ReadInt32();

        // 过期的 key 不加载到内存索引中，避免浪费内存
        if (expiresAt < DateTime.MaxValue && DateTime.UtcNow >= expiresAt) return;

        // 计算值在文件中的偏移: recordStart + 4(TotalLen) + 1(Type) + 2(KeyLen) + keyLen + 8(ExpiresAt) + 4(ValueLen)
        var valueOffset = valueLen > 0 ? recordFileOffset + 4 + 1 + 2 + keyLen + 8 + 4 : -1L;

        _data[key] = new KvEntry
        {
            ValueOffset = valueOffset,
            ValueLength = valueLen,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>回放 Delete 记录</summary>
    /// <param name="body">记录体</param>
    /// <param name="offset">数据起始偏移</param>
    /// <param name="dataLength">数据长度</param>
    private void ReplayDelete(Byte[] body, Int32 offset, Int32 dataLength)
    {
        var reader = new SpanReader(body, offset, dataLength);

        var keyLen = reader.ReadUInt16();
        var key = reader.ReadString(keyLen);

        _data.TryRemove(key, out _);
    }
    #endregion

    #region 压缩与维护
    /// <summary>尝试自动压缩。当写入记录数与存活键数的比值超过 KvCompactRatio 时触发 Compact。调用方需已持有 _writeLock</summary>
    private void TryAutoCompactNoLock()
    {
        if (_compacting) return;

        var ratio = _options?.KvCompactRatio ?? 0;
        if (ratio <= 0) return;

        // 存活键数不足 100 时不触发，避免小数据量时频繁压缩
        var aliveCount = _data.Count;
        if (aliveCount < 100) return;

        // 写入记录数 ÷ 存活键数 > 比率阈值 时执行压缩
        if ((Double)_writeCount / aliveCount > ratio)
        {
            CompactNoLock();
        }
    }

    /// <summary>压缩数据文件，将存活键逐条写入临时文件后替换原文件，同时重建内存索引。避免一次性加载全部值到内存，适合较大文件场景</summary>
    public void Compact()
    {
        CheckDisposed();

        lock (_writeLock)
        {
            CompactNoLock();
        }
    }

    /// <summary>压缩数据文件的内部实现，调用方需持有 _writeLock</summary>
    private void CompactNoLock()
    {
        _compacting = true;
        try
        {
            var tempPath = _filePath + ".compact.tmp";

            // 新索引，记录写入临时文件后各键的偏移信息
            var newEntries = new Dictionary<String, KvEntry>(StringComparer.Ordinal);

            try
            {
                // 逐条从旧文件读取存活条目并写入临时文件，每次仅一条记录在内存中
                using var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.SequentialScan);

                // 写入文件头
                var header = new FileHeader
                {
                    Version = FileVersion,
                    FileType = FileType.KvData,
                    PageSize = 1,
                    CreateTime = DateTime.UtcNow,
                };
                var headerBuf = new Byte[FileHeaderSize];
                header.Write(headerBuf);
                tempStream.Write(headerBuf, 0, headerBuf.Length);

                foreach (var kvp in _data)
                {
                    if (kvp.Value.IsExpired()) continue;

                    using var pk = ReadValueFromDiskNoLock(kvp.Value);
                    var value = pk != null ? pk.GetSpan() : ReadOnlySpan<byte>.Empty;

                    var valueOffset = WriteSetRecordToStream(tempStream, kvp.Key, value, kvp.Value.ExpiresAt);
                    newEntries[kvp.Key] = new KvEntry
                    {
                        ValueOffset = valueOffset,
                        ValueLength = value.Length,
                        ExpiresAt = kvp.Value.ExpiresAt,
                    };
                }

                tempStream.Flush(true);
            }
            catch
            {
                // 写入失败时清理临时文件，保留原文件不受影响
                try { File.Delete(tempPath); } catch { }
                throw;
            }

            // 关闭旧文件流后用临时文件替换
            _fileStream!.Dispose();
            _fileStream = null;

            File.Delete(_filePath);
            File.Move(tempPath, _filePath);

            // 重新打开文件流，定位到末尾供后续追加写入
            _fileStream = new FileStream(_filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
            _fileStream.Position = _fileStream.Length;

            // 更新内存索引
            _data.Clear();
            foreach (var kvp in newEntries)
            {
                _data[kvp.Key] = kvp.Value;
            }

            // 压缩完成后重置写入计数
            _writeCount = 0;
        }
        finally
        {
            _compacting = false;
        }
    }
    #endregion
}
