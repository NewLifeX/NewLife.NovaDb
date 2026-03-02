using System.Collections.Concurrent;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine.Flux;
using NewLife.NovaDb.Engine.KV;

namespace NewLife.NovaDb.Client;

/// <summary>嵌入模式数据库实例。同一数据库目录下共用 KvStore/FluxEngine 引擎</summary>
/// <remarks>
/// 一个数据库目录对应一个 EmbeddedDatabase 实例，内部管理多个 KV 表和一个 Flux 引擎。
/// KV 表按表名隔离存储在各自子目录下，Flux 引擎管理所有时序/MQ 数据。
/// </remarks>
internal sealed class EmbeddedDatabase
{
    private readonly String _dbPath;
    private readonly DbOptions _options;
    private readonly ConcurrentDictionary<String, KvStore> _kvStores = new(StringComparer.OrdinalIgnoreCase);
    private FluxEngine? _fluxEngine;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _fluxLock = new();
#else
    private readonly Object _fluxLock = new();
#endif

    /// <summary>数据库路径</summary>
    public String DbPath => _dbPath;

    /// <summary>Flux 引擎（时序/MQ 共用）。首次访问时创建</summary>
    public FluxEngine FluxEngine
    {
        get
        {
            if (_fluxEngine != null) return _fluxEngine;
            lock (_fluxLock)
            {
                if (_fluxEngine != null) return _fluxEngine;

                var mqPath = Path.Combine(_dbPath, "flux");
                _fluxEngine = new FluxEngine(mqPath, _options);
                return _fluxEngine;
            }
        }
    }

    /// <summary>创建嵌入模式数据库实例</summary>
    /// <param name="dbPath">数据库目录路径（已标准化的完整路径）</param>
    /// <param name="csb">连接字符串设置</param>
    public EmbeddedDatabase(String dbPath, NovaConnectionStringBuilder csb)
    {
        _dbPath = dbPath;

        _options = new DbOptions
        {
            Path = dbPath,
            WalMode = csb.WalMode,
            ReadOnly = csb.ReadOnly
        };

        if (!Directory.Exists(dbPath))
            Directory.CreateDirectory(dbPath);
    }

    /// <summary>获取指定名称的 KV 存储。同一表名共用实例</summary>
    /// <param name="tableName">KV 表名</param>
    /// <returns>KvStore 实例</returns>
    public KvStore GetKvStore(String tableName)
    {
        return _kvStores.GetOrAdd(tableName, name =>
        {
            var kvPath = Path.Combine(_dbPath, $"{name}.kvd");
            return new KvStore(_options, kvPath);
        });
    }
}