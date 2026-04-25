namespace NewLife.NovaDb.Sql;

/// <summary>SHOW 命令执行器</summary>
public partial class SqlEngine
{
    /// <summary>执行 SHOW 命令，返回元数据结果集</summary>
    /// <param name="show">SHOW 语句</param>
    /// <returns>元数据结果集</returns>
    private SqlResult ExecuteShow(ShowStatement show)
    {
        return show.ShowType switch
        {
            ShowType.Databases => ExecuteShowDatabases(show),
            ShowType.Tables => ExecuteShowTables(show),
            ShowType.Columns => ExecuteShowColumns(show),
            ShowType.Index => ExecuteShowIndex(show),
            ShowType.Users => ExecuteShowUsers(show),
            ShowType.Variables => ExecuteShowVariables(show),
            _ => throw new NotSupportedException($"Unsupported SHOW type: {show.ShowType}")
        };
    }

    private SqlResult ExecuteShowDatabases(ShowStatement show)
    {
        var dbName = Path.GetFileName(_dbPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var rows = new List<Object?[]>();

        if (show.LikePattern.IsNullOrEmpty() || MatchLike(dbName, show.LikePattern))
            rows.Add([dbName]);

        return new SqlResult
        {
            ColumnNames = ["Database"],
            Rows = rows
        };
    }

    private SqlResult ExecuteShowTables(ShowStatement show)
    {
        var rows = new List<Object?[]>();

        using var _ = _metaLock.AcquireRead();
        foreach (var schema in _schemas.Values)
        {
            if (!show.LikePattern.IsNullOrEmpty() && !MatchLike(schema.TableName, show.LikePattern))
                continue;

            rows.Add([
                schema.TableName,         // 0: Tables_in_db
                "BASE TABLE",             // 1: Table_type
                schema.EngineName,        // 2: Engine
                schema.Comment ?? String.Empty // 3: Comment
            ]);
        }

        var dbName = Path.GetFileName(_dbPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var colName = $"Tables_in_{dbName}";
        return new SqlResult
        {
            ColumnNames = [colName, "Table_type", "Engine", "Comment"],
            Rows = rows
        };
    }

    private SqlResult ExecuteShowIndex(ShowStatement show)
    {
        var rows = new List<Object?[]>();
        var tableFilter = show.TableName;

        using var _ = _metaLock.AcquireRead();
        var schemas = tableFilter.IsNullOrEmpty()
            ? _schemas.Values.ToList()
            : _schemas.TryGetValue(tableFilter, out var s) ? [s] : [];

        foreach (var schema in schemas)
        {
            // 主键索引
            var pkCol = schema.GetPrimaryKeyColumn();
            if (pkCol != null)
            {
                rows.Add([
                    schema.TableName, // TABLE
                    0,                // NON_UNIQUE
                    "PRIMARY",        // KEY_NAME
                    1,                // SEQ_IN_INDEX
                    pkCol.Name,       // COLUMN_NAME
                    "A",              // COLLATION
                    (Object?)DBNull.Value, // CARDINALITY
                    (Object?)DBNull.Value, // SUB_PART
                    (Object?)DBNull.Value, // PACKED
                    "YES",            // NULL (pk: not null)
                    "BTREE",          // INDEX_TYPE
                    String.Empty,     // COMMENT
                    String.Empty      // INDEX_COMMENT
                ]);
            }

            // 二级索引
            foreach (var idx in schema.Indexes)
            {
                for (var i = 0; i < idx.Columns.Count; i++)
                {
                    rows.Add([
                        schema.TableName,    // TABLE
                        idx.IsUnique ? 0 : 1, // NON_UNIQUE
                        idx.IndexName,       // KEY_NAME
                        i + 1,               // SEQ_IN_INDEX
                        idx.Columns[i],      // COLUMN_NAME
                        "A",                 // COLLATION
                        (Object?)DBNull.Value,
                        (Object?)DBNull.Value,
                        (Object?)DBNull.Value,
                        "YES",               // NULL
                        "BTREE",             // INDEX_TYPE
                        String.Empty,        // COMMENT
                        String.Empty         // INDEX_COMMENT
                    ]);
                }
            }
        }

        return new SqlResult
        {
            ColumnNames = ["TABLE", "NON_UNIQUE", "KEY_NAME", "SEQ_IN_INDEX", "COLUMN_NAME",
                           "COLLATION", "CARDINALITY", "SUB_PART", "PACKED", "NULL",
                           "INDEX_TYPE", "COMMENT", "INDEX_COMMENT"],
            Rows = rows
        };
    }

    private SqlResult ExecuteShowColumns(ShowStatement show)
    {
        var rows = new List<Object?[]>();
        var tableFilter = show.TableName;

        using var _ = _metaLock.AcquireRead();
        var schemas = tableFilter.IsNullOrEmpty()
            ? _schemas.Values.ToList()
            : _schemas.TryGetValue(tableFilter, out var s) ? [s] : [];

        foreach (var schema in schemas)
        {
            foreach (var col in schema.Columns)
            {
                rows.Add([
                    col.Name,                                  // 0: Field
                    col.DataType.ToString().ToLower(),         // 1: Type
                    col.Nullable ? "YES" : "NO",               // 2: Null
                    col.IsPrimaryKey ? "PRI" : String.Empty,   // 3: Key
                    (Object?)DBNull.Value,                     // 4: Default
                    String.Empty,                              // 5: Extra
                    col.Comment ?? String.Empty                // 6: Comment
                ]);
            }
        }

        return new SqlResult
        {
            ColumnNames = ["Field", "Type", "Null", "Key", "Default", "Extra", "Comment"],
            Rows = rows
        };
    }

    private SqlResult ExecuteShowVariables(ShowStatement show)
    {
        var rows = new List<Object?[]>();
        // NovaDb 内嵌模式暂不支持服务器变量，返回空结果
        return new SqlResult
        {
            ColumnNames = ["Variable_name", "Value"],
            Rows = rows
        };
    }

    private SqlResult ExecuteShowUsers(ShowStatement show)
    {
        // NovaDb 内嵌模式无用户管理，返回空结果
        return new SqlResult
        {
            ColumnNames = ["Host", "User"],
            Rows = []
        };
    }

    /// <summary>SQL LIKE 模式匹配（% 匹配任意字符串，_ 匹配单个字符）</summary>
    private static Boolean MatchLike(String value, String? pattern)
    {
        if (pattern.IsNullOrEmpty()) return true;

        // 转换为正则表达式简单实现：将 % 换为 .*，_ 换为 .
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
