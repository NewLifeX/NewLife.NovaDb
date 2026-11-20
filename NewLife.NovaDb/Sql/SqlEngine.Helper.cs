using System.Globalization;
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
                    throw new NovaException(ErrorCode.InvalidArgument, "Parameter '{0}' not found", param.ParameterName);
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
                    _ => throw new NovaException(ErrorCode.NotSupported, "Unsupported operator: {0}", binary.Operator)
                };

            case UnaryExpression unary:
                var operand = EvaluateJoinExpression(unary.Operand, combinedRow, columns, parameters);
                return unary.Operator switch
                {
                    "NOT" => !(Convert.ToBoolean(operand)),
                    "-" => ArithmeticNegate(operand),
                    _ => throw new NovaException(ErrorCode.NotSupported, "Unsupported unary operator: {0}", unary.Operator)
                };

            case IsNullExpression isNull:
                var val = EvaluateJoinExpression(isNull.Operand, combinedRow, columns, parameters);
                return isNull.IsNot ? val != null : val == null;

            default:
                throw new NovaException(ErrorCode.NotSupported, "Unsupported expression type in JOIN: {0}", expr.ExprType);
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
            throw new NovaException(ErrorCode.InvalidArgument, "Column '{0}.{1}' not found", colRef.TablePrefix, colRef.ColumnName);
        }

        // 无表前缀：按列名匹配（如有歧义取第一个）
        for (var i = 0; i < columns.Count; i++)
        {
            if (String.Equals(columns[i].Column, colRef.ColumnName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new NovaException(ErrorCode.InvalidArgument, "Column '{0}' not found", colRef.ColumnName);
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
            throw new NovaException(ErrorCode.TableNotFound, "Table '{0}' not found", tableName);
        return table;
    }

    /// <summary>在已持有写锁的上下文中获取表引用（不再加锁）</summary>
    private NovaTable GetTableInternal(String tableName)
    {
        if (!_tables.TryGetValue(tableName, out var table))
            throw new NovaException(ErrorCode.TableNotFound, "Table '{0}' not found", tableName);
        return table;
    }

    private TableSchema GetSchema(String tableName)
    {
        using var _ = _metaLock.AcquireRead();
        if (!_schemas.TryGetValue(tableName, out var schema))
            throw new NovaException(ErrorCode.TableNotFound, "Table '{0}' not found", tableName);
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

    /// <summary>取列长度声明。只有字符串、二进制、向量保留长度，其余类型的括号参数（显示宽度、精度标度）忽略</summary>
    /// <param name="colDef">列定义</param>
    /// <param name="dataType">已解析的数据类型</param>
    /// <returns>长度，0 表示未声明</returns>
    private static Int32 GetColumnLength(SqlColumnDef colDef, DataType dataType) =>
        colDef.Length > 0 && DataTypeNames.SupportsLength(dataType) ? colDef.Length : 0;

    private static void ConvertRowTypes(Object?[] row, TableSchema schema)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] == null) continue;

            var colDef = schema.Columns[i];
            row[i] = ConvertValue(row[i]!, colDef.DataType);

            ValidateColumnValue(row[i]!, colDef);
        }
    }

    /// <summary>校验列长度约束。字符串按字符数，二进制按字节数，向量按维度</summary>
    /// <param name="value">已转换的值</param>
    /// <param name="colDef">列定义</param>
    private static void ValidateColumnValue(Object value, ColumnDefinition colDef)
    {
        var max = colDef.Length;
        if (max <= 0) return;

        switch (colDef.DataType)
        {
            case DataType.String when value is String str && str.Length > max:
                throw new NovaException(ErrorCode.ConstraintViolation, "Value too long for column '{0}': {1} > {2}", colDef.Name, str.Length, max);
            case DataType.Binary when value is Byte[] buf && buf.Length > max:
                throw new NovaException(ErrorCode.ConstraintViolation, "Value too long for column '{0}': {1} > {2}", colDef.Name, buf.Length, max);
            case DataType.Vector when value is Single[] vec && vec.Length != max:
                throw new NovaException(ErrorCode.ConstraintViolation, "Vector dimensions mismatch: {0} vs {1}", vec.Length, max);
        }
    }

    private static Object ConvertValue(Object value, DataType targetType)
    {
        return targetType switch
        {
            DataType.Boolean => ToBooleanValue(value),
            DataType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            DataType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            DataType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            DataType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            DataType.String => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            DataType.DateTime => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
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

        // 布尔按 0/1 参与数值比较（MySQL 兼容：bool列 = 1、bool列 = '1'）
        if (left is Boolean || right is Boolean)
        {
            if (TryGetNumber(left, out var leftNumber) && TryGetNumber(right, out var rightNumber))
                return leftNumber.CompareTo(rightNumber);
        }

        // 尝试数值比较
        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        // 字符串比较
        return String.Compare(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>尝试把值转换为数值，布尔按 1/0 处理</summary>
    /// <param name="value">源值</param>
    /// <param name="number">转换结果</param>
    /// <returns>是否转换成功</returns>
    private static Boolean TryGetNumber(Object? value, out Double number)
    {
        number = 0;
        if (value == null) return false;

        if (value is Boolean b)
        {
            number = b ? 1d : 0d;
            return true;
        }

        if (IsNumeric(value))
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }

        return value is String s && Double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>将值转换为布尔，兼容 true/false 文本与 1/0 数字</summary>
    /// <param name="value">源值</param>
    /// <returns>布尔值</returns>
    private static Boolean ToBooleanValue(Object value)
    {
        if (value is Boolean b) return b;

        if (value is String s)
        {
            var text = s.Trim();
            if (text.EqualIgnoreCase("true") || text == "1") return true;
            if (text.EqualIgnoreCase("false") || text == "0" || text.Length == 0) return false;
        }

        if (IsNumeric(value)) return Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0;

        return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
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
            throw new NovaException(ErrorCode.InvalidArgument, "Vector dimensions mismatch: {0} vs {1}", a.Length, b.Length);

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
            throw new NovaException(ErrorCode.InvalidArgument, "Vector dimensions mismatch: {0} vs {1}", a.Length, b.Length);

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
            throw new NovaException(ErrorCode.InvalidArgument, "Vector dimensions mismatch: {0} vs {1}", a.Length, b.Length);

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
        var tableName = Convert.ToString(args[1], CultureInfo.InvariantCulture)!;
        var k = Convert.ToInt32(args[2], CultureInfo.InvariantCulture);
        var metric = args.Count > 3 && args[3] != null ? Convert.ToString(args[3], CultureInfo.InvariantCulture)!.ToLowerInvariant() : "cosine";

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
            throw new NovaException(ErrorCode.InvalidArgument, "Table '{0}' has no Vector column", tableName);

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
                _ => throw new NovaException(ErrorCode.InvalidArgument, "Unknown metric: {0}", metric)
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
            sb.Append(scored[i].Score.ToString("F6", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static Object? ArithmeticOp(Object? left, Object? right, Func<Double, Double, Double> op)
    {
        if (left == null || right == null) return null;
        return op(Convert.ToDouble(left, CultureInfo.InvariantCulture), Convert.ToDouble(right, CultureInfo.InvariantCulture));
    }

    private static Object? ArithmeticNegate(Object? value)
    {
        if (value == null) return null;
        return -Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static Object? EvaluateLike(Object? left, Object? right)
    {
        if (left == null || right == null) return false;

        var str = Convert.ToString(left, CultureInfo.InvariantCulture)!;
        var pattern = Convert.ToString(right, CultureInfo.InvariantCulture)!;

        return System.Text.RegularExpressions.Regex.IsMatch(str, LikeToRegex(pattern), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
            parts.Add(Convert.ToString(row[idx], CultureInfo.InvariantCulture) ?? "NULL");
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
                // 兼容别名限定列名（如 e.name），取点后部分作为列名
                var columnName = clause.ColumnName;
                var dot = columnName.LastIndexOf('.');
                if (dot >= 0) columnName = columnName.Substring(dot + 1);

                var idx = schema.GetColumnIndex(columnName);
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
                // 兼容别名限定列名（如 e.name），取点后部分作为列名
                var columnName = clause.ColumnName;
                var dot = columnName.LastIndexOf('.');
                if (dot >= 0) columnName = columnName.Substring(dot + 1);

                var idx = -1;
                for (var i = 0; i < columns.Count; i++)
                {
                    if (String.Equals(columns[i].Column, columnName, StringComparison.OrdinalIgnoreCase))
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
