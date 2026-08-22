using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
    #region DML 执行

    private SqlResult ExecuteInsert(InsertStatement stmt, Dictionary<String, Object?>? parameters, Transaction? tx = null)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var localTx = tx == null ? _txManager.BeginTransaction() : null;
        var effectiveTx = tx ?? localTx!;
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

            // 处理自增主键：显式标记 AUTO_INCREMENT，或整数主键值为空时，均自动分配下一个 ID
            var pkCol = schema.GetPrimaryKeyColumn();
            var isAuto = pkCol != null && (pkCol.IsAutoIncrement
                || pkCol.DataType == DataType.Int32 || pkCol.DataType == DataType.Int64);
            if (pkCol != null && isAuto && row[pkCol.Ordinal] == null)
            {
                EnsureAutoIncrementSeed(table, schema, pkCol);
                var nextId = ++schema.NextAutoIncrement;
                row[pkCol.Ordinal] = pkCol.DataType == DataType.Int32 ? (Object)(Int32)nextId : nextId;
                LastInsertId = nextId;
            }
            else if (pkCol != null && isAuto && row[pkCol.Ordinal] != null)
            {
                // 用户显式提供值，更新种子，避免后续冲突
                try
                {
                    var v = Convert.ToInt64(row[pkCol.Ordinal]);
                    if (v > schema.NextAutoIncrement) schema.NextAutoIncrement = v;
                    LastInsertId = v;
                }
                catch { }
            }

            // INSERT IGNORE：唯一键冲突时跳过当前行
            if (stmt.Ignore)
            {
                try
                {
                    table.Insert(effectiveTx, row);
                    affectedRows++;
                }
                catch (NovaException ex) when (ex.Code == ErrorCode.UniqueConstraintViolation)
                {
                    // 跳过冲突行，不报错
                }
            }
            else
            {
                table.Insert(effectiveTx, row);
                affectedRows++;
            }
        }

        if (tx == null) localTx!.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteReplace(ReplaceStatement stmt, Dictionary<String, Object?>? parameters, Transaction? tx = null)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var localTx = tx == null ? _txManager.BeginTransaction() : null;
        var effectiveTx = tx ?? localTx!;
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
                        $"REPLACE values count ({values.Count}) does not match column count ({schema.Columns.Count})");

                for (var i = 0; i < values.Count; i++)
                {
                    row[i] = EvaluateExpression(values[i], null, schema, parameters);
                }
            }

            ConvertRowTypes(row, schema);

            // REPLACE 语义：先删除唯一键冲突的行
            var deleted = DeleteConflictingRows(table, effectiveTx, row, schema);

            // 处理自增主键
            var pkCol = schema.GetPrimaryKeyColumn();
            var isAuto = pkCol != null && (pkCol.IsAutoIncrement
                || pkCol.DataType == DataType.Int32 || pkCol.DataType == DataType.Int64);
            if (pkCol != null && isAuto && row[pkCol.Ordinal] == null)
            {
                EnsureAutoIncrementSeed(table, schema, pkCol);
                var nextId = ++schema.NextAutoIncrement;
                row[pkCol.Ordinal] = pkCol.DataType == DataType.Int32 ? (Object)(Int32)nextId : nextId;
                LastInsertId = nextId;
            }
            else if (pkCol != null && isAuto && row[pkCol.Ordinal] != null)
            {
                try
                {
                    var v = Convert.ToInt64(row[pkCol.Ordinal]);
                    if (v > schema.NextAutoIncrement) schema.NextAutoIncrement = v;
                    LastInsertId = v;
                }
                catch { }
            }

            table.Insert(effectiveTx, row);
            affectedRows++;
        }

        if (tx == null) localTx!.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    /// <summary>根据主键或唯一索引删除冲突行</summary>
    /// <returns>是否删除了至少一行</returns>
    private static Boolean DeleteConflictingRows(NovaTable table, Transaction tx, Object?[] row, TableSchema schema)
    {
        var pkCol = schema.GetPrimaryKeyColumn();

        // 遍历所有唯一索引，查找并删除冲突行
        var deleted = false;
        var allRows = table.GetAll(tx);

        foreach (var existingRow in allRows)
        {
            var conflict = false;

            // 检查主键冲突
            if (pkCol != null)
            {
                var existingPk = existingRow[pkCol.Ordinal];
                var newPk = row[pkCol.Ordinal];
                if (newPk != null && Equals(newPk, existingPk))
                    conflict = true;
            }

            // 检查唯一索引冲突
            if (!conflict)
            {
                foreach (var idx in schema.Indexes)
                {
                    if (!idx.IsUnique) continue;

                    var idxMatch = true;
                    foreach (var colName in idx.Columns)
                    {
                        var colIdx = schema.GetColumnIndex(colName);
                        var existingVal = existingRow[colIdx];
                        var newVal = row[colIdx];
                        if (newVal == null || !Equals(newVal, existingVal))
                        {
                            idxMatch = false;
                            break;
                        }
                    }

                    if (idxMatch)
                    {
                        conflict = true;
                        break;
                    }
                }
            }

            if (conflict)
            {
                var existingPk = pkCol != null ? existingRow[pkCol.Ordinal] : null;
                if (existingPk != null)
                {
                    table.Delete(tx, existingPk);
                    deleted = true;
                }
            }
        }

        return deleted;
    }

    private SqlResult ExecuteUpdate(UpdateStatement stmt, Dictionary<String, Object?>? parameters, Transaction? tx = null)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);
        var pkCol = schema.GetPrimaryKeyColumn()
            ?? throw new NovaException(ErrorCode.InvalidArgument, "UPDATE requires a table with a primary key");

        using var localTx = tx == null ? _txManager.BeginTransaction() : null;
        var effectiveTx = tx ?? localTx!;
        var allRows = table.GetAll(effectiveTx);
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
            var pkValue = row[pkCol.Ordinal]!;
            table.Update(effectiveTx, pkValue, newRow);
            affectedRows++;
        }

        if (tx == null) localTx!.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteDelete(DeleteStatement stmt, Dictionary<String, Object?>? parameters, Transaction? tx = null)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);
        var pkCol = schema.GetPrimaryKeyColumn()
            ?? throw new NovaException(ErrorCode.InvalidArgument, "DELETE requires a table with a primary key");

        using var localTx = tx == null ? _txManager.BeginTransaction() : null;
        var effectiveTx = tx ?? localTx!;
        var allRows = table.GetAll(effectiveTx);
        var affectedRows = 0;

        foreach (var row in allRows)
        {
            if (stmt.Where != null && !EvaluateCondition(stmt.Where, row, schema, parameters))
                continue;

            var pkValue = row[pkCol.Ordinal]!;
            if (table.Delete(effectiveTx, pkValue))
                affectedRows++;
        }

        if (tx == null) localTx!.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteUpsert(UpsertStatement stmt, Dictionary<String, Object?>? parameters, Transaction? tx = null)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);
        var pkCol = schema.GetPrimaryKeyColumn()
            ?? throw new NovaException(ErrorCode.InvalidArgument, "UPSERT requires a table with a primary key");

        using var localTx = tx == null ? _txManager.BeginTransaction() : null;
        var effectiveTx = tx ?? localTx!;
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
            var existingRow = table.Get(effectiveTx, pkValue);
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
                table.Update(effectiveTx, pkValue, newRow);
            }
            else
            {
                // 不存在：执行 INSERT 逻辑
                table.Insert(effectiveTx, row);
            }

            affectedRows++;
        }

        if (tx == null) localTx!.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    private SqlResult ExecuteMerge(MergeStatement stmt, Dictionary<String, Object?>? parameters, Transaction? tx = null)
    {
        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);
        var pkCol = schema.GetPrimaryKeyColumn()
            ?? throw new NovaException(ErrorCode.InvalidArgument, "MERGE requires a table with a primary key");

        using var localTx = tx == null ? _txManager.BeginTransaction() : null;
        var effectiveTx = tx ?? localTx!;
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
                existingRow = table.Get(effectiveTx, pkValue);
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
                        existingRow = table.Get(effectiveTx, existingPkValue);
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
                table.Update(effectiveTx, existingPkValue!, newRow);
            }
            else
            {
                // 无冲突：执行 INSERT
                table.Insert(effectiveTx, row);
            }

            affectedRows++;
        }

        if (tx == null) localTx!.Commit();
        return new SqlResult { AffectedRows = affectedRows };
    }

    /// <summary>首次需要自增时，从已存在的数据中初始化种子，使重启后仍可继续递增</summary>
    private void EnsureAutoIncrementSeed(NovaTable table, TableSchema schema, ColumnDefinition pkCol)
    {
        if (schema.NextAutoIncrement > 0) return;

        Int64 max = 0;
        using var tx = _txManager.BeginTransaction();
        foreach (var existing in table.GetAll(tx))
        {
            var v = existing[pkCol.Ordinal];
            if (v == null) continue;
            try
            {
                var n = Convert.ToInt64(v);
                if (n > max) max = n;
            }
            catch { }
        }
        tx.Commit();
        schema.NextAutoIncrement = max;
    }

    #endregion
}
