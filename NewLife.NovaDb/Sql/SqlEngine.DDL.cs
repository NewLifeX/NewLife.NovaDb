using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Storage;

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
    #region DDL 执行

    private SqlResult ExecuteCreateTable(CreateTableStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        {
            if (_schemas.ContainsKey(stmt.TableName))
            {
                if (stmt.IfNotExists) return new SqlResult { AffectedRows = 0 };
                throw new NovaException(ErrorCode.TableExists, "Table '{0}' already exists", stmt.TableName);
            }

            var schema = new TableSchema(stmt.TableName);

            // 设置引擎名称，未指定时默认为 Nova
            schema.EngineName = stmt.EngineName ?? "Nova";

            // 设置表注释
            if (stmt.Comment != null)
                schema.Comment = stmt.Comment;

            foreach (var colDef in stmt.Columns)
            {
                var dataType = DataTypeNames.Parse(colDef.DataTypeName);
                var column = new ColumnDefinition(colDef.Name, dataType, !colDef.NotNull, colDef.IsPrimaryKey, GetColumnLength(colDef, dataType))
                {
                    IsAutoIncrement = colDef.IsAutoIncrement,
                    DefaultValue = colDef.DefaultValue
                };
                if (colDef.Comment != null)
                    column.Comment = colDef.Comment;
                schema.AddColumn(column);
            }

            // 表内索引（MySQL CREATE TABLE 内联 UNIQUE KEY / KEY）
            var table = new NovaTable(schema, _dbPath, _options, _txManager);

            // 表对象创建后再登记索引，避免构造函数的索引恢复流程提前注册同名索引
            if (stmt.Indexes.Count > 0)
            {
                var tx = _txManager.BeginTransaction();
                try
                {
                    foreach (var idx in stmt.Indexes)
                    {
                        var indexDef = new IndexDefinition(idx.Name, [.. idx.Columns], idx.IsUnique);
                        schema.AddIndex(indexDef);
                        table.CreateSecondaryIndex(indexDef, tx);
                    }
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    table.Dispose();
                    new TableFileManager(_dbPath, stmt.TableName, _options).DeleteAllFiles();
                    throw;
                }
            }

            _schemas[stmt.TableName] = schema;
            _tables[stmt.TableName] = table;

            // 持久化表架构，重启后可恢复
            SchemaPersister.Save(_dbPath, schema);

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteDropTable(DropTableStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        {
            if (!_schemas.ContainsKey(stmt.TableName))
            {
                if (stmt.IfExists) return new SqlResult { AffectedRows = 0 };
                throw new NovaException(ErrorCode.TableNotFound, "Table '{0}' not found", stmt.TableName);
            }

            if (_tables.TryGetValue(stmt.TableName, out var table))
            {
                table.Dispose();
                _tables.Remove(stmt.TableName);
            }

            _schemas.Remove(stmt.TableName);

            // 使用 TableFileManager 删除表的所有文件（平铺在数据库目录下）
            var fileManager = new TableFileManager(_dbPath, stmt.TableName, _options);
            try
            {
                fileManager.DeleteAllFiles();
            }
            catch
            {
                // 忽略文件系统错误
            }

            // 删除持久化的表架构文件
            SchemaPersister.Delete(_dbPath, stmt.TableName);

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteCreateIndex(CreateIndexStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        {
            if (!_schemas.TryGetValue(stmt.TableName, out var schema))
                throw new NovaException(ErrorCode.TableNotFound, "Table '{0}' not found", stmt.TableName);

            // 验证索引列存在
            foreach (var col in stmt.Columns)
            {
                if (!schema.HasColumn(col))
                    throw new NovaException(ErrorCode.InvalidArgument, "Column '{0}' not found in table '{1}'", col, stmt.TableName);
            }

            // 创建索引定义并注册到 Schema
            var indexDef = new IndexDefinition(stmt.IndexName, stmt.Columns, stmt.IsUnique);
            schema.AddIndex(indexDef);

            // 在 NovaTable 上构建二级索引
            if (_tables.TryGetValue(stmt.TableName, out var table))
            {
                var tx = _txManager.BeginTransaction();
                try
                {
                    table.CreateSecondaryIndex(indexDef, tx);
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    schema.RemoveIndex(stmt.IndexName);
                    throw;
                }
            }

            // 持久化索引定义
            SchemaPersister.Save(_dbPath, schema);

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteDropIndex(DropIndexStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        {
            if (!_schemas.TryGetValue(stmt.TableName, out var schema))
                throw new NovaException(ErrorCode.TableNotFound, "Table '{0}' not found", stmt.TableName);

            // 从 Schema 移除索引定义
            schema.RemoveIndex(stmt.IndexName);

            // 从 NovaTable 删除二级索引
            if (_tables.TryGetValue(stmt.TableName, out var table))
                table.DropSecondaryIndex(stmt.IndexName);

            // 持久化索引变更
            SchemaPersister.Save(_dbPath, schema);

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteCreateDatabase(CreateDatabaseStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        var dbPath = Path.Combine(Path.GetDirectoryName(_dbPath) ?? ".", stmt.DatabaseName);
        if (Directory.Exists(dbPath))
        {
            if (stmt.IfNotExists) return new SqlResult { AffectedRows = 0 };
            throw new NovaException(ErrorCode.DatabaseExists, "Database '{0}' already exists", stmt.DatabaseName);
        }

        Directory.CreateDirectory(dbPath);
        return new SqlResult { AffectedRows = 0 };
    }

    private SqlResult ExecuteDropDatabase(DropDatabaseStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        var dbPath = Path.Combine(Path.GetDirectoryName(_dbPath) ?? ".", stmt.DatabaseName);
        if (!Directory.Exists(dbPath))
        {
            if (stmt.IfExists) return new SqlResult { AffectedRows = 0 };
            throw new NovaException(ErrorCode.DatabaseNotFound, "Database '{0}' not found", stmt.DatabaseName);
        }

        Directory.Delete(dbPath, recursive: true);
        return new SqlResult { AffectedRows = 0 };
    }

    private SqlResult ExecuteAlterTable(AlterTableStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        {
            if (!_schemas.TryGetValue(stmt.TableName, out var schema))
                throw new NovaException(ErrorCode.TableNotFound, "Table '{0}' not found", stmt.TableName);

            switch (stmt.Action)
            {
                case AlterTableAction.AddColumn:
                    {
                        var colDef = stmt.ColumnDef!;
                        var dataType = DataTypeNames.Parse(colDef.DataTypeName);
                        var column = new ColumnDefinition(colDef.Name, dataType, !colDef.NotNull, colDef.IsPrimaryKey, GetColumnLength(colDef, dataType))
                        {
                            IsAutoIncrement = colDef.IsAutoIncrement,
                            DefaultValue = colDef.DefaultValue
                        };
                        if (colDef.Comment != null)
                            column.Comment = colDef.Comment;
                        schema.AddColumn(column);
                        break;
                    }

                case AlterTableAction.ModifyColumn:
                    {
                        var colDef = stmt.ColumnDef!;
                        var dataType = DataTypeNames.Parse(colDef.DataTypeName);
                        schema.ModifyColumn(colDef.Name, dataType, !colDef.NotNull, colDef.Comment, GetColumnLength(colDef, dataType));
                        break;
                    }

                case AlterTableAction.ChangeColumn:
                    {
                        var colDef = stmt.ColumnDef!;
                        var dataType = DataTypeNames.Parse(colDef.DataTypeName);
                        schema.RenameColumn(stmt.ColumnName!, colDef.Name);
                        schema.ModifyColumn(colDef.Name, dataType, !colDef.NotNull, colDef.Comment, GetColumnLength(colDef, dataType));
                        break;
                    }

                case AlterTableAction.DropColumn:
                    schema.RemoveColumn(stmt.ColumnName!);
                    break;

                case AlterTableAction.RenameTable:
                    RenameTableCore(stmt.TableName, stmt.NewTableName!);
                    break;

                case AlterTableAction.AddTableComment:
                    schema.Comment = stmt.Comment;
                    break;

                case AlterTableAction.AddColumnComment:
                    {
                        var col = schema.GetColumn(stmt.ColumnName!);
                        col.Comment = stmt.Comment;
                        break;
                    }

                default:
                    throw new NovaException(ErrorCode.NotSupported, "Unsupported ALTER TABLE action: {0}", stmt.Action);
            }

            // 主键列类型变化时索引键编码随之改变，按新类型重建主键索引
            if (_tables.TryGetValue(stmt.TableName, out var changedTable))
            {
                changedTable.ReopenPrimaryIndexForSchemaChange();
                changedTable.ReopenSecondaryIndexesForSchemaChange();
            }

            // 表结构变更，持久化更新
            SchemaPersister.Save(_dbPath, schema);

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteTruncateTable(TruncateTableStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        var table = GetTableInternal(stmt.TableName);

        // 直接清空表数据，比逐行 DELETE 更快
        table.Truncate();

        return new SqlResult { AffectedRows = 0 };
    }

    private SqlResult ExecuteRenameTable(RenameTableStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        foreach (var (from, to) in stmt.Items)
        {
            RenameTableCore(from, to);
        }

        return new SqlResult { AffectedRows = 0 };
    }

    /// <summary>重命名单张表：迁移文件组、更新元数据并重建表对象</summary>
    /// <remarks>调用方必须已持有元数据写锁</remarks>
    /// <param name="from">旧表名</param>
    /// <param name="to">新表名</param>
    private void RenameTableCore(String from, String to)
    {
        if (!_schemas.TryGetValue(from, out var schema))
            throw new NovaException(ErrorCode.TableNotFound, "Table '{0}' not found", from);

        if (String.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;

        if (_schemas.ContainsKey(to))
            throw new NovaException(ErrorCode.TableExists, "Table '{0}' already exists", to);

        // 先关闭旧表对象，释放数据文件句柄
        if (_tables.TryGetValue(from, out var table))
        {
            table.Dispose();
            _tables.Remove(from);
        }

        // 迁移 {旧表名}.xxx / {旧表名}_xxx 文件组（data/idx/wal/二级索引/schema）
        foreach (var path in Directory.GetFiles(_dbPath))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(from, StringComparison.OrdinalIgnoreCase) || name.Length <= from.Length + 1) continue;

            var sep = name[from.Length];
            if (sep != '.' && sep != '_') continue;

            var tail = name.Substring(from.Length + 1);
            if (!IsTableFileTail(tail)) continue;

            // 避免误伤同名表的文件，如重命名 a 时跳过 a_1 表
            var leaf = Path.GetFileNameWithoutExtension(name);
            if (leaf.Length > from.Length && _schemas.ContainsKey(leaf)) continue;

            File.Move(path, Path.Combine(_dbPath, to + name.Substring(from.Length)));
        }

        _schemas.Remove(from);
        schema.TableName = to;
        _schemas[to] = schema;

        _tables[to] = new NovaTable(schema, _dbPath, _options, _txManager);

        // 持久化新表名，旧 schema 文件已随文件组迁移为同名文件
        SchemaPersister.Save(_dbPath, schema);
    }

    /// <summary>判断文件名的尾部是否为表文件形态（data/idx/idxl/hidx/oidx/wal/rlog/schema，含分片与二级索引）</summary>
    /// <param name="tail">去掉“表名”和分隔符后的尾部</param>
    /// <returns>是否属于表文件</returns>
    private static Boolean IsTableFileTail(String tail)
    {
        if (tail is "data" or "idx" or "idxl" or "hidx" or "oidx" or "wal" or "rlog" or "schema") return true;

        // 分片或二级索引：{分片号|索引名}.{data|idx|idxl|wal}
        var dot = tail.LastIndexOf('.');
        if (dot <= 0) return false;

        return tail.Substring(dot + 1) is "data" or "idx" or "idxl" or "hidx" or "oidx" or "wal";
    }

    #endregion
}
