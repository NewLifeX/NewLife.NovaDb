using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.Serialization;

namespace NewLife.NovaDb.Storage;

/// <summary>表架构持久化器。将 <see cref="TableSchema"/> 序列化为 JSON 保存到磁盘，
/// 使得 NovaDb 在进程/连接重启后仍能恢复表结构与索引定义</summary>
public static class SchemaPersister
{
    /// <summary>schema 文件后缀</summary>
    public const String SchemaFileExtension = ".schema";

    /// <summary>列定义的序列化模型</summary>
    private class ColumnDto
    {
        public String Name { get; set; } = String.Empty;
        public DataType DataType { get; set; }
        public Boolean Nullable { get; set; } = true;
        public Boolean IsPrimaryKey { get; set; }
        public Boolean IsAutoIncrement { get; set; }
        public Object? DefaultValue { get; set; }
        public String? Comment { get; set; }
    }

    /// <summary>索引定义的序列化模型</summary>
    private class IndexDto
    {
        public String IndexName { get; set; } = String.Empty;
        public List<String> Columns { get; set; } = [];
        public Boolean IsUnique { get; set; }
    }

    /// <summary>表架构的序列化模型</summary>
    private class TableSchemaDto
    {
        public String TableName { get; set; } = String.Empty;
        public String? Comment { get; set; }
        public String EngineName { get; set; } = "Nova";
        public List<ColumnDto> Columns { get; set; } = [];
        public List<IndexDto> Indexes { get; set; } = [];
    }

    /// <summary>根据数据库目录和表名拼出 schema 文件完整路径</summary>
    /// <param name="dbPath">数据库目录</param>
    /// <param name="tableName">表名</param>
    /// <returns>schema 文件路径</returns>
    public static String GetSchemaFilePath(String dbPath, String tableName)
    {
        if (dbPath == null) throw new ArgumentNullException(nameof(dbPath));
        if (tableName == null) throw new ArgumentNullException(nameof(tableName));

        return Path.Combine(dbPath, tableName + SchemaFileExtension);
    }

    /// <summary>将表架构保存到磁盘</summary>
    /// <param name="dbPath">数据库目录</param>
    /// <param name="schema">表架构</param>
    public static void Save(String dbPath, TableSchema schema)
    {
        if (dbPath == null) throw new ArgumentNullException(nameof(dbPath));
        if (schema == null) throw new ArgumentNullException(nameof(schema));

        if (!Directory.Exists(dbPath)) Directory.CreateDirectory(dbPath);

        var dto = new TableSchemaDto
        {
            TableName = schema.TableName,
            Comment = schema.Comment,
            EngineName = schema.EngineName,
            Columns = [.. schema.Columns.Select(c => new ColumnDto
            {
                Name = c.Name,
                DataType = c.DataType,
                Nullable = c.Nullable,
                IsPrimaryKey = c.IsPrimaryKey,
                IsAutoIncrement = c.IsAutoIncrement,
                DefaultValue = c.DefaultValue,
                Comment = c.Comment,
            })],
            Indexes = [.. schema.Indexes.Select(i => new IndexDto
            {
                IndexName = i.IndexName,
                Columns = [.. i.Columns],
                IsUnique = i.IsUnique,
            })],
        };

        var json = dto.ToJson(true);
        var path = GetSchemaFilePath(dbPath, schema.TableName);
        File.WriteAllText(path, json);
    }

    /// <summary>从磁盘删除指定表的 schema 文件</summary>
    /// <param name="dbPath">数据库目录</param>
    /// <param name="tableName">表名</param>
    public static void Delete(String dbPath, String tableName)
    {
        var path = GetSchemaFilePath(dbPath, tableName);
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch { /* 忽略文件锁等错误 */ }
        }
    }

    /// <summary>扫描数据库目录加载所有表架构</summary>
    /// <param name="dbPath">数据库目录</param>
    /// <returns>表架构列表</returns>
    public static List<TableSchema> LoadAll(String dbPath)
    {
        var result = new List<TableSchema>();
        if (String.IsNullOrEmpty(dbPath) || !Directory.Exists(dbPath)) return result;

        foreach (var file in Directory.GetFiles(dbPath, "*" + SchemaFileExtension))
        {
            try
            {
                var json = File.ReadAllText(file);
                if (json.IsNullOrWhiteSpace()) continue;

                var dto = json.ToJsonEntity<TableSchemaDto>();
                if (dto == null || dto.TableName.IsNullOrEmpty()) continue;

                var schema = new TableSchema(dto.TableName)
                {
                    Comment = dto.Comment,
                    EngineName = dto.EngineName.IsNullOrEmpty() ? "Nova" : dto.EngineName,
                };

                foreach (var col in dto.Columns)
                {
                    var column = new ColumnDefinition(col.Name, col.DataType, col.Nullable, col.IsPrimaryKey)
                    {
                        IsAutoIncrement = col.IsAutoIncrement,
                        DefaultValue = col.DefaultValue,
                        Comment = col.Comment,
                    };
                    schema.AddColumn(column);
                }

                foreach (var idx in dto.Indexes)
                {
                    schema.AddIndex(new IndexDefinition(idx.IndexName, idx.Columns, idx.IsUnique));
                }

                result.Add(schema);
            }
            catch
            {
                // 单个文件解析失败不影响其它表加载
            }
        }

        return result;
    }
}
