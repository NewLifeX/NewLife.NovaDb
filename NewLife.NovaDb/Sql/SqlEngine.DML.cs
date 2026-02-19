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

    #endregion
}
