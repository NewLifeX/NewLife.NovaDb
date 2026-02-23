using NewLife.NovaDb.Core;
using NewLife.NovaDb.Engine;

namespace NewLife.NovaDb.Sql;

partial class SqlEngine
{
    #region 表达式求值

    private Object? EvaluateExpression(SqlExpression expr, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                return lit.Value;

            case ColumnRefExpression colRef:
                if (row == null || schema == null)
                    throw new NovaException(ErrorCode.InvalidArgument, $"Column '{colRef.ColumnName}' cannot be evaluated without a row context");
                var idx = schema.GetColumnIndex(colRef.ColumnName);
                return row[idx];

            case ParameterExpression param:
                if (parameters == null || !parameters.TryGetValue(param.ParameterName, out var paramValue))
                    throw new NovaException(ErrorCode.InvalidArgument, $"Parameter '{param.ParameterName}' not found");
                return paramValue;

            case BinaryExpression binary:
                return EvaluateBinary(binary, row, schema, parameters);

            case UnaryExpression unary:
                return EvaluateUnary(unary, row, schema, parameters);

            case FunctionExpression func when func.IsAggregate:
                // 单行上下文中不应出现聚合函数
                throw new NovaException(ErrorCode.SyntaxError, $"Aggregate function {func.FunctionName} not allowed in this context");

            case FunctionExpression func when !func.IsAggregate:
                return EvaluateScalarFunction(func, row, schema, parameters);

            case CaseExpression caseExpr:
                return EvaluateCaseExpression(caseExpr, row, schema, parameters);

            case CastExpression castExpr:
                return EvaluateCastExpression(castExpr, row, schema, parameters);

            case IsNullExpression isNull:
                var operandVal = EvaluateExpression(isNull.Operand, row, schema, parameters);
                return isNull.IsNot ? operandVal != null : operandVal == null;

            case InExpression inExpr:
                return EvaluateInExpression(inExpr, row, schema, parameters);

            default:
                throw new NovaException(ErrorCode.NotSupported, $"Unsupported expression type: {expr.ExprType}");
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
            BinaryOperator.Modulo => ArithmeticOp(left, right, (a, b) => b != 0 ? a % b : throw new DivideByZeroException()),
            BinaryOperator.Like => EvaluateLike(left, right),
            _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported operator: {binary.Operator}")
        };
    }

    private Object? EvaluateUnary(UnaryExpression unary, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        var operand = EvaluateExpression(unary.Operand, row, schema, parameters);

        return unary.Operator switch
        {
            "NOT" => !(Convert.ToBoolean(operand)),
            "-" => ArithmeticNegate(operand),
            _ => throw new NovaException(ErrorCode.NotSupported, $"Unsupported unary operator: {unary.Operator}")
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

            case "STRING_AGG":
                var separator = func.Arguments.Count > 1
                    ? Convert.ToString(EvaluateExpression(func.Arguments[1], rows.Count > 0 ? rows[0] : null, schema, parameters)) ?? ","
                    : ",";
                var parts = new List<String>();
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) parts.Add(Convert.ToString(val)!);
                }
                return String.Join(separator, parts);

            case "GROUP_CONCAT":
                var gcParts = new List<String>();
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) gcParts.Add(Convert.ToString(val)!);
                }
                return String.Join(",", gcParts);

            case "STDDEV":
                var stdValues = new List<Double>();
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) stdValues.Add(Convert.ToDouble(val));
                }
                if (stdValues.Count == 0) return null;
                var stdMean = stdValues.Average();
                var stdVariance = stdValues.Sum(v => (v - stdMean) * (v - stdMean)) / stdValues.Count;
                return Math.Sqrt(stdVariance);

            case "VARIANCE":
                var varValues = new List<Double>();
                foreach (var row in rows)
                {
                    var val = EvaluateExpression(func.Arguments[0], row, schema, parameters);
                    if (val != null) varValues.Add(Convert.ToDouble(val));
                }
                if (varValues.Count == 0) return null;
                var varMean = varValues.Average();
                return varValues.Sum(v => (v - varMean) * (v - varMean)) / varValues.Count;

            default:
                throw new NovaException(ErrorCode.NotSupported, $"Unsupported aggregate function: {func.FunctionName}");
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

    /// <summary>求值标量函数</summary>
    private Object? EvaluateScalarFunction(FunctionExpression func, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        var args = new List<Object?>();
        foreach (var arg in func.Arguments)
        {
            args.Add(EvaluateExpression(arg, row, schema, parameters));
        }

        switch (func.FunctionName)
        {
            // 字符串函数
            case "CONCAT":
                return String.Concat(args.Select(a => Convert.ToString(a) ?? String.Empty));

            case "LENGTH" or "LEN":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.Length : (Object?)null;

            case "SUBSTRING" or "SUBSTR":
                if (args.Count < 2 || args[0] == null) return null;
                var subStr = Convert.ToString(args[0])!;
                var startPos = Convert.ToInt32(args[1]) - 1; // SQL 1-based
                if (startPos < 0) startPos = 0;
                if (startPos >= subStr.Length) return String.Empty;
                if (args.Count >= 3 && args[2] != null)
                {
                    var len = Convert.ToInt32(args[2]);
                    if (startPos + len > subStr.Length) len = subStr.Length - startPos;
                    return subStr.Substring(startPos, len);
                }
                return subStr.Substring(startPos);

            case "UPPER":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.ToUpper() : null;

            case "LOWER":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.ToLower() : null;

            case "TRIM":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.Trim() : null;

            case "LTRIM":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.TrimStart() : null;

            case "RTRIM":
                return args.Count > 0 && args[0] != null ? Convert.ToString(args[0])!.TrimEnd() : null;

            case "REPLACE":
                if (args.Count < 3 || args[0] == null) return args.Count > 0 ? args[0] : null;
                return Convert.ToString(args[0])!.Replace(
                    Convert.ToString(args[1]) ?? String.Empty,
                    Convert.ToString(args[2]) ?? String.Empty);

            case "LEFT":
                if (args.Count < 2 || args[0] == null) return null;
                var leftStr = Convert.ToString(args[0])!;
                var leftLen = Convert.ToInt32(args[1]);
                return leftLen >= leftStr.Length ? leftStr : leftStr.Substring(0, leftLen);

            case "RIGHT":
                if (args.Count < 2 || args[0] == null) return null;
                var rightStr = Convert.ToString(args[0])!;
                var rightLen = Convert.ToInt32(args[1]);
                return rightLen >= rightStr.Length ? rightStr : rightStr.Substring(rightStr.Length - rightLen);

            case "CHARINDEX" or "INSTR":
                if (args.Count < 2 || args[0] == null || args[1] == null) return 0;
                var ciNeedle = Convert.ToString(args[0])!;
                var ciHaystack = Convert.ToString(args[1])!;
                var ciIdx = ciHaystack.IndexOf(ciNeedle, StringComparison.OrdinalIgnoreCase);
                return ciIdx >= 0 ? ciIdx + 1 : 0; // 1-based

            case "REVERSE":
                if (args.Count < 1 || args[0] == null) return null;
                var revChars = Convert.ToString(args[0])!.ToCharArray();
                Array.Reverse(revChars);
                return new String(revChars);

            case "LPAD":
                if (args.Count < 2 || args[0] == null) return null;
                var lpadStr = Convert.ToString(args[0])!;
                var lpadLen = Convert.ToInt32(args[1]);
                var lpadChar = args.Count >= 3 && args[2] != null ? Convert.ToString(args[2])![0] : ' ';
                return lpadStr.PadLeft(lpadLen, lpadChar);

            case "RPAD":
                if (args.Count < 2 || args[0] == null) return null;
                var rpadStr = Convert.ToString(args[0])!;
                var rpadLen = Convert.ToInt32(args[1]);
                var rpadChar = args.Count >= 3 && args[2] != null ? Convert.ToString(args[2])![0] : ' ';
                return rpadStr.PadRight(rpadLen, rpadChar);

            // 数值函数
            case "ABS":
                return args.Count > 0 && args[0] != null ? Math.Abs(Convert.ToDouble(args[0])) : (Object?)null;

            case "ROUND":
                if (args.Count < 1 || args[0] == null) return null;
                var roundDecimals = args.Count >= 2 && args[1] != null ? Convert.ToInt32(args[1]) : 0;
                return Math.Round(Convert.ToDouble(args[0]), roundDecimals, MidpointRounding.AwayFromZero);

            case "CEILING" or "CEIL":
                return args.Count > 0 && args[0] != null ? Math.Ceiling(Convert.ToDouble(args[0])) : (Object?)null;

            case "FLOOR":
                return args.Count > 0 && args[0] != null ? Math.Floor(Convert.ToDouble(args[0])) : (Object?)null;

            case "MOD":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                var modB = Convert.ToDouble(args[1]);
                if (modB == 0) throw new DivideByZeroException();
                return Convert.ToDouble(args[0]) % modB;

            case "POWER" or "POW":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return Math.Pow(Convert.ToDouble(args[0]), Convert.ToDouble(args[1]));

            case "SQRT":
                return args.Count > 0 && args[0] != null ? Math.Sqrt(Convert.ToDouble(args[0])) : (Object?)null;

            case "RAND" or "RANDOM":
                return new Random().NextDouble();

            case "SIGN":
                return args.Count > 0 && args[0] != null ? (Object)Math.Sign(Convert.ToDouble(args[0])) : null;

            case "TRUNCATE" or "TRUNC":
                if (args.Count < 1 || args[0] == null) return null;
                var truncDecimals = args.Count >= 2 && args[1] != null ? Convert.ToInt32(args[1]) : 0;
                var truncFactor = Math.Pow(10, truncDecimals);
                return Math.Truncate(Convert.ToDouble(args[0]) * truncFactor) / truncFactor;

            case "PI":
                return Math.PI;

            case "EXP":
                return args.Count > 0 && args[0] != null ? Math.Exp(Convert.ToDouble(args[0])) : (Object?)null;

            case "LOG" or "LN":
                return args.Count > 0 && args[0] != null ? Math.Log(Convert.ToDouble(args[0])) : (Object?)null;

            case "LOG10":
                return args.Count > 0 && args[0] != null ? Math.Log10(Convert.ToDouble(args[0])) : (Object?)null;

            // 日期时间函数
            case "NOW" or "GETDATE" or "CURRENT_TIMESTAMP":
                return DateTime.Now;

            case "YEAR":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Year : (Object?)null;

            case "MONTH":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Month : (Object?)null;

            case "DAY":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Day : (Object?)null;

            case "HOUR":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Hour : (Object?)null;

            case "MINUTE":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Minute : (Object?)null;

            case "SECOND":
                return args.Count > 0 && args[0] != null ? Convert.ToDateTime(args[0]).Second : (Object?)null;

            case "DATEDIFF":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return (Convert.ToDateTime(args[0]) - Convert.ToDateTime(args[1])).Days;

            case "DATEADD":
                if (args.Count < 3 || args[0] == null || args[1] == null || args[2] == null) return null;
                var daInterval = Convert.ToString(args[0])!.ToUpper();
                var daAmount = Convert.ToInt32(args[1]);
                var daDate = Convert.ToDateTime(args[2]);
                return daInterval switch
                {
                    "YEAR" => daDate.AddYears(daAmount),
                    "MONTH" => daDate.AddMonths(daAmount),
                    "DAY" => daDate.AddDays(daAmount),
                    "HOUR" => daDate.AddHours(daAmount),
                    "MINUTE" => daDate.AddMinutes(daAmount),
                    "SECOND" => daDate.AddSeconds(daAmount),
                    _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown DATEADD interval: {daInterval}")
                };

            case "DATEPART":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                var dpPart = Convert.ToString(args[0])!.ToUpper();
                var dpDate = Convert.ToDateTime(args[1]);
                return dpPart switch
                {
                    "YEAR" => dpDate.Year,
                    "MONTH" => dpDate.Month,
                    "DAY" => dpDate.Day,
                    "HOUR" => dpDate.Hour,
                    "MINUTE" => dpDate.Minute,
                    "SECOND" => dpDate.Second,
                    "WEEKDAY" or "DAYOFWEEK" => (Int32)dpDate.DayOfWeek + 1,
                    "QUARTER" => (dpDate.Month - 1) / 3 + 1,
                    "WEEK" => System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(
                        dpDate, System.Globalization.CalendarWeekRule.FirstDay, DayOfWeek.Sunday),
                    _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown DATEPART part: {dpPart}")
                };

            case "WEEKDAY" or "DAYOFWEEK":
                return args.Count > 0 && args[0] != null ? (Int32)Convert.ToDateTime(args[0]).DayOfWeek + 1 : (Object?)null;

            case "QUARTER":
                if (args.Count < 1 || args[0] == null) return null;
                return (Convert.ToDateTime(args[0]).Month - 1) / 3 + 1;

            case "WEEK":
                if (args.Count < 1 || args[0] == null) return null;
                var wkDate = Convert.ToDateTime(args[0]);
                return System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(
                    wkDate, System.Globalization.CalendarWeekRule.FirstDay, DayOfWeek.Sunday);

            case "ASCII":
                if (args.Count < 1 || args[0] == null) return null;
                var ascStr = Convert.ToString(args[0]);
                return ascStr != null && ascStr.Length > 0 ? (Int32)ascStr[0] : (Object?)null;

            case "CHAR":
                if (args.Count < 1 || args[0] == null) return null;
                var charCode = Convert.ToInt32(args[0]);
                return charCode is >= 0 and <= 0xFFFF ? ((Char)charCode).ToString() : (Object?)null;

            case "LAST_DAY":
                if (args.Count < 1 || args[0] == null) return null;
                var ldDate = Convert.ToDateTime(args[0]);
                return new DateTime(ldDate.Year, ldDate.Month, DateTime.DaysInMonth(ldDate.Year, ldDate.Month));

            case "DATE_FORMAT":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                var dfDate = Convert.ToDateTime(args[0]);
                var dfFormat = Convert.ToString(args[1])!
                    .Replace("%Y", "yyyy").Replace("%m", "MM").Replace("%d", "dd")
                    .Replace("%H", "HH").Replace("%i", "mm").Replace("%s", "ss")
                    .Replace("%M", "MMMM").Replace("%W", "dddd");
                return dfDate.ToString(dfFormat);

            case "TIMESTAMPDIFF":
                if (args.Count < 3 || args[0] == null || args[1] == null || args[2] == null) return null;
                var tdUnit = Convert.ToString(args[0])!.ToUpper();
                var tdDate1 = Convert.ToDateTime(args[1]);
                var tdDate2 = Convert.ToDateTime(args[2]);
                var tdDiff = tdDate2 - tdDate1;
                return tdUnit switch
                {
                    "YEAR" => tdDate2.Year - tdDate1.Year,
                    "MONTH" => (tdDate2.Year - tdDate1.Year) * 12 + (tdDate2.Month - tdDate1.Month),
                    "DAY" => (Int32)tdDiff.TotalDays,
                    "HOUR" => (Int64)tdDiff.TotalHours,
                    "MINUTE" => (Int64)tdDiff.TotalMinutes,
                    "SECOND" => (Int64)tdDiff.TotalSeconds,
                    _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown TIMESTAMPDIFF unit: {tdUnit}")
                };

            case "TIMESTAMPADD":
                if (args.Count < 3 || args[0] == null || args[1] == null || args[2] == null) return null;
                var taUnit = Convert.ToString(args[0])!.ToUpper();
                var taAmount = Convert.ToInt32(args[1]);
                var taDate = Convert.ToDateTime(args[2]);
                return taUnit switch
                {
                    "YEAR" => taDate.AddYears(taAmount),
                    "MONTH" => taDate.AddMonths(taAmount),
                    "DAY" => taDate.AddDays(taAmount),
                    "HOUR" => taDate.AddHours(taAmount),
                    "MINUTE" => taDate.AddMinutes(taAmount),
                    "SECOND" => taDate.AddSeconds(taAmount),
                    _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown TIMESTAMPADD unit: {taUnit}")
                };

            case "TIME_BUCKET":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                var tbBucket = Convert.ToString(args[0])!.Trim().ToLower();
                var tbDate2 = Convert.ToDateTime(args[1]);
                return ParseTimeBucket(tbBucket, tbDate2);

            // 类型转换函数
            case "CONVERT":
                if (args.Count < 2) return null;
                var convTypeName = Convert.ToString(args[0]) ?? String.Empty;
                return CastValue(args[1], convTypeName);

            case "COALESCE":
                foreach (var a in args)
                {
                    if (a != null) return a;
                }
                return null;

            case "ISNULL" or "IFNULL":
                if (args.Count < 2) return args.Count > 0 ? args[0] : null;
                return args[0] ?? args[1];

            case "NULLIF":
                if (args.Count < 2) return args.Count > 0 ? args[0] : null;
                return CompareValues(args[0], args[1]) == 0 ? null : args[0];

            // 条件函数
            case "IF" or "IIF":
                if (args.Count < 3) return null;
                var condResult = args[0] is Boolean bv ? bv : Convert.ToBoolean(args[0]);
                return condResult ? args[1] : args[2];

            // 系统函数
            case "DATABASE" or "CURRENT_DATABASE":
                return Path.GetFileName(_dbPath);

            case "VERSION":
                return "NovaDb 1.0";

            case "USER" or "CURRENT_USER":
                return "nova";

            case "CONNECTION_ID":
                return 0;

            case "ROW_COUNT":
                return _lastAffectedRows;

            case "LAST_INSERT_ID":
                return 0; // NovaDb 不支持自增主键，返回 0

            // 哈希函数
            case "MD5":
                if (args.Count < 1 || args[0] == null) return null;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(Convert.ToString(args[0])!);
                    var hash = md5.ComputeHash(bytes);
                    return BitConverter.ToString(hash).Replace("-", String.Empty).ToLower();
                }

            case "SHA1":
                if (args.Count < 1 || args[0] == null) return null;
                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(Convert.ToString(args[0])!);
                    var hash = sha1.ComputeHash(bytes);
                    return BitConverter.ToString(hash).Replace("-", String.Empty).ToLower();
                }

            case "SHA2":
                if (args.Count < 1 || args[0] == null) return null;
                var sha2Bits = args.Count >= 2 && args[1] != null ? Convert.ToInt32(args[1]) : 256;
                using (var sha2 = sha2Bits switch
                {
                    384 => (System.Security.Cryptography.HashAlgorithm)System.Security.Cryptography.SHA384.Create(),
                    512 => System.Security.Cryptography.SHA512.Create(),
                    _ => System.Security.Cryptography.SHA256.Create()
                })
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(Convert.ToString(args[0])!);
                    var hash = sha2.ComputeHash(bytes);
                    return BitConverter.ToString(hash).Replace("-", String.Empty).ToLower();
                }

            // GeoPoint 函数
            case "GEOPOINT":
                if (args.Count < 2) throw new NovaException(ErrorCode.InvalidArgument, "GEOPOINT requires 2 arguments (lat, lon)");
                return new GeoPoint(Convert.ToDouble(args[0]), Convert.ToDouble(args[1]));

            case "DISTANCE":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return ((GeoPoint)args[0]!).Distance((GeoPoint)args[1]!);

            case "DISTANCE_KM":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return ((GeoPoint)args[0]!).Distance((GeoPoint)args[1]!) / 1000.0;

            case "WITHIN_RADIUS":
                if (args.Count < 3 || args[0] == null || args[1] == null || args[2] == null) return null;
                return ((GeoPoint)args[0]!).WithinRadius((GeoPoint)args[1]!, Convert.ToDouble(args[2]));

            // Vector 函数
            case "VECTOR":
                var vec = new Single[args.Count];
                for (var i = 0; i < args.Count; i++)
                {
                    vec[i] = Convert.ToSingle(args[i]);
                }
                return vec;

            case "COSINE_SIMILARITY":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return CosineSimilarity((Single[])args[0]!, (Single[])args[1]!);

            case "EUCLIDEAN_DISTANCE":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return EuclideanDistance((Single[])args[0]!, (Single[])args[1]!);

            case "DOT_PRODUCT":
                if (args.Count < 2 || args[0] == null || args[1] == null) return null;
                return DotProduct((Single[])args[0]!, (Single[])args[1]!);

            default:
                throw new NovaException(ErrorCode.NotSupported, $"Unsupported function: {func.FunctionName}");
        }
    }

    /// <summary>求值 CASE WHEN 表达式</summary>
    private Object? EvaluateCaseExpression(CaseExpression caseExpr, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        foreach (var (whenExpr, thenExpr) in caseExpr.WhenClauses)
        {
            var whenResult = EvaluateExpression(whenExpr, row, schema, parameters);
            if (whenResult is Boolean b && b)
                return EvaluateExpression(thenExpr, row, schema, parameters);
        }

        return caseExpr.ElseExpression != null
            ? EvaluateExpression(caseExpr.ElseExpression, row, schema, parameters)
            : null;
    }

    /// <summary>求值 CAST 表达式</summary>
    private Object? EvaluateCastExpression(CastExpression castExpr, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        var value = EvaluateExpression(castExpr.Operand, row, schema, parameters);
        return CastValue(value, castExpr.TargetTypeName);
    }

    /// <summary>将值转换为指定类型</summary>
    private static Object? CastValue(Object? value, String typeName)
    {
        if (value == null) return null;

        return typeName.ToUpper() switch
        {
            "INT" or "INT32" or "INTEGER" => Convert.ToInt32(value),
            "BIGINT" or "INT64" or "LONG" => Convert.ToInt64(value),
            "FLOAT" or "DOUBLE" or "REAL" => Convert.ToDouble(value),
            "DECIMAL" or "NUMERIC" => Convert.ToDecimal(value),
            "VARCHAR" or "TEXT" or "STRING" or "NVARCHAR" or "CHAR" => Convert.ToString(value),
            "BOOL" or "BOOLEAN" => Convert.ToBoolean(value),
            "DATETIME" or "TIMESTAMP" or "DATE" => Convert.ToDateTime(value),
            _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown CAST target type: {typeName}")
        };
    }

    /// <summary>计算 IN / NOT IN 表达式</summary>
    private Object? EvaluateInExpression(InExpression inExpr, Object?[]? row, TableSchema? schema, Dictionary<String, Object?>? parameters)
    {
        var value = EvaluateExpression(inExpr.Operand, row, schema, parameters);
        if (value == null) return null;

        var found = false;

        if (inExpr.Subquery != null)
        {
            // 子查询模式
            var subResult = ExecuteSelect(inExpr.Subquery, parameters);
            foreach (var subRow in subResult.Rows)
            {
                if (subRow.Length > 0 && CompareValues(value, subRow[0]) == 0)
                {
                    found = true;
                    break;
                }
            }
        }
        else
        {
            // 值列表模式
            foreach (var valExpr in inExpr.Values)
            {
                var listVal = EvaluateExpression(valExpr, row, schema, parameters);
                if (listVal != null && CompareValues(value, listVal) == 0)
                {
                    found = true;
                    break;
                }
            }
        }

        return inExpr.IsNot ? !found : found;
    }

    /// <summary>解析时间分桶表达式，将时间对齐到指定桶边界</summary>
    /// <param name="bucket">桶大小表达式，如 "1 hour"、"5 minute"、"1 day"</param>
    /// <param name="dt">待对齐的时间</param>
    /// <returns>对齐后的时间</returns>
    private static DateTime ParseTimeBucket(String bucket, DateTime dt)
    {
        // 解析桶大小，格式如 "1 hour", "5 minute", "1 day"
        var parts = bucket.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new NovaException(ErrorCode.InvalidArgument, $"Invalid TIME_BUCKET format: {bucket}");

        var amount = Convert.ToInt32(parts[0]);
        if (amount <= 0) throw new NovaException(ErrorCode.InvalidArgument, $"TIME_BUCKET amount must be positive: {amount}");

        var unit = parts[1].TrimEnd('s').ToLower();
        return unit switch
        {
            "second" => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second / amount * amount),
            "minute" => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute / amount * amount, 0),
            "hour" => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour / amount * amount, 0, 0),
            "day" => new DateTime(dt.Year, dt.Month, Math.Min((dt.Day - 1) / amount * amount + 1, DateTime.DaysInMonth(dt.Year, dt.Month))),
            "month" => new DateTime(dt.Year, (dt.Month - 1) / amount * amount + 1, 1),
            "year" => new DateTime(dt.Year / amount * amount == 0 ? 1 : dt.Year / amount * amount, 1, 1),
            _ => throw new NovaException(ErrorCode.InvalidArgument, $"Unknown TIME_BUCKET unit: {unit}")
        };
    }

    #endregion
}
