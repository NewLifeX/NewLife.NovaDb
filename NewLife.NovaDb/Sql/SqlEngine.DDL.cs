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
                throw new NovaException(ErrorCode.TableExists, $"Table '{stmt.TableName}' already exists");
            }

            var schema = new TableSchema(stmt.TableName);

            // 设置引擎名称，未指定时默认为 Nova
            schema.EngineName = stmt.EngineName ?? "Nova";

            // 设置表注释
            if (stmt.Comment != null)
                schema.Comment = stmt.Comment;

            foreach (var colDef in stmt.Columns)
            {
                var dataType = ParseDataType(colDef.DataTypeName);
                var column = new ColumnDefinition(colDef.Name, dataType, !colDef.NotNull, colDef.IsPrimaryKey);
                if (colDef.Comment != null)
                    column.Comment = colDef.Comment;
                schema.AddColumn(column);
            }

            var table = new NovaTable(schema, _dbPath, _options, _txManager);

            _schemas[stmt.TableName] = schema;
            _tables[stmt.TableName] = table;

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
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");
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

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteCreateIndex(CreateIndexStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        {
            if (!_schemas.TryGetValue(stmt.TableName, out var schema))
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");

            // 验证索引列存在
            foreach (var col in stmt.Columns)
            {
                if (!schema.HasColumn(col))
                    throw new NovaException(ErrorCode.InvalidArgument, $"Column '{col}' not found in table '{stmt.TableName}'");
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

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteDropIndex(DropIndexStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        {
            if (!_schemas.TryGetValue(stmt.TableName, out var schema))
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");

            // 从 Schema 移除索引定义
            schema.RemoveIndex(stmt.IndexName);

            // 从 NovaTable 删除二级索引
            if (_tables.TryGetValue(stmt.TableName, out var table))
                table.DropSecondaryIndex(stmt.IndexName);

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
            throw new NovaException(ErrorCode.DatabaseExists, $"Database '{stmt.DatabaseName}' already exists");
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
            throw new NovaException(ErrorCode.DatabaseNotFound, $"Database '{stmt.DatabaseName}' not found");
        }

        Directory.Delete(dbPath, recursive: true);
        return new SqlResult { AffectedRows = 0 };
    }

    private SqlResult ExecuteAlterTable(AlterTableStatement stmt)
    {
        using var _ = _metaLock.AcquireWrite();
        {
            if (!_schemas.TryGetValue(stmt.TableName, out var schema))
                throw new NovaException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");

            switch (stmt.Action)
            {
                case AlterTableAction.AddColumn:
                    {
                        var colDef = stmt.ColumnDef!;
                        var dataType = ParseDataType(colDef.DataTypeName);
                        var column = new ColumnDefinition(colDef.Name, dataType, !colDef.NotNull, colDef.IsPrimaryKey);
                        if (colDef.Comment != null)
                            column.Comment = colDef.Comment;
                        schema.AddColumn(column);
                        break;
                    }

                case AlterTableAction.ModifyColumn:
                    {
                        var colDef = stmt.ColumnDef!;
                        var dataType = ParseDataType(colDef.DataTypeName);
                        schema.ModifyColumn(colDef.Name, dataType, !colDef.NotNull, colDef.Comment);
                        break;
                    }

                case AlterTableAction.DropColumn:
                    schema.RemoveColumn(stmt.ColumnName!);
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
                    throw new NovaException(ErrorCode.NotSupported, $"Unsupported ALTER TABLE action: {stmt.Action}");
            }

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

    #endregion
}
