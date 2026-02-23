using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
    #region DML 执行

    private SqlResult ExecuteInsert(InsertStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var tx = _txManager.BeginTransaction();
        var affectedRows = 0;

        foreach (var values in stmt.ValuesList)
        {
            var row = new Object?[schema.Columns.Count];

            if (stmt.Columns != null)
            {
                // 按指定列名填充
                for (var i = 0; i < stmt.Columns.Count; i++)
                {
                    var colIdx = schema.GetColumnIndex(stmt.Columns[i]);
                    row[colIdx] = EvaluateExpression(values[i], null, schema, parameters);
                }
            }
            else
            {
                // 按列序号填充
                if (values.Count != schema.Columns.Count)
                    throw new NovaException(ErrorCode.InvalidArgument,
                        $"INSERT values count ({values.Count}) does not match column count ({schema.Columns.Count})");

                for (var i = 0; i < values.Count; i++)
                {
                    row[i] = EvaluateExpression(values[i], null, schema, parameters);
                }
            }

            // 类型转换
            ConvertRowTypes(row, schema);

            table.Insert(tx, row);
            affectedRows++;
        }

        tx.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteUpdate(UpdateStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var tx = _txManager.BeginTransaction();
        var allRows = table.GetAll(tx);
        var affectedRows = 0;

        foreach (var row in allRows)
        {
            if (stmt.Where != null && !EvaluateCondition(stmt.Where, row, schema, parameters))
                continue;

            // 构建新行
            var newRow = new Object?[schema.Columns.Count];
            Array.Copy(row, newRow, row.Length);

            // 应用 SET 子句
            foreach (var (column, value) in stmt.SetClauses)
            {
                var colIdx = schema.GetColumnIndex(column);
                newRow[colIdx] = EvaluateExpression(value, row, schema, parameters);
            }

            ConvertRowTypes(newRow, schema);

            // 获取主键值
            var pkCol = schema.GetPrimaryKeyColumn()!;
            var pkValue = row[pkCol.Ordinal]!;
            table.Update(tx, pkValue, newRow);
            affectedRows++;
        }

        tx.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteDelete(DeleteStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var tx = _txManager.BeginTransaction();
        var allRows = table.GetAll(tx);
        var affectedRows = 0;

        foreach (var row in allRows)
        {
            if (stmt.Where != null && !EvaluateCondition(stmt.Where, row, schema, parameters))
                continue;

            var pkCol = schema.GetPrimaryKeyColumn()!;
            var pkValue = row[pkCol.Ordinal]!;
            if (table.Delete(tx, pkValue))
                affectedRows++;
        }

        tx.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteUpsert(UpsertStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);
        var pkCol = schema.GetPrimaryKeyColumn()
            ?? throw new NovaException(ErrorCode.InvalidArgument, "UPSERT requires a table with a primary key");

        using var tx = _txManager.BeginTransaction();
        var affectedRows = 0;

        foreach (var values in stmt.ValuesList)
        {
            var row = new Object?[schema.Columns.Count];

            if (stmt.Columns != null)
            {
                for (var i = 0; i < stmt.Columns.Count; i++)
                {
                    var colIdx = schema.GetColumnIndex(stmt.Columns[i]);
                    row[colIdx] = EvaluateExpression(values[i], null, schema, parameters);
                }
            }
            else
            {
                if (values.Count != schema.Columns.Count)
                    throw new NovaException(ErrorCode.InvalidArgument,
                        $"INSERT values count ({values.Count}) does not match column count ({schema.Columns.Count})");

                for (var i = 0; i < values.Count; i++)
                {
                    row[i] = EvaluateExpression(values[i], null, schema, parameters);
                }
            }

            ConvertRowTypes(row, schema);

            var pkValue = row[pkCol.Ordinal];
            if (pkValue == null)
                throw new NovaException(ErrorCode.InvalidArgument, "Primary key value cannot be null for UPSERT");

            // 尝试查找已有行
            var existingRow = table.Get(tx, pkValue);
            if (existingRow != null)
            {
                // 已存在：执行 UPDATE 逻辑
                var newRow = new Object?[schema.Columns.Count];
                Array.Copy(existingRow, newRow, existingRow.Length);

                // 应用 ON DUPLICATE KEY UPDATE 子句
                foreach (var (column, value) in stmt.UpdateClauses)
                {
                    var colIdx = schema.GetColumnIndex(column);
                    newRow[colIdx] = EvaluateExpression(value, existingRow, schema, parameters);
                }

                ConvertRowTypes(newRow, schema);
                table.Update(tx, pkValue, newRow);
            }
            else
            {
                // 不存在：执行 INSERT 逻辑
                table.Insert(tx, row);
            }

            affectedRows++;
        }

        tx.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteMerge(MergeStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);
        var pkCol = schema.GetPrimaryKeyColumn()
            ?? throw new NovaException(ErrorCode.InvalidArgument, "MERGE requires a table with a primary key");

        using var tx = _txManager.BeginTransaction();
        var affectedRows = 0;

        foreach (var values in stmt.ValuesList)
        {
            var row = new Object?[schema.Columns.Count];

            if (stmt.Columns != null)
            {
                for (var i = 0; i < stmt.Columns.Count; i++)
                {
                    var colIdx = schema.GetColumnIndex(stmt.Columns[i]);
                    row[colIdx] = EvaluateExpression(values[i], null, schema, parameters);
                }
            }
            else
            {
                if (values.Count != schema.Columns.Count)
                    throw new NovaException(ErrorCode.InvalidArgument,
                        $"MERGE values count ({values.Count}) does not match column count ({schema.Columns.Count})");

                for (var i = 0; i < values.Count; i++)
                {
                    row[i] = EvaluateExpression(values[i], null, schema, parameters);
                }
            }

            ConvertRowTypes(row, schema);

            // 检测唯一冲突：优先检查主键，再检查唯一索引
            Object? existingPkValue = null;
            Object?[]? existingRow = null;

            // 1. 检查主键冲突
            var pkValue = row[pkCol.Ordinal];
            if (pkValue != null)
            {
                existingRow = table.Get(tx, pkValue);
                if (existingRow != null)
                    existingPkValue = pkValue;
            }

            // 2. 若无主键冲突，检查唯一索引冲突
            if (existingRow == null)
            {
                foreach (var indexDef in schema.Indexes)
                {
                    if (!indexDef.IsUnique) continue;

                    var indexKeyValues = new Object?[indexDef.Columns.Count];
                    for (var i = 0; i < indexDef.Columns.Count; i++)
                    {
                        var colIdx = schema.GetColumnIndex(indexDef.Columns[i]);
                        indexKeyValues[i] = row[colIdx];
                    }

                    var matchedPks = table.LookupByIndex(indexDef.IndexName, indexKeyValues);
                    if (matchedPks != null && matchedPks.Count > 0)
                    {
                        existingPkValue = matchedPks[0];
                        existingRow = table.Get(tx, existingPkValue);
                        break;
                    }
                }
            }

            if (existingRow != null)
            {
                // 冲突：用 VALUES 数据更新非主键列
                var newRow = new Object?[schema.Columns.Count];
                Array.Copy(existingRow, newRow, existingRow.Length);

                if (stmt.Columns != null)
                {
                    for (var i = 0; i < stmt.Columns.Count; i++)
                    {
                        var colIdx = schema.GetColumnIndex(stmt.Columns[i]);
                        if (colIdx == pkCol.Ordinal) continue;
                        newRow[colIdx] = row[colIdx];
                    }
                }
                else
                {
                    for (var i = 0; i < schema.Columns.Count; i++)
                    {
                        if (i == pkCol.Ordinal) continue;
                        newRow[i] = row[i];
                    }
                }

                ConvertRowTypes(newRow, schema);
                table.Update(tx, existingPkValue!, newRow);
            }
            else
            {
                // 无冲突：执行 INSERT
                table.Insert(tx, row);
            }

            affectedRows++;
        }

        tx.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    #endregion
}
