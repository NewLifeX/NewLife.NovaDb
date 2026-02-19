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
