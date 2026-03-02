using System.Diagnostics;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.Sql;

namespace NewLife.NovaDb.Engine;

/// <summary>物化视图管理器，负责创建、刷新、删除和定时修正物化视图</summary>
/// <remarks>
/// 对应模块 X01（增量物化视图）、X02（物化视图定时修正）。
/// 通过 SqlEngine 执行源查询并缓存结果，支持手动/定时刷新。
/// </remarks>
public class MaterializedViewManager : IDisposable
{
    private readonly Dictionary<String, MaterializedView> _views = new(StringComparer.OrdinalIgnoreCase);
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _lock = new();
#else
    private readonly Object _lock = new();
#endif
    private readonly SqlEngine _engine;
    private Timer? _scheduler;
    private Boolean _disposed;

    /// <summary>定时检查间隔（秒），默认 60 秒</summary>
    public Int32 SchedulerIntervalSeconds { get; set; } = 60;

    /// <summary>视图数量</summary>
    public Int32 Count
    {
        get
        {
            lock (_lock) return _views.Count;
        }
    }

    /// <summary>创建物化视图管理器</summary>
    /// <param name="engine">SQL 执行引擎</param>
    public MaterializedViewManager(SqlEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>创建物化视图</summary>
    /// <param name="name">视图名称</param>
    /// <param name="query">源 SQL 查询</param>
    /// <param name="refreshIntervalSeconds">自动刷新间隔（秒），0 为仅手动</param>
    /// <returns>创建的物化视图</returns>
    public MaterializedView Create(String name, String query, Int32 refreshIntervalSeconds = 0)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        if (query == null) throw new ArgumentNullException(nameof(query));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MaterializedViewManager));

            if (_views.ContainsKey(name))
                throw new NovaException(ErrorCode.TableExists, $"Materialized view '{name}' already exists");

            var view = new MaterializedView
            {
                Name = name,
                Query = query,
                RefreshIntervalSeconds = refreshIntervalSeconds
            };

            // 立即执行首次刷新
            RefreshInternal(view);

            _views[name] = view;
            return view;
        }
    }

    /// <summary>刷新指定物化视图</summary>
    /// <param name="name">视图名称</param>
    /// <returns>刷新后的物化视图</returns>
    public MaterializedView Refresh(String name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MaterializedViewManager));

            if (!_views.TryGetValue(name, out var view))
                throw new NovaException(ErrorCode.TableNotFound, $"Materialized view '{name}' not found");

            RefreshInternal(view);
            return view;
        }
    }

    /// <summary>删除物化视图</summary>
    /// <param name="name">视图名称</param>
    /// <returns>是否删除成功</returns>
    public Boolean Drop(String name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));

        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MaterializedViewManager));

            return _views.Remove(name);
        }
    }

    /// <summary>获取物化视图</summary>
    /// <param name="name">视图名称</param>
    /// <returns>物化视图，不存在返回 null</returns>
    public MaterializedView? Get(String name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));

        lock (_lock)
        {
            return _views.TryGetValue(name, out var view) ? view : null;
        }
    }

    /// <summary>查询物化视图缓存数据</summary>
    /// <param name="name">视图名称</param>
    /// <returns>SQL 查询结果</returns>
    public SqlResult Query(String name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));

        lock (_lock)
        {
            if (!_views.TryGetValue(name, out var view))
                throw new NovaException(ErrorCode.TableNotFound, $"Materialized view '{name}' not found");

            return new SqlResult
            {
                AffectedRows = view.Rows.Count,
                ColumnNames = view.ColumnNames.ToArray(),
                Rows = view.Rows.Select(r => (Object?[])r.Clone()).ToList()
            };
        }
    }

    /// <summary>获取所有物化视图的状态信息</summary>
    /// <returns>视图名称及状态列表</returns>
    public List<MaterializedView> GetAll()
    {
        lock (_lock)
        {
            return new List<MaterializedView>(_views.Values);
        }
    }

    /// <summary>启动定时刷新调度器</summary>
    public void StartScheduler()
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MaterializedViewManager));
            if (_scheduler != null) return;

            _scheduler = new Timer(SchedulerCallback, null,
                TimeSpan.FromSeconds(SchedulerIntervalSeconds),
                TimeSpan.FromSeconds(SchedulerIntervalSeconds));
        }
    }

    /// <summary>停止定时刷新调度器</summary>
    public void StopScheduler()
    {
        lock (_lock)
        {
            _scheduler?.Dispose();
            _scheduler = null;
        }
    }

    /// <summary>检查并刷新所有需要刷新的视图（供外部或定时器调用）</summary>
    /// <returns>刷新的视图数量</returns>
    public Int32 RefreshDue()
    {
        lock (_lock)
        {
            if (_disposed) return 0;

            var refreshed = 0;
            foreach (var view in _views.Values)
            {
                if (view.NeedsRefresh())
                {
                    RefreshInternal(view);
                    refreshed++;
                }
            }

            return refreshed;
        }
    }

    private void RefreshInternal(MaterializedView view)
    {
        var sw = Stopwatch.StartNew();

        var result = _engine.Execute(view.Query);

        view.ColumnNames = result.ColumnNames != null ? new List<String>(result.ColumnNames) : [];
        view.Rows = result.Rows?.Select(r => (Object?[])r.Clone()).ToList() ?? [];
        view.LastRefreshTime = DateTime.UtcNow;
        view.RefreshCount++;

        sw.Stop();
        view.LastRefreshMs = sw.ElapsedMilliseconds;
    }

    private void SchedulerCallback(Object? state)
    {
        try
        {
            RefreshDue();
        }
        catch
        {
            // 定时刷新异常不应中断调度器
        }
    }

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _scheduler?.Dispose();
            _scheduler = null;
            _views.Clear();
        }
    }
}
