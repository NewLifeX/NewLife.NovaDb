using NewLife.Data;
using NewLife.Log;
using NewLife.NovaDb.Core;
using NewLife.Serialization;

namespace NewLife.NovaDb.Storage;

/// <summary>数据库管理器（网络模式专用，管理 Nova 系统库及所有用户数据库）</summary>
/// <remarks>
/// 仅在网络模式下使用。嵌入模式不需要系统数据库，直接使用 DatabaseDirectory 操作单个库即可。
///
/// 职责：
/// - 创建并维护名为 Nova 的系统级数据库，用于记录系统相关信息
/// - 服务器启动时执行两阶段数据库发现：
///   阶段一：扫描默认数据库目录（BasePath）下所有子目录，发现新数据库并注册
///   阶段二：逐个检查系统库中已登记的所有数据库（含外部目录），更新在线/离线状态
///
/// 目录结构示例：
/// BasePath/                       ← 默认数据库目录
/// ├── Nova/                       ← 系统数据库
/// │   ├── nova.db                 ← FileHeader 元数据
/// │   └── databases.json          ← 数据库目录（持久化 DatabaseInfo 列表）
/// ├── UserDb1/                    ← 默认目录内的数据库
/// │   └── nova.db
/// └── UserDb2/
///     └── nova.db
///
/// /other/path/ExternalDb/         ← 外部目录的数据库（手动注册）
///     └── nova.db
/// </remarks>
public class DatabaseManager
{
    /// <summary>系统数据库名称</summary>
    public const String SystemDatabaseName = "Nova";

    /// <summary>数据库目录文件名</summary>
    private const String CatalogFileName = "databases.json";

    private readonly String _basePath;
    private readonly DbOptions _options;
    private readonly Dictionary<String, DatabaseInfo> _databases = new(StringComparer.OrdinalIgnoreCase);
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif

    /// <summary>基础目录路径（默认数据库目录）</summary>
    public String BasePath => _basePath;

    /// <summary>系统数据库目录</summary>
    public DatabaseDirectory? SystemDatabase { get; private set; }

    /// <summary>所有已知数据库信息（只读快照）</summary>
    public IReadOnlyDictionary<String, DatabaseInfo> Databases
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<String, DatabaseInfo>(_databases, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>实例化数据库管理器</summary>
    /// <param name="basePath">默认数据库目录路径</param>
    /// <param name="options">默认数据库配置</param>
    public DatabaseManager(String basePath, DbOptions options)
    {
        if (basePath == null) throw new ArgumentNullException(nameof(basePath));
        if (String.IsNullOrWhiteSpace(basePath))
            throw new ArgumentException("Base path cannot be empty", nameof(basePath));

        _basePath = basePath;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>初始化数据库管理器（创建/打开系统库、两阶段发现数据库）</summary>
    /// <remarks>
    /// 启动流程：
    /// 1. 初始化 Nova 系统数据库
    /// 2. 加载系统库中已登记的数据库目录
    /// 3. 阶段一：扫描默认目录，发现并注册新数据库
    /// 4. 阶段二：逐个检查所有已登记数据库（含外部目录），更新在线/离线状态
    /// 5. 持久化更新后的目录
    /// </remarks>
    public void Initialize()
    {
        _basePath.EnsureDirectory(false);

        // 初始化系统库
        InitializeSystemDatabase();

        // 加载已有的数据库目录
        LoadCatalog();

        // 阶段一：扫描默认目录，发现新数据库
        ScanDefaultDirectory();

        // 阶段二：逐个检查所有已登记数据库的状态
        CheckRegisteredDatabases();

        // 持久化更新后的目录
        SaveCatalog();
    }

    #region 系统库
    /// <summary>初始化 Nova 系统数据库</summary>
    private void InitializeSystemDatabase()
    {
        var systemPath = Path.Combine(_basePath, SystemDatabaseName);
        var systemOptions = new DbOptions
        {
            Path = systemPath,
            PageSize = _options.PageSize
        };

        var systemDb = new DatabaseDirectory(systemPath, systemOptions);

        var metaFile = systemPath.CombinePath("nova.db").AsFile();
        if (!metaFile.Exists || metaFile.Length == 0)
        {
            systemDb.Create();
            XTrace.WriteLine("[DatabaseManager] Nova system database created: {0}", systemPath);
        }
        else
        {
            systemDb.Open();
            XTrace.WriteLine("[DatabaseManager] Nova system database opened: {0}", systemPath);
        }

        SystemDatabase = systemDb;
    }
    #endregion

    #region 扫描发现
    /// <summary>阶段一：扫描默认目录下所有子目录，发现新数据库并注册到系统库</summary>
    /// <remarks>仅扫描 BasePath 下的直接子目录，不处理已登记数据库的状态变更</remarks>
    public void ScanDefaultDirectory()
    {
        if (!Directory.Exists(_basePath)) return;

        var nowTicks = DateTime.UtcNow.Ticks;
        var directories = Directory.GetDirectories(_basePath);

        foreach (var dir in directories)
        {
            var dirName = Path.GetFileName(dir);

            // 跳过系统库自身
            if (String.Equals(dirName, SystemDatabaseName, StringComparison.OrdinalIgnoreCase))
                continue;

            // 已登记的数据库跳过，状态由阶段二负责
            lock (_lock)
            {
                if (_databases.ContainsKey(dirName)) continue;
            }

            // 检查是否存在 nova.db 元数据文件
            var metaPath = Path.Combine(dir, "nova.db");
            if (!File.Exists(metaPath)) continue;

            // 尝试读取文件头，验证是否为真实数据库
            var header = TryReadFileHeader(metaPath);
            if (header == null) continue;

            // 新发现的数据库，注册到系统库
            var info = new DatabaseInfo
            {
                Name = dirName,
                Path = dir,
                Status = DatabaseStatus.Online,
                IsExternal = false,
                Version = header.Version,
                PageSize = header.PageSize,
                CreateTime = header.CreateTime,
                LastSeenAt = nowTicks
            };

            lock (_lock)
            {
                _databases[dirName] = info;
            }

            XTrace.WriteLine("[DatabaseManager] Discovered database: {0}", dirName);
        }
    }

    /// <summary>阶段二：逐个检查所有已登记数据库的实际状态</summary>
    /// <remarks>
    /// 遍历系统库中所有已登记的数据库（包括默认目录内的和外部目录的），
    /// 逐个检查其 nova.db 元数据文件是否存在且有效，更新在线/离线状态。
    /// </remarks>
    public void CheckRegisteredDatabases()
    {
        var nowTicks = DateTime.UtcNow.Ticks;

        List<DatabaseInfo> snapshot;
        lock (_lock)
        {
            snapshot = _databases.Values.ToList();
        }

        foreach (var info in snapshot)
        {
            var metaPath = Path.Combine(info.Path, "nova.db");

            // 检查元数据文件是否存在且有效
            var header = File.Exists(metaPath) ? TryReadFileHeader(metaPath) : null;

            lock (_lock)
            {
                if (header != null)
                {
                    // 数据库在线
                    if (info.Status != DatabaseStatus.Online)
                        XTrace.WriteLine("[DatabaseManager] Database is back online: {0}", info.Name);

                    info.Status = DatabaseStatus.Online;
                    info.LastSeenAt = nowTicks;
                    info.Version = header.Version;
                    info.PageSize = header.PageSize;
                }
                else
                {
                    // 数据库离线
                    if (info.Status != DatabaseStatus.Offline)
                    {
                        info.Status = DatabaseStatus.Offline;
                        XTrace.WriteLine("[DatabaseManager] Database marked offline: {0} ({1})", info.Name, info.Path);
                    }
                }
            }
        }
    }

    /// <summary>尝试读取 nova.db 文件头</summary>
    /// <param name="metaPath">元数据文件路径</param>
    /// <returns>文件头对象；文件损坏或格式不匹配时返回 null</returns>
    internal static FileHeader? TryReadFileHeader(String metaPath)
    {
        try
        {
            var metaBytes = File.ReadAllBytes(metaPath);
            if (metaBytes.Length < FileHeader.HeaderSize) return null;

            return FileHeader.Read(new ArrayPacket(metaBytes));
        }
        catch (Exception ex)
        {
            XTrace.WriteLine("[DatabaseManager] Failed to read metadata: {0}, Error: {1}", metaPath, ex.Message);
            return null;
        }
    }
    #endregion

    #region 数据库操作
    /// <summary>注册外部目录的数据库到系统库</summary>
    /// <param name="name">数据库名称</param>
    /// <param name="path">数据库目录的完整路径</param>
    /// <returns>注册成功的数据库信息</returns>
    public DatabaseInfo RegisterDatabase(String name, String path)
    {
        if (String.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Database name cannot be empty", nameof(name));
        if (String.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Database path cannot be empty", nameof(path));

        lock (_lock)
        {
            if (_databases.ContainsKey(name))
                throw new NovaException(ErrorCode.DatabaseExists, $"Database '{name}' already registered");
        }

        // 验证路径下有有效的 nova.db
        var metaPath = Path.Combine(path, "nova.db");
        if (!File.Exists(metaPath))
            throw new NovaException(ErrorCode.DatabaseNotFound, $"No metadata file found at: {path}");

        var header = TryReadFileHeader(metaPath);
        if (header == null)
            throw new NovaException(ErrorCode.FileCorrupted, $"Invalid metadata file at: {metaPath}");

        var nowTicks = DateTime.UtcNow.Ticks;

        // 判断是否为外部数据库
        var fullBase = Path.GetFullPath(_basePath);
        var fullPath = Path.GetFullPath(path);
        var isExternal = !fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);

        var info = new DatabaseInfo
        {
            Name = name,
            Path = fullPath,
            Status = DatabaseStatus.Online,
            IsExternal = isExternal,
            Version = header.Version,
            PageSize = header.PageSize,
            CreateTime = header.CreateTime,
            LastSeenAt = nowTicks
        };

        lock (_lock)
        {
            _databases[name] = info;
        }

        SaveCatalog();

        XTrace.WriteLine("[DatabaseManager] Registered database: {0} -> {1} (External={2})", name, fullPath, isExternal);

        return info;
    }

    /// <summary>获取指定数据库信息</summary>
    /// <param name="name">数据库名称</param>
    /// <returns>数据库信息；不存在时返回 null</returns>
    public DatabaseInfo? GetDatabase(String name)
    {
        if (String.IsNullOrWhiteSpace(name)) return null;

        lock (_lock)
        {
            return _databases.TryGetValue(name, out var info) ? info : null;
        }
    }

    /// <summary>列举所有在线数据库</summary>
    /// <returns>按名称排序的在线数据库信息列表</returns>
    public IList<DatabaseInfo> ListOnlineDatabases()
    {
        lock (_lock)
        {
            return _databases.Values
                .Where(x => x.Status == DatabaseStatus.Online)
                .OrderBy(x => x.Name)
                .ToList();
        }
    }

    /// <summary>列举所有数据库（含离线）</summary>
    /// <returns>按名称排序的所有数据库信息列表</returns>
    public IList<DatabaseInfo> ListAllDatabases()
    {
        lock (_lock)
        {
            return _databases.Values
                .OrderBy(x => x.Name)
                .ToList();
        }
    }
    #endregion

    #region 目录持久化
    /// <summary>从系统库目录中加载数据库目录信息</summary>
    private void LoadCatalog()
    {
        if (SystemDatabase == null) return;

        var catalogPath = Path.Combine(SystemDatabase.Path, CatalogFileName);
        if (!File.Exists(catalogPath)) return;

        try
        {
            var json = File.ReadAllText(catalogPath);
            if (String.IsNullOrWhiteSpace(json)) return;

            var list = json.ToJsonEntity<List<DatabaseInfo>>();
            if (list == null || list.Count == 0) return;

            lock (_lock)
            {
                foreach (var info in list)
                {
                    if (!String.IsNullOrEmpty(info.Name))
                        _databases[info.Name] = info;
                }
            }
        }
        catch (Exception ex)
        {
            XTrace.WriteLine("[DatabaseManager] Failed to load catalog: {0}", ex.Message);
        }
    }

    /// <summary>将数据库目录信息持久化到系统库</summary>
    private void SaveCatalog()
    {
        if (SystemDatabase == null) return;

        var catalogPath = Path.Combine(SystemDatabase.Path, CatalogFileName);

        try
        {
            List<DatabaseInfo> list;
            lock (_lock)
            {
                list = _databases.Values.OrderBy(x => x.Name).ToList();
            }

            var json = list.ToJson(true);
            File.WriteAllText(catalogPath, json);
        }
        catch (Exception ex)
        {
            XTrace.WriteLine("[DatabaseManager] Failed to save catalog: {0}", ex.Message);
        }
    }
    #endregion

    #region 日志
    /// <summary>日志对象</summary>
    public ILog Log { get; set; } = XTrace.Log;
    #endregion
}
