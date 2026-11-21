using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;

#nullable enable

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
    #region 列裁剪

    /// <summary>计算 SELECT 实际引用到的列掩码，用于只反序列化需要的列</summary>
    /// <param name="stmt">查询语句</param>
    /// <param name="schema">表结构</param>
    /// <returns>列掩码；无法确定引用集合（含通配符、子查询、窗口函数、跨表列等）时返回 null，表示读取全部列</returns>
    /// <remarks>
    /// 漏算一列会导致该列读成 null 而静默出错，因此遇到任何无法分析的构造一律放弃裁剪（返回 null）。
    /// 覆盖范围：WHERE、HAVING、SELECT 表达式、ORDER BY、GROUP BY。
    /// </remarks>
    private static Boolean[]? BuildNeededColumnMask(SelectStatement stmt, TableSchema schema)
    {
        // JOIN 涉及多张表，列名可能重名，不做裁剪
        if (stmt.HasJoin) return null;
        if (stmt.GroupByWindows != null && stmt.GroupByWindows.Any(e => e != null)) return null;

        var ordinals = new HashSet<Int32>();

        foreach (var column in stmt.Columns)
        {
            // SELECT * 需要全部列
            if (column.IsWildcard) return null;
            if (!CollectColumns(column.Expression, stmt, schema, ordinals)) return null;
        }

        if (stmt.Where != null && !CollectColumns(stmt.Where, stmt, schema, ordinals)) return null;
        if (stmt.Having != null && !CollectColumns(stmt.Having, stmt, schema, ordinals)) return null;

        if (stmt.GroupBy != null)
        {
            foreach (var name in stmt.GroupBy)
            {
                var ordinal = schema.GetColumnIndex(name);
                if (ordinal < 0) return null;
                ordinals.Add(ordinal);
            }
        }

        if (stmt.OrderBy != null)
        {
            foreach (var orderBy in stmt.OrderBy)
            {
                var ordinal = FindColumnOrdinal(schema, orderBy.ColumnName);
                if (ordinal < 0) return null;
                ordinals.Add(ordinal);
            }
        }

        var mask = new Boolean[schema.Columns.Count];
        foreach (var ordinal in ordinals)
        {
            if (ordinal >= 0 && ordinal < mask.Length) mask[ordinal] = true;
        }

        return mask;
    }

    /// <summary>收集表达式引用到的列序号</summary>
    /// <param name="expression">表达式</param>
    /// <param name="stmt">查询语句，用于判断表前缀</param>
    /// <param name="schema">表结构</param>
    /// <param name="ordinals">收集到的列序号</param>
    /// <returns>能否确定引用集合；遇到无法分析的构造返回 false</returns>
    private static Boolean CollectColumns(SqlExpression expression, SelectStatement stmt, TableSchema schema, HashSet<Int32> ordinals)
    {
        switch (expression)
        {
            case ColumnRefExpression columnRef:
                {
                    // 带表前缀且不是本表/别名时无法定位（可能是 JOIN 的另一张表），放弃裁剪
                    if (!columnRef.TablePrefix.IsNullOrEmpty()
                        && !String.Equals(columnRef.TablePrefix, stmt.TableAlias, StringComparison.OrdinalIgnoreCase)
                        && !String.Equals(columnRef.TablePrefix, stmt.TableName, StringComparison.OrdinalIgnoreCase))
                        return false;

                    // COUNT(*) 一类通配引用解析成列名 "*"，不指向具体列
                    if (columnRef.ColumnName == "*") return true;

                    var ordinal = FindColumnOrdinal(schema, columnRef.ColumnName);

                    // 列名解析不到（可能是 SELECT 别名）时无法判断它依赖哪一列，放弃裁剪
                    if (ordinal < 0) return false;

                    ordinals.Add(ordinal);
                    return true;
                }

            case BinaryExpression binary:
                return CollectColumns(binary.Left, stmt, schema, ordinals)
                    && CollectColumns(binary.Right, stmt, schema, ordinals);

            case UnaryExpression unary:
                return CollectColumns(unary.Operand, stmt, schema, ordinals);

            case IsNullExpression isNull:
                return CollectColumns(isNull.Operand, stmt, schema, ordinals);

            case CastExpression cast:
                return CollectColumns(cast.Operand, stmt, schema, ordinals);

            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                {
                    if (!CollectColumns(argument, stmt, schema, ordinals)) return false;
                }
                return true;

            case InExpression inExpr:
                // 子查询需要单独执行，放弃裁剪
                if (inExpr.Subquery != null) return false;
                if (!CollectColumns(inExpr.Operand, stmt, schema, ordinals)) return false;
                foreach (var value in inExpr.Values)
                {
                    if (!CollectColumns(value, stmt, schema, ordinals)) return false;
                }
                return true;

            case CaseExpression caseExpr:
                foreach (var (when, then) in caseExpr.WhenClauses)
                {
                    if (!CollectColumns(when, stmt, schema, ordinals)) return false;
                    if (!CollectColumns(then, stmt, schema, ordinals)) return false;
                }
                return caseExpr.ElseExpression == null || CollectColumns(caseExpr.ElseExpression, stmt, schema, ordinals);

            case LiteralExpression:
            case ParameterExpression:
                return true;

            default:
                return false;
        }
    }

    /// <summary>按列名查找列序号，找不到返回 -1</summary>
    /// <param name="schema">表结构</param>
    /// <param name="columnName">列名</param>
    /// <returns>列序号，未找到返回 -1</returns>
    private static Int32 FindColumnOrdinal(TableSchema schema, String columnName)
    {
        var columns = schema.Columns;
        for (var i = 0; i < columns.Count; i++)
        {
            if (String.Equals(columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    #endregion
}
