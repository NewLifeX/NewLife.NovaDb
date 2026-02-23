using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
    #region JOIN 辅助

    /// <summary>在合并行上对 JOIN 条件求值</summary>
    private Boolean EvaluateJoinCondition(SqlExpression expr, Object?[] combinedRow,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns,
        Dictionary<String, Object?>? parameters)
    {
        var result = EvaluateJoinExpression(expr, combinedRow, columns, parameters);
        return result is Boolean b && b;
    }

    /// <summary>在合并行上对表达式求值（支持 table.column 前缀解析）</summary>
    private Object? EvaluateJoinExpression(SqlExpression expr, Object?[] combinedRow,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns,
        Dictionary<String, Object?>? parameters)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                return lit.Value;

            case ColumnRefExpression colRef:
                var colIdx = ResolveJoinColumnIndex(colRef, columns);
                return combinedRow[colIdx];

            case ParameterExpression param:
                if (parameters == null || !parameters.TryGetValue(param.ParameterName, out var paramValue))
                    throw new NovaException(ErrorCode.InvalidArgument, $"Parameter '{param.ParameterName}' not found");
                return paramValue;

            case BinaryExpression binary:
                // 短路求值
                if (binary.Operator == BinaryOperator.And)
                {
                    var lv = EvaluateJoinExpression(binary.Left, combinedRow, columns, parameters);
                    if (lv is Boolean lb && !lb) return false;
                    var rv = EvaluateJoinExpression(binary.Right, combinedRow, columns, parameters);
                    return Convert.ToBoolean(lv) && Convert.ToBoolean(rv);
                }
                if (binary.Operator == BinaryOperator.Or)
                {
                    var lv = EvaluateJoinExpression(binary.Left, combinedRow, columns, parameters);
                    if (lv is Boolean lb && lb) return true;
                    var rv = EvaluateJoinExpression(binary.Right, combinedRow, columns, parameters);
                    return Convert.ToBoolean(lv) || Convert.ToBoolean(rv);
                }

                var left = EvaluateJoinExpression(binary.Left, combinedRow, columns, parameters);
                var right = EvaluateJoinExpression(binary.Right, combinedRow, columns, parameters);

                return binary.Operator switch
                {
                    BinaryOperator.Equal => CompareValues(left, right) == 0,
                    BinaryOperator.NotEqual => CompareValues(left, right) != 0,
                    BinaryOperator.LessThan => CompareValues(left, right) < 0,
                    BinaryOperator.GreaterThan => CompareValues(left, right) > 0,
                    BinaryOperator.LessOrEqual => CompareValues(left, right) <= 0,
                    BinaryOperator.GreaterOrEqual => CompareValues(left, right) >= 0,
                    BinaryOperator.Add => ArithmeticOp(left, right, (a, b) => a + b),
                    BinaryOperator.Subtract => ArithmeticOp(left, right, (a, b) => a - b),
                    BinaryOperator.Multiply => ArithmeticOp(left, right, (a, b) => a * b),
                    BinaryOperator.Divide => ArithmeticOp(left, right, (a, b) => b != 0 ? a / b : throw new DivideByZeroException()),
                    BinaryOperator.Modulo => ArithmeticOp(left, right, (a, b) => b != 0 ? a % b : throw new DivideByZeroException()),
                    BinaryOperator.Like => EvaluateLike(left, right),
                    _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported operator: {binary.Operator}")
                };

            case UnaryExpression unary:
                var operand = EvaluateJoinExpression(unary.Operand, combinedRow, columns, parameters);
                return unary.Operator switch
                {
                    "NOT" => !(Convert.ToBoolean(operand)),
                    "-" => ArithmeticNegate(operand),
                    _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported unary operator: {unary.Operator}")
                };

            case IsNullExpression isNull:
                var val = EvaluateJoinExpression(isNull.Operand, combinedRow, columns, parameters);
                return isNull.IsNot ? val != null : val == null;

            default:
                throw new NovaException(ErrorCode.NotSupported, $"Unsupported expression type in JOIN: {expr.ExprType}");
        }
    }

    /// <summary>解析 JOIN 中的列引用（支持 table.column 和 无前缀）</summary>
    private static Int32 ResolveJoinColumnIndex(ColumnRefExpression colRef,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns)
    {
        if (colRef.TablePrefix != null)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (String.Equals(columns[i].Alias, colRef.TablePrefix, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(columns[i].Column, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            throw new NovaException(ErrorCode.InvalidArgument, $"Column '{colRef.TablePrefix}.{colRef.ColumnName}' not found");
        }

        // 无表前缀：按列名匹配（如有歧义取第一个）
        for (var i = 0; i < columns.Count; i++)
        {
            if (String.Equals(columns[i].Column, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new NovaException(ErrorCode.InvalidArgument, $"Column '{colRef.ColumnName}' not found");
    }

    /// <summary>构建 JOIN 查询的结果投影</summary>
    private SqlResult BuildJoinSelectResult(SelectStatement stmt, List<Object?[]> rows,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns,
        Dictionary<String, Object?>? parameters)
    {
        var result = new SqlResult();

        if (stmt.IsSelectAll)
        {
            result.ColumnNames = columns.Select(c => c.Column).ToArray();
            result.Rows = rows;
        }
        else
        {
            var colNames = new String[stmt.Columns.Count];
            for (var i = 0; i < stmt.Columns.Count; i++)
            {
                var col = stmt.Columns[i];
                if (col.Alias != null)
                    colNames[i] = col.Alias;
                else if (col.Expression is ColumnRefExpression cr)
                    colNames[i] = cr.ColumnName;
                else
                    colNames[i] = $"col{i}";
            }
            result.ColumnNames = colNames;

            foreach (var row in rows)
            {
                var outputRow = new Object?[stmt.Columns.Count];
                for (var i = 0; i < stmt.Columns.Count; i++)
                {
                    var col = stmt.Columns[i];
                    outputRow[i] = EvaluateJoinExpression(col.Expression, row, columns, parameters);
                }
                result.Rows.Add(outputRow);
            }
        }

        return result;
    }

    /// <summary>JOIN 结果的 ORDER BY</summary>
    private static List<Object?[]> ApplyJoinOrderBy(List<Object?[]> rows, List<OrderByClause> orderBy,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns)
    {
        return rows.OrderBy(r => 0, Comparer<Int32>.Default)
            .ThenBy(r => r, new JoinOrderByComparer(orderBy, columns))
            .ToList();
    }

    #endregion

    #region 辅助

    private NovaTable GetTable(String tableName)
    {
        using var _ = _metaLock.AcquireRead();
        if (!_tables.TryGetValue(tableName, out var table))
            throw new NovaException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");
        return table;
    }

    /// <summary>在已持有写锁的上下文中获取表引用（不再加锁）</summary>
    private NovaTable GetTableInternal(String tableName)
    {
        if (!_tables.TryGetValue(tableName, out var table))
            throw new NovaException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");
        return table;
    }

    private TableSchema GetSchema(String tableName)
    {
        using var _ = _metaLock.AcquireRead();
        if (!_schemas.TryGetValue(tableName, out var schema))
            throw new NovaException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");
        return schema;
    }

    /// <summary>获取表的架构定义</summary>
    /// <param name="tableName">表名</param>
    /// <returns>架构定义，不存在时返回 null</returns>
    public TableSchema? GetTableSchema(String tableName)
    {
        using var _ = _metaLock.AcquireRead();
        _schemas.TryGetValue(tableName, out var schema);
        return schema;
    }

    private static DataType ParseDataType(String typeName)
    {
        return typeName.ToUpper() switch
        {
            "BOOL" or "BOOLEAN" => DataType.Boolean,
            "INT" or "INT32" or "INTEGER" => DataType.Int32,
            "BIGINT" or "INT64" or "LONG" => DataType.Int64,
            "FLOAT" or "DOUBLE" or "REAL" => DataType.Double,
            "DECIMAL" or "NUMERIC" => DataType.Decimal,
            "CHAR" or "NCHAR" or "VARCHAR" or "TEXT" or "STRING" or "NVARCHAR" => DataType.String,
            "BINARY" or "VARBINARY" or "BYTES" or "BLOB" => DataType.Binary,
            "DATETIME" or "TIMESTAMP" or "DATE" => DataType.DateTime,
            "GEOPOINT" => DataType.GeoPoint,
            "VECTOR" => DataType.Vector,
            _ => throw new NovaException(ErrorCode.SyntaxError, $"Unknown data type: {typeName}")
        };
    }

    private static void ConvertRowTypes(Object?[] row, TableSchema schema)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] == null) continue;

            var colDef = schema.Columns[i];
            row[i] = ConvertValue(row[i]!, colDef.DataType);
        }
    }

    private static Object ConvertValue(Object value, DataType targetType)
    {
        return targetType switch
        {
            DataType.Boolean => Convert.ToBoolean(value),
            DataType.Int32 => Convert.ToInt32(value),
            DataType.Int64 => Convert.ToInt64(value),
            DataType.Double => Convert.ToDouble(value),
            DataType.Decimal => Convert.ToDecimal(value),
            DataType.String => Convert.ToString(value)!,
            DataType.DateTime => Convert.ToDateTime(value),
            DataType.Binary when value is Byte[] bytes => bytes,
            DataType.GeoPoint when value is GeoPoint gp => gp,
            DataType.Vector when value is Single[] vec => vec,
            _ => value
        };
    }

    private static Int32 CompareValues(Object? left, Object? right)
    {
        if (left == null && right == null) return 0;
        if (left == null) return -1;
        if (right == null) return 1;

        // 尝试数值比较
        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left).CompareTo(Convert.ToDouble(right));

        // 字符串比较
        return String.Compare(Convert.ToString(left), Convert.ToString(right), StringComparison.OrdinalIgnoreCase);
    }

    private static Boolean IsNumeric(Object? value) =>
        value is Int32 or Int64 or Double or Decimal or Single or Byte or Int16 or UInt32 or UInt64;

    /// <summary>计算两个向量的余弦相似度</summary>
    /// <param name="a">向量 A</param>
    /// <param name="b">向量 B</param>
    /// <returns>余弦相似度（-1 到 1）</returns>
    private static Double CosineSimilarity(Single[] a, Single[] b)
    {
        if (a.Length != b.Length)
            throw new NovaException(ErrorCode.InvalidArgument, $"Vector dimensions mismatch: {a.Length} vs {b.Length}");

        Double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (Double)b[i];
            normA += a[i] * (Double)a[i];
            normB += b[i] * (Double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }

    /// <summary>计算两个向量的欧氏距离</summary>
    /// <param name="a">向量 A</param>
    /// <param name="b">向量 B</param>
    /// <returns>欧氏距离</returns>
    private static Double EuclideanDistance(Single[] a, Single[] b)
    {
        if (a.Length != b.Length)
            throw new NovaException(ErrorCode.InvalidArgument, $"Vector dimensions mismatch: {a.Length} vs {b.Length}");

        Double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var diff = a[i] - (Double)b[i];
            sum += diff * diff;
        }

        return Math.Sqrt(sum);
    }

    /// <summary>计算两个向量的点积</summary>
    /// <param name="a">向量 A</param>
    /// <param name="b">向量 B</param>
    /// <returns>点积</returns>
    private static Double DotProduct(Single[] a, Single[] b)
    {
        if (a.Length != b.Length)
            throw new NovaException(ErrorCode.InvalidArgument, $"Vector dimensions mismatch: {a.Length} vs {b.Length}");

        Double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * (Double)b[i];
        }

        return sum;
    }

    /// <summary>执行 VECTOR_NEAREST 函数：在指定表中查找与查询向量最相似的 Top-K 行</summary>
    /// <param name="func">函数表达式</param>
    /// <param name="row">当前行上下文</param>
    /// <param name="schema">当前查询的表架构</param>
    /// <param name="parameters">SQL 参数</param>
    /// <returns>JSON 格式的结果字符串，包含主键和相似度分数</returns>
    private Object? EvaluateVectorNearest(FunctionExpression func, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        // VECTOR_NEAREST(query_vector, 'table_name', k, 'metric')
        // query_vector: 查询向量
        // table_name: 目标表名（字符串字面量）
        // k: 返回前 K 个最相似结果
        // metric: 距离度量方式，可选 'cosine'（默认）、'euclidean'、'dot_product'
        var args = new List<Object?>();
        foreach (var argExpr in func.Arguments)
        {
            args.Add(EvaluateExpression(argExpr, row, schema, parameters));
        }

        if (args.Count < 3) throw new NovaException(ErrorCode.InvalidArgument, "VECTOR_NEAREST requires at least 3 arguments (query, table, k)");
        if (args[0] == null) return null;

        var queryVec = (Single[])args[0]!;
        var tableName = Convert.ToString(args[1])!;
        var k = Convert.ToInt32(args[2]);
        var metric = args.Count > 3 && args[3] != null ? Convert.ToString(args[3])!.ToLower() : "cosine";

        if (k <= 0) throw new NovaException(ErrorCode.InvalidArgument, "VECTOR_NEAREST k must be positive");

        // 获取目标表和架构
        var targetTable = GetTable(tableName);
        var targetSchema = GetSchema(tableName);

        // 查找第一个 Vector 类型的列
        var vectorColIdx = -1;
        for (var i = 0; i < targetSchema.Columns.Count; i++)
        {
            if (targetSchema.Columns[i].DataType == DataType.Vector)
            {
                vectorColIdx = i;
                break;
            }
        }
        if (vectorColIdx < 0)
            throw new NovaException(ErrorCode.InvalidArgument, $"Table '{tableName}' has no Vector column");

        // 获取主键列索引
        var pkIdx = targetSchema.PrimaryKeyIndex ?? 0;

        // 扫描所有行，计算相似度
        using var tx = _txManager.BeginTransaction();
        var allRows = targetTable.GetAll(tx);

        var scored = new List<(Object? PrimaryKey, Double Score)>();
        foreach (var r in allRows)
        {
            if (r[vectorColIdx] is not Single[] vec) continue;
            if (vec.Length != queryVec.Length) continue;

            Double score = metric switch
            {
                "cosine" => CosineSimilarity(queryVec, vec),
                "euclidean" => -EuclideanDistance(queryVec, vec), // 取负使得距离越小排名越高
                "dot_product" or "dot" => DotProduct(queryVec, vec),
                _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown metric: {metric}")
            };

            scored.Add((r[pkIdx], score));
        }

        // 按分数降序排列，取 Top-K
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (scored.Count > k) scored.RemoveRange(k, scored.Count - k);

        // 构建结果字符串："pk1:score1,pk2:score2,..."
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < scored.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(scored[i].PrimaryKey);
            sb.Append(':');
            sb.Append(scored[i].Score.ToString("F6"));
        }

        return sb.ToString();
    }

    private static Object? ArithmeticOp(Object? left, Object? right, Func<Double, Double, Double> op)
    {
        if (left == null || right == null) return null;
        return op(Convert.ToDouble(left), Convert.ToDouble(right));
    }

    private static Object? ArithmeticNegate(Object? value)
    {
        if (value == null) return null;
        return -Convert.ToDouble(value);
    }

    private static Object? EvaluateLike(Object? left, Object? right)
    {
        if (left == null || right == null) return false;

        var str = Convert.ToString(left)!;
        var pattern = Convert.ToString(right)!;

        // 简单 LIKE 实现：% 匹配任意字符序列, _ 匹配单个字符
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(str, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static List<Object?[]> ApplyOrderBy(List<Object?[]> rows, List<OrderByClause> orderBy, TableSchema schema)
    {
        return rows.OrderBy(r => 0, Comparer<Int32>.Default)
            .ThenBy(r => r, new OrderByComparer(orderBy, schema))
            .ToList();
    }

    private static String BuildGroupKey(Object?[] row, List<String> groupByColumns, TableSchema schema)
    {
        var parts = new List<String>();
        foreach (var colName in groupByColumns)
        {
            var idx = schema.GetColumnIndex(colName);
            parts.Add(Convert.ToString(row[idx]) ?? "NULL");
        }
        return String.Join("|", parts);
    }

    private static String[] BuildColumnNames(SelectStatement stmt, TableSchema schema)
    {
        var names = new String[stmt.Columns.Count];
        for (var i = 0; i < stmt.Columns.Count; i++)
        {
            var col = stmt.Columns[i];
            if (col.Alias != null)
            {
                names[i] = col.Alias;
            }
            else if (col.Expression is ColumnRefExpression colRef)
            {
                names[i] = colRef.ColumnName;
            }
            else if (col.Expression is FunctionExpression func)
            {
                names[i] = func.FunctionName;
            }
            else
            {
                names[i] = $"col{i}";
            }
        }
        return names;
    }

    private static Boolean HasAggregateFunction(SelectStatement stmt)
    {
        foreach (var col in stmt.Columns)
        {
            if (col.Expression is FunctionExpression func && func.IsAggregate)
                return true;
        }
        return false;
    }

    /// <summary>ORDER BY 比较器</summary>
    private class OrderByComparer(List<OrderByClause> orderBy, TableSchema schema) : IComparer<Object?[]>
    {
        public Int32 Compare(Object?[]? x, Object?[]? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            foreach (var clause in orderBy)
            {
                var idx = schema.GetColumnIndex(clause.ColumnName);
                var cmp = CompareValues(x[idx], y[idx]);

                if (cmp != 0)
                    return clause.Descending ? -cmp : cmp;
            }

            return 0;
        }
    }

    /// <summary>JOIN 结果 ORDER BY 比较器</summary>
    private class JoinOrderByComparer(List<OrderByClause> orderBy,
        List<(String Alias, String Column, Int32 TableIndex, Int32 ColIndex)> columns) : IComparer<Object?[]>
    {
        public Int32 Compare(Object?[]? x, Object?[]? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            foreach (var clause in orderBy)
            {
                var idx = -1;
                for (var i = 0; i < columns.Count; i++)
                {
                    if (String.Equals(columns[i].Column, clause.ColumnName, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }

                if (idx < 0) continue;

                var cmp = CompareValues(x[idx], y[idx]);
                if (cmp != 0)
                    return clause.Descending ? -cmp : cmp;
            }

            return 0;
        }
    }

    #endregion
}
