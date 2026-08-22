using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;

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
            ShowType.TableStatus => ExecuteShowTableStatus(show),
            ShowType.Columns => ExecuteShowColumns(show),
            ShowType.Index => ExecuteShowIndex(show),
            ShowType.Users => ExecuteShowUsers(show),
            ShowType.Variables => ExecuteShowVariables(show),
            ShowType.Binlogs => ExecuteShowBinlogs(show),
            ShowType.BinlogStatus => ExecuteShowBinlogStatus(show),
            _ => throw new NotSupportedException($"Unsupported SHOW type: {show.ShowType}")
        };
    }

    /// <summary>SHOW BINLOGS —— Binlog 文件列表</summary>
    /// <param name="show">SHOW 语句</param>
    /// <returns>Binlog 文件列表</returns>
    private SqlResult ExecuteShowBinlogs(ShowStatement show)
    {
        var schema = new TableSchema("binlogs");
        schema.AddColumn(new ColumnDefinition("Log_name", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("File_size", DataType.Int64, false));

        var rows = new List<Object?[]>();
        if (Binlog != null)
        {
            foreach (var (fileName, size) in Binlog.ListFiles())
            {
                if (show.LikePattern.IsNullOrEmpty() || MatchLike(fileName, show.LikePattern))
                    rows.Add([fileName, size]);
            }
        }

        return new SqlResult { ColumnNames = schema.Columns.Select(c => c.Name).ToArray(), Rows = rows };
    }

    /// <summary>SHOW BINLOG STATUS —— 当前 Binlog 状态</summary>
    /// <param name="show">SHOW 语句</param>
    /// <returns>当前 Binlog 文件与位置</returns>
    private SqlResult ExecuteShowBinlogStatus(ShowStatement show)
    {
        var schema = new TableSchema("binlog_status");
        schema.AddColumn(new ColumnDefinition("File", DataType.String, false));
        schema.AddColumn(new ColumnDefinition("Position", DataType.Int64, false));
        schema.AddColumn(new ColumnDefinition("File_index", DataType.Int32, false));

        var rows = new List<Object?[]>();
        if (Binlog != null)
        {
            var files = Binlog.ListFiles();
            var currentIndex = Binlog.FileIndex;
            var currentFile = files.FirstOrDefault(f => f.FileName.EndsWith($".{currentIndex:D6}"));
            rows.Add([currentFile.FileName, Binlog.Position, currentIndex]);
        }

        return new SqlResult { ColumnNames = schema.Columns.Select(c => c.Name).ToArray(), Rows = rows };
    }

    /// <summary>PURGE BINLOGS —— 清理 Binlog 文件</summary>
    /// <param name="stmt">Binlog 管理语句</param>
    /// <returns>清理的文件数量</returns>
    private SqlResult ExecutePurgeBinlogs(BinlogStatement stmt)
    {
        if (Binlog == null)
            throw new NovaException(ErrorCode.NotSupported, "Binlog is not enabled");

        // 清理到指定文件索引（TO 'binlog.000123'），不删除目标文件本身
        var targetIndex = stmt.ToIndex ?? 0;
        if (targetIndex <= 0)
            throw new NovaException(ErrorCode.InvalidArgument, "PURGE BINLOGS TO requires a valid file index");

        var deleted = Binlog.Purge(targetIndex);
        return new SqlResult { AffectedRows = deleted };
    }

    /// <summary>FLUSH BINLOGS —— 轮转到新 Binlog 文件</summary>
    /// <returns>执行结果</returns>
    private SqlResult ExecuteFlushBinlogs()
    {
        if (Binlog == null)
            throw new NovaException(ErrorCode.NotSupported, "Binlog is not enabled");

        Binlog.Rotate();
        return new SqlResult { AffectedRows = 1 };
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

            var stats = GetTableStatsNoLock(schema.TableName);
            rows.Add([
                schema.TableName,                          // 0: Tables_in_db
                "BASE TABLE",                              // 1: Table_type
                schema.EngineName,                         // 2: Engine
                schema.Comment ?? String.Empty,            // 3: Comment
                (Object?)(stats?.ColumnCount ?? 0),        // 4: ColumnCount
                (Object?)(stats?.IndexCount ?? 0),         // 5: IndexCount
                (Object?)(stats?.TableRows ?? 0L),         // 6: TableRows
                (Object?)(stats?.DataLength ?? 0L),        // 7: DataLength
                stats?.CreateTime,                         // 8: CreateTime
            ]);
        }

        var dbName = Path.GetFileName(_dbPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var colName = $"Tables_in_{dbName}";
        return new SqlResult
        {
            ColumnNames = [colName, "Table_type", "Engine", "Comment", "ColumnCount", "IndexCount", "TableRows", "DataLength", "CreateTime"],
            Rows = rows
        };
    }

    private SqlResult ExecuteShowTableStatus(ShowStatement show)
    {
        // MySQL SHOW TABLE STATUS 兼容输出，列顺序与 MySQL 一致（XCode 等元数据按列名取值）
        var rows = new List<Object?[]>();

        using var _ = _metaLock.AcquireRead();
        foreach (var schema in _schemas.Values)
        {
            if (!show.LikePattern.IsNullOrEmpty() && !MatchLike(schema.TableName, show.LikePattern))
                continue;

            var stats = GetTableStatsNoLock(schema.TableName);
            rows.Add([
                schema.TableName,                          // 0: Name
                schema.EngineName,                         // 1: Engine
                10,                                        // 2: Version
                "Dynamic",                                 // 3: Row_format
                (Object?)(stats?.TableRows ?? 0L),         // 4: Rows
                (Object?)0L,                               // 5: Avg_row_length
                (Object?)(stats?.DataLength ?? 0L),        // 6: Data_length
                (Object?)0L,                               // 7: Max_data_length
                (Object?)0L,                               // 8: Index_length
                (Object?)0L,                               // 9: Data_free
                (Object?)DBNull.Value,                     // 10: Auto_increment
                (Object?)(stats?.CreateTime ?? (Object?)DBNull.Value), // 11: Create_time
                (Object?)DBNull.Value,                     // 12: Update_time
                (Object?)DBNull.Value,                     // 13: Check_time
                "utf8mb4_general_ci",                      // 14: Collation
                (Object?)DBNull.Value,                     // 15: Checksum
                String.Empty,                              // 16: Create_options
                schema.Comment ?? String.Empty             // 17: Comment
            ]);
        }

        return new SqlResult
        {
            ColumnNames = ["Name", "Engine", "Version", "Row_format", "Rows", "Avg_row_length",
                           "Data_length", "Max_data_length", "Index_length", "Data_free",
                           "Auto_increment", "Create_time", "Update_time", "Check_time",
                           "Collation", "Checksum", "Create_options", "Comment"],
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
