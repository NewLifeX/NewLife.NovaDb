using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;
using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Sql;

/// <summary>SQL 执行引擎，连接 SQL 解析器与表引擎</summary>
public class SqlEngine : IDisposable
{
    private readonly String _dbPath;
    private readonly DbOptions _options;
    private readonly TransactionManager _txManager;
    private readonly Dictionary<String, NovaTable> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<String, TableSchema> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Object _lock = new();
    private Boolean _disposed;

    /// <summary>事务管理器</summary>
    public TransactionManager TxManager => _txManager;

    /// <summary>数据库路径</summary>
    public String DbPath => _dbPath;

    /// <summary>获取所有表名</summary>
    public IReadOnlyCollection<String> TableNames
    {
        get
        {
            lock (_lock)
            {
                return _schemas.Keys.ToList().AsReadOnly();
            }
        }
    }

    /// <summary>创建 SQL 执行引擎</summary>
    /// <param name="dbPath">数据库路径</param>
    /// <param name="options">数据库选项</param>
    public SqlEngine(String dbPath, DbOptions? options = null)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _options = options ?? new DbOptions { Path = dbPath };
        _txManager = new TransactionManager();

        if (!Directory.Exists(_dbPath))
            Directory.CreateDirectory(_dbPath);
    }

    /// <summary>执行 SQL 语句并返回结果</summary>
    /// <param name="sql">SQL 文本</param>
    /// <param name="parameters">参数字典</param>
    /// <returns>执行结果</returns>
    public SqlResult Execute(String sql, Dictionary<String, Object?>? parameters = null)
    {
        if (sql == null) throw new ArgumentNullException(nameof(sql));

        var parser = new SqlParser(sql);
        var stmt = parser.Parse();

        return stmt switch
        {
            CreateTableStatement create => ExecuteCreateTable(create),
            DropTableStatement drop => ExecuteDropTable(drop),
            CreateIndexStatement createIdx => ExecuteCreateIndex(createIdx),
            DropIndexStatement dropIdx => ExecuteDropIndex(dropIdx),
            InsertStatement insert => ExecuteInsert(insert, parameters),
            UpdateStatement update => ExecuteUpdate(update, parameters),
            DeleteStatement delete => ExecuteDelete(delete, parameters),
            SelectStatement select => ExecuteSelect(select, parameters),
            _ => throw new NovaDbException(ErrorCode.NotSupported, $"Unsupported statement type: {stmt.StatementType}")
        };
    }

    #region DDL 执行

    private SqlResult ExecuteCreateTable(CreateTableStatement stmt)
    {
        lock (_lock)
        {
            if (_schemas.ContainsKey(stmt.TableName))
            {
                if (stmt.IfNotExists) return new SqlResult { AffectedRows = 0 };
                throw new NovaDbException(ErrorCode.TableExists, $"Table '{stmt.TableName}' already exists");
            }

            var schema = new TableSchema(stmt.TableName);

            foreach (var colDef in stmt.Columns)
            {
                var dataType = ParseDataType(colDef.DataTypeName);
                schema.AddColumn(new ColumnDefinition(colDef.Name, dataType, !colDef.NotNull, colDef.IsPrimaryKey));
            }

            var tablePath = Path.Combine(_dbPath, stmt.TableName);
            var table = new NovaTable(schema, tablePath, _options, _txManager);

            _schemas[stmt.TableName] = schema;
            _tables[stmt.TableName] = table;

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteDropTable(DropTableStatement stmt)
    {
        lock (_lock)
        {
            if (!_schemas.ContainsKey(stmt.TableName))
            {
                if (stmt.IfExists) return new SqlResult { AffectedRows = 0 };
                throw new NovaDbException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");
            }

            if (_tables.TryGetValue(stmt.TableName, out var table))
            {
                table.Dispose();
                _tables.Remove(stmt.TableName);
            }

            _schemas.Remove(stmt.TableName);

            // 删除表目录
            var tablePath = Path.Combine(_dbPath, stmt.TableName);
            if (Directory.Exists(tablePath))
            {
                try
                {
                    Directory.Delete(tablePath, recursive: true);
                }
                catch
                {
                    // 忽略文件系统错误
                }
            }

            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteCreateIndex(CreateIndexStatement stmt)
    {
        lock (_lock)
        {
            if (!_schemas.ContainsKey(stmt.TableName))
                throw new NovaDbException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");

            // 当前实现仅记录索引元数据，实际索引由 NovaTable 的 SkipList 主键索引处理
            return new SqlResult { AffectedRows = 0 };
        }
    }

    private SqlResult ExecuteDropIndex(DropIndexStatement stmt)
    {
        lock (_lock)
        {
            if (!_schemas.ContainsKey(stmt.TableName))
                throw new NovaDbException(ErrorCode.TableNotFound, $"Table '{stmt.TableName}' not found");

            return new SqlResult { AffectedRows = 0 };
        }
    }

    #endregion

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
                    throw new NovaDbException(ErrorCode.InvalidArgument,
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

    #region SELECT 执行

    private SqlResult ExecuteSelect(SelectStatement stmt, Dictionary<String, Object?>? parameters)
    {
        // 无表的 SELECT（如 SELECT 1）
        if (stmt.TableName == null)
        {
            return ExecuteSelectNoTable(stmt, parameters);
        }

        var table = GetTable(stmt.TableName);
        var schema = GetSchema(stmt.TableName);

        using var tx = _txManager.BeginTransaction();

        // 1. 获取所有行
        var rows = table.GetAll(tx);

        // 2. WHERE 过滤
        if (stmt.Where != null)
        {
            rows = rows.Where(row => EvaluateCondition(stmt.Where, row, schema, parameters)).ToList();
        }

        // 3. GROUP BY
        if (stmt.GroupBy != null && stmt.GroupBy.Count > 0)
        {
            return ExecuteGroupBy(stmt, rows, schema, parameters);
        }

        // 4. 检查是否有聚合函数（无 GROUP BY）
        if (HasAggregateFunction(stmt))
        {
            return ExecuteAggregate(stmt, rows, schema, parameters);
        }

        // 5. ORDER BY
        if (stmt.OrderBy != null)
        {
            rows = ApplyOrderBy(rows, stmt.OrderBy, schema);
        }

        // 6. OFFSET / LIMIT
        if (stmt.OffsetValue.HasValue)
        {
            rows = rows.Skip(stmt.OffsetValue.Value).ToList();
        }
        if (stmt.Limit.HasValue)
        {
            rows = rows.Take(stmt.Limit.Value).ToList();
        }

        // 7. 投影
        return BuildSelectResult(stmt, rows, schema, parameters);
    }

    private SqlResult ExecuteSelectNoTable(SelectStatement stmt, Dictionary<String, Object?>? parameters)
    {
        var result = new SqlResult();
        var colNames = new List<String>();
        var values = new List<Object?>();

        for (var i = 0; i < stmt.Columns.Count; i++)
        {
            var col = stmt.Columns[i];
            var val = EvaluateExpression(col.Expression, null, null, parameters);
            colNames.Add(col.Alias ?? $"col{i}");
            values.Add(val);
        }

        result.ColumnNames = colNames.ToArray();
        result.Rows.Add(values.ToArray());
        return result;
    }

    private SqlResult ExecuteGroupBy(SelectStatement stmt, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        // 按 GROUP BY 列分组
        var groups = new Dictionary<String, List<Object?[]>>();
        foreach (var row in rows)
        {
            var key = BuildGroupKey(row, stmt.GroupBy!, schema);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
            }
            group.Add(row);
        }

        // HAVING 过滤
        var filteredGroups = groups.AsEnumerable();
        if (stmt.Having != null)
        {
            filteredGroups = filteredGroups.Where(g =>
            {
                var representative = g.Value[0];
                return EvaluateGroupCondition(stmt.Having, g.Value, representative, schema, parameters);
            });
        }

        // 构建结果
        var result = new SqlResult();
        var colNames = BuildColumnNames(stmt, schema);
        result.ColumnNames = colNames;

        foreach (var group in filteredGroups)
        {
            var representative = group.Value[0];
            var outputRow = new Object?[stmt.Columns.Count];

            for (var i = 0; i < stmt.Columns.Count; i++)
            {
                var col = stmt.Columns[i];
                outputRow[i] = EvaluateSelectExpression(col.Expression, group.Value, representative, schema, parameters);
            }

            result.Rows.Add(outputRow);
        }

        return result;
    }

    private SqlResult ExecuteAggregate(SelectStatement stmt, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        var result = new SqlResult();
        var colNames = BuildColumnNames(stmt, schema);
        result.ColumnNames = colNames;

        var outputRow = new Object?[stmt.Columns.Count];
        for (var i = 0; i < stmt.Columns.Count; i++)
        {
            var col = stmt.Columns[i];
            outputRow[i] = EvaluateSelectExpression(col.Expression, rows, rows.Count > 0 ? rows[0] : null, schema, parameters);
        }

        result.Rows.Add(outputRow);
        return result;
    }

    private SqlResult BuildSelectResult(SelectStatement stmt, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        var result = new SqlResult();

        if (stmt.IsSelectAll)
        {
            result.ColumnNames = schema.Columns.Select(c => c.Name).ToArray();
            result.Rows = rows;
        }
        else
        {
            result.ColumnNames = BuildColumnNames(stmt, schema);

            foreach (var row in rows)
            {
                var outputRow = new Object?[stmt.Columns.Count];
                for (var i = 0; i < stmt.Columns.Count; i++)
                {
                    var col = stmt.Columns[i];
                    outputRow[i] = EvaluateExpression(col.Expression, row, schema, parameters);
                }
                result.Rows.Add(outputRow);
            }
        }

        return result;
    }

    #endregion

    #region 表达式求值

    private Object? EvaluateExpression(SqlExpression expr, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                return lit.Value;

            case ColumnRefExpression colRef:
                if (row == null || schema == null)
                    throw new NovaDbException(ErrorCode.InvalidArgument, $"Column '{colRef.ColumnName}' cannot be evaluated without a row context");
                var idx = schema.GetColumnIndex(colRef.ColumnName);
                return row[idx];

            case ParameterExpression param:
                if (parameters == null || !parameters.TryGetValue(param.ParameterName, out var paramValue))
                    throw new NovaDbException(ErrorCode.InvalidArgument, $"Parameter '{param.ParameterName}' not found");
                return paramValue;

            case BinaryExpression binary:
                return EvaluateBinary(binary, row, schema, parameters);

            case UnaryExpression unary:
                return EvaluateUnary(unary, row, schema, parameters);

            case FunctionExpression func when func.IsAggregate:
                // 单行上下文中不应出现聚合函数
                throw new NovaDbException(ErrorCode.SyntaxError, $"Aggregate function {func.FunctionName} not allowed in this context");

            case IsNullExpression isNull:
                var operandVal = EvaluateExpression(isNull.Operand, row, schema, parameters);
                return isNull.IsNot ? operandVal != null : operandVal == null;

            default:
                throw new NovaDbException(ErrorCode.NotSupported, $"Unsupported expression type: {expr.ExprType}");
        }
    }

    private Object? EvaluateSelectExpression(SqlExpression expr, List<Object?[]> groupRows, Object?[]? representative, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        if (expr is FunctionExpression func && func.IsAggregate)
            return EvaluateAggregateFunction(func, groupRows, schema, parameters);

        return EvaluateExpression(expr, representative, schema, parameters);
    }

    private Object? EvaluateBinary(BinaryExpression binary, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        // 短路求值
        if (binary.Operator == BinaryOperator.And)
        {
            var leftVal = EvaluateExpression(binary.Left, row, schema, parameters);
            if (leftVal is Boolean lb && !lb) return false;
            var rightVal = EvaluateExpression(binary.Right, row, schema, parameters);
            return Convert.ToBoolean(leftVal) && Convert.ToBoolean(rightVal);
        }

        if (binary.Operator == BinaryOperator.Or)
        {
            var leftVal = EvaluateExpression(binary.Left, row, schema, parameters);
            if (leftVal is Boolean lb && lb) return true;
            var rightVal = EvaluateExpression(binary.Right, row, schema, parameters);
            return Convert.ToBoolean(leftVal) || Convert.ToBoolean(rightVal);
        }

        var left = EvaluateExpression(binary.Left, row, schema, parameters);
        var right = EvaluateExpression(binary.Right, row, schema, parameters);

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
            BinaryOperator.Like => EvaluateLike(left, right),
            _ => throw new NovaDbException(ErrorCode.NotSupported, $"Unsupported operator: {binary.Operator}")
        };
    }

    private Object? EvaluateUnary(UnaryExpression unary, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        var operand = EvaluateExpression(unary.Operand, row, schema, parameters);

        return unary.Operator switch
        {
            "NOT" => !(Convert.ToBoolean(operand)),
            "-" => ArithmeticNegate(operand),
            _ => throw new NovaDbException(ErrorCode.NotSupported, $"Unsupported unary operator: {unary.Operator}")
        };
    }

    private Object? EvaluateAggregateFunction(FunctionExpression func, List<Object?[]> rows, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        switch (func.FunctionName)
        {
            case "COUNT":
                if (func.Arguments.Count > 0 && func.Arguments[0] is ColumnRefExpression colRef && colRef.ColumnName == "*")
                    return rows.Count;
                return rows.Count(r =>
                {
                    var val = EvaluateExpression(func.Arguments[0], r, schema, parameters);
                    return val != null;
                });

            case "SUM":
                var sum = 0.0;
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) sum += Convert.ToDouble(val);
                }
                return sum;

            case "AVG":
                var total = 0.0;
                var count = 0;
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null)
                    {
                        total += Convert.ToDouble(val);
                        count++;
                    }
                }
                return count > 0 ? total / count : null;

            case "MIN":
                Object? minVal = null;
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null && (minVal == null || CompareValues(val, minVal) < 0))
                        minVal = val;
                }
                return minVal;

            case "MAX":
                Object? maxVal = null;
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null && (maxVal == null || CompareValues(val, maxVal) > 0))
                        maxVal = val;
                }
                return maxVal;

            default:
                throw new NovaDbException(ErrorCode.NotSupported, $"Unsupported aggregate function: {func.FunctionName}");
        }
    }

    private Boolean EvaluateCondition(SqlExpression expr, Object?[]? row, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        var result = EvaluateExpression(expr, row, schema, parameters);
        return result is Boolean b && b;
    }

    private Boolean EvaluateGroupCondition(SqlExpression expr, List<Object?[]> groupRows, Object?[]? representative, TableSchema schema, Dictionary<String, Object?>? parameters)
    {
        // 对 HAVING 中的聚合函数进行求值
        if (expr is BinaryExpression binary)
        {
            if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
            {
                var leftResult = EvaluateGroupCondition(binary.Left, groupRows, representative, schema, parameters);
                var rightResult = EvaluateGroupCondition(binary.Right, groupRows, representative, schema, parameters);
                return binary.Operator == BinaryOperator.And ? leftResult && rightResult : leftResult || rightResult;
            }

            var left = EvaluateSelectExpression(binary.Left, groupRows, representative, schema, parameters);
            var right = EvaluateSelectExpression(binary.Right, groupRows, representative, schema, parameters);

            return binary.Operator switch
            {
                BinaryOperator.Equal => CompareValues(left, right) == 0,
                BinaryOperator.NotEqual => CompareValues(left, right) != 0,
                BinaryOperator.LessThan => CompareValues(left, right) < 0,
                BinaryOperator.GreaterThan => CompareValues(left, right) > 0,
                BinaryOperator.LessOrEqual => CompareValues(left, right) <= 0,
                BinaryOperator.GreaterOrEqual => CompareValues(left, right) >= 0,
                _ => false
            };
        }

        return EvaluateCondition(expr, representative, schema, parameters);
    }

    #endregion

    #region 辅助

    private NovaTable GetTable(String tableName)
    {
        lock (_lock)
        {
            if (!_tables.TryGetValue(tableName, out var table))
                throw new NovaDbException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");
            return table;
        }
    }

    private TableSchema GetSchema(String tableName)
    {
        lock (_lock)
        {
            if (!_schemas.TryGetValue(tableName, out var schema))
                throw new NovaDbException(ErrorCode.TableNotFound, $"Table '{tableName}' not found");
            return schema;
        }
    }

    /// <summary>获取表的架构定义</summary>
    /// <param name="tableName">表名</param>
    /// <returns>架构定义，不存在时返回 null</returns>
    public TableSchema? GetTableSchema(String tableName)
    {
        lock (_lock)
        {
            _schemas.TryGetValue(tableName, out var schema);
            return schema;
        }
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
            "VARCHAR" or "TEXT" or "STRING" or "NVARCHAR" => DataType.String,
            "BLOB" or "BINARY" or "VARBINARY" or "BYTES" => DataType.ByteArray,
            "DATETIME" or "TIMESTAMP" or "DATE" => DataType.DateTime,
            _ => throw new NovaDbException(ErrorCode.SyntaxError, $"Unknown data type: {typeName}")
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
            DataType.ByteArray when value is Byte[] bytes => bytes,
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

    /// <summary>释放资源</summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            foreach (var table in _tables.Values)
            {
                table.Dispose();
            }
            _tables.Clear();
            _schemas.Clear();
        }

        _disposed = true;
    }

    #endregion

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
}

/// <summary>SQL 执行结果</summary>
public class SqlResult
{
    /// <summary>受影响行数（DDL/DML 语句）</summary>
    public Int32 AffectedRows { get; set; }

    /// <summary>列名（SELECT 语句）</summary>
    public String[]? ColumnNames { get; set; }

    /// <summary>结果行（SELECT 语句）</summary>
    public List<Object?[]> Rows { get; set; } = [];

    /// <summary>是否为查询结果</summary>
    public Boolean IsQuery => ColumnNames != null;

    /// <summary>获取标量值（第一行第一列）</summary>
    /// <returns>标量值</returns>
    public Object? GetScalar()
    {
        if (Rows.Count > 0 && Rows[0].Length > 0)
            return Rows[0][0];
        return null;
    }
}
