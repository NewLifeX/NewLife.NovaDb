namespace NewLife.QueryBench;

/// <summary>基准查询定义。SQL 文本三库完全一致，参数名统一 @x</summary>
public sealed class BenchQuery
{
    /// <summary>查询编号</summary>
    public String Id { get; init; } = "";

    /// <summary>SQL 模板，{t} 由表名替换</summary>
    public String SqlTemplate { get; init; } = "";

    /// <summary>参数集合</summary>
    public Dictionary<String, Object?> Parameters { get; init; } = [];

    /// <summary>结果是否带排序（排序查询按顺序哈希，其余按无序聚合哈希）</summary>
    public Boolean Ordered { get; init; }

    /// <summary>替换表名</summary>
    /// <param name="table"></param>
    /// <returns></returns>
    public String GetSql(String table) => SqlTemplate.Replace("{t}", table);
}

/// <summary>查询矩阵 Q1~Q11</summary>
public static class QueryCatalog
{
    /// <summary>构建指定规模的查询矩阵</summary>
    /// <param name="dataset"></param>
    /// <param name="includeJoin"></param>
    /// <returns></returns>
    public static List<BenchQuery> Build(DatasetInfo dataset, Boolean includeJoin)
    {
        var total = dataset.Rows;
        var maxUser = dataset.MaxUserId;
        var id = Math.Max(1, total / 2);
        var a = Math.Max(1, total / 4);
        var b = a + 100;
        var user = Math.Max(1, maxUser / 2);
        var userThird = Math.Max(1, maxUser / 3);
        var code = DataGenerator.Create(id, total).Code;

        var queries = new List<BenchQuery>
        {
            new()
            {
                Id = "Q1",
                SqlTemplate = "SELECT id,user_id,status,code FROM {t} WHERE id = @id",
                Parameters = new() { ["@id"] = id },
            },
            new()
            {
                Id = "Q2",
                SqlTemplate = "SELECT id,user_id,status,code FROM {t} WHERE id >= @a AND id < @b",
                Parameters = new() { ["@a"] = a, ["@b"] = b },
            },
            new()
            {
                Id = "Q3",
                SqlTemplate = "SELECT id,user_id,status,code FROM {t} WHERE user_id = @u",
                Parameters = new() { ["@u"] = user },
            },
            new()
            {
                Id = "Q4",
                SqlTemplate = "SELECT id,user_id,status,code FROM {t} WHERE status = @s",
                Parameters = new() { ["@s"] = 2 },
            },
            new()
            {
                Id = "Q5",
                SqlTemplate = "SELECT id,user_id,status,code FROM {t} WHERE status = @s AND created_at >= @t1 AND created_at < @t2",
                Parameters = new()
                {
                    ["@s"] = 2,
                    ["@t1"] = DataGenerator.Anchor.AddDays(-90),
                    ["@t2"] = DataGenerator.Anchor,
                },
            },
            new()
            {
                Id = "Q6",
                SqlTemplate = "SELECT id FROM {t} WHERE status IN (0,1) AND user_id = @u AND amount > @m AND code LIKE 'AB%' ORDER BY created_at DESC LIMIT 20",
                Parameters = new() { ["@u"] = userThird, ["@m"] = 5000.0 },
                Ordered = true,
            },
            new()
            {
                Id = "Q7",
                SqlTemplate = "SELECT id FROM {t} WHERE user_id = @u OR code = @c",
                Parameters = new() { ["@u"] = user, ["@c"] = code },
            },
            new()
            {
                Id = "Q8",
                SqlTemplate = "SELECT id FROM {t} WHERE status = @s ORDER BY created_at DESC LIMIT 20 OFFSET 1000",
                Parameters = new() { ["@s"] = 2 },
                Ordered = true,
            },
            new()
            {
                Id = "Q9",
                SqlTemplate = "SELECT COUNT(*) FROM {t} WHERE status = @s",
                Parameters = new() { ["@s"] = 3 },
            },
            new()
            {
                Id = "Q10",
                SqlTemplate = "SELECT id FROM {t} WHERE remark LIKE '%abc%'",
                Parameters = [],
            },
        };

        if (includeJoin)
        {
            queries.Add(new()
            {
                Id = "Q11",
                SqlTemplate = "SELECT o.id, u.name FROM {t} o JOIN t_user u ON u.id = o.user_id WHERE o.status = @s LIMIT 20",
                Parameters = new() { ["@s"] = 2 },
            });
        }

        return queries;
    }
}

/// <summary>数据集描述</summary>
public sealed class DatasetInfo
{
    /// <summary>规模名称</summary>
    public String Scale { get; init; } = "";

    /// <summary>行数</summary>
    public Int64 Rows { get; init; }

    /// <summary>用户编号上界</summary>
    public Int32 MaxUserId { get; init; }

    /// <summary>按规模名称创建</summary>
    /// <param name="scale"></param>
    /// <returns></returns>
    public static DatasetInfo Create(String scale)
    {
        if (!BenchOptions.ScaleRows.TryGetValue(scale, out var rows))
            throw new ArgumentException($"未知规模：{scale}", nameof(scale));

        return new DatasetInfo
        {
            Scale = scale,
            Rows = rows,
            MaxUserId = DataGenerator.MaxUserId(rows),
        };
    }
}
