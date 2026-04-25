namespace NewLife.NovaDb.Sql;

/// <summary>SQL 执行结果</summary>
public class SqlResult
{
    /// <summary>受影响行数（DDL/DML 语句）</summary>
    public Int32 AffectedRows { get; set; }

    /// <summary>列名（SELECT 语句）</summary>
    public String[]? ColumnNames { get; set; }

    /// <summary>结果行（SELECT 语句）</summary>
    public List<Object?[]> Rows { get; set; } = [];

    /// <summary>是否为查询结果</summary>
    public Boolean IsQuery => ColumnNames != null;

    /// <summary>获取标量值（第一行第一列）</summary>
    /// <returns>标量值</returns>
    public Object? GetScalar()
    {
        if (Rows.Count > 0 && Rows[0].Length > 0)
            return Rows[0][0];
        return null;
    }
}

/// <summary>表统计信息</summary>
public class TableStats
{
    /// <summary>存储引擎名称</summary>
    public String EngineName { get; set; } = "Nova";

    /// <summary>列数</summary>
    public Int32 ColumnCount { get; set; }

    /// <summary>索引数（含主键）</summary>
    public Int32 IndexCount { get; set; }

    /// <summary>估算行数</summary>
    public Int64 TableRows { get; set; }

    /// <summary>数据文件总大小（字节）</summary>
    public Int64 DataLength { get; set; }

    /// <summary>最早分片创建时间</summary>
    public DateTime? CreateTime { get; set; }

    /// <summary>表注释</summary>
    public String? Comment { get; set; }
}
