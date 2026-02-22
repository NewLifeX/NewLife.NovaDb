# NewLife.NovaDb - SQL 引擎架构

## 1. 概述

SQL 引擎负责将 SQL 文本转换为可执行的操作,支持 DDL、DML、查询、JOIN、聚合等功能。采用**词法分析 → 语法分析 → AST → 执行**的经典编译流程。

## 2. 处理流程

```
SQL 文本 → SqlLexer(词法分析) → Token 流
        ↓
SqlParser(语法分析) → SqlAst(抽象语法树)
        ↓
SqlEngine(执行) → 调用底层引擎(Nova/Flux/KV)
        ↓
返回结果集 / 影响行数
```

## 3. 词法分析器 (`SqlLexer`)

### 3.1 Token 类型

| Token 类型 | 示例 |
|-----------|------|
| 关键字 | SELECT, INSERT, UPDATE, DELETE, CREATE, DROP, WHERE, JOIN |
| 标识符 | table_name, column_name, alias |
| 字面量 | 123, 3.14, 'hello', true, NULL |
| 运算符 | =, !=, <, >, <=, >=, +, -, *, /, AND, OR, NOT, LIKE |
| 参数占位符 | @param1, @userId |
| 分隔符 | (, ), ,, ; |
| 注释 | `-- this is a comment` |

### 3.2 词法规则

- 关键字**不区分大小写**（SELECT = select = SeLeCt）
- 标识符支持字母、数字、下划线,不以数字开头
- 字符串使用单引号：`'hello world'`
- 数值支持整数、浮点、科学计数法：`123`, `3.14`, `1.2e-3`
- 行注释：`-- comment`

## 4. 语法分析器 (`SqlParser`)

### 4.1 支持的 SQL 语句

#### DDL（数据定义语言）

```sql
CREATE TABLE users (id INT PRIMARY KEY, name STRING, age INT);
CREATE INDEX idx_age ON users(age);
CREATE DATABASE mydb;
DROP TABLE users;
DROP INDEX idx_age ON users;
DROP DATABASE mydb;
ALTER TABLE users ADD COLUMN email STRING;
ALTER TABLE users MODIFY COLUMN age BIGINT;
ALTER TABLE users DROP COLUMN email;
ALTER TABLE users COMMENT 'user table';
```

#### DML（数据操作语言）

```sql
INSERT INTO users (id, name, age) VALUES (1, 'Alice', 25);
UPDATE users SET age=26 WHERE id=1;
DELETE FROM users WHERE age<18;
```

#### 查询语句

```sql
-- 基础查询
SELECT * FROM users;
SELECT id, name, age FROM users WHERE age>18;
SELECT name AS username, age+1 AS next_age FROM users;

-- JOIN
SELECT u.name, o.total 
FROM users u 
INNER JOIN orders o ON u.id = o.user_id
WHERE o.total > 100;

-- 聚合与分组
SELECT age, COUNT(*) AS cnt, AVG(score) AS avg_score
FROM students
GROUP BY age
HAVING COUNT(*) > 10
ORDER BY age DESC
LIMIT 20 OFFSET 10;
```

### 4.2 递归下降解析

```csharp
public class SqlParser
{
    // 解析 SELECT 语句
    private SelectStatement ParseSelect()
    {
        Expect(TokenType.SELECT);
        
        // SELECT 列表
        var columns = ParseSelectColumns();
        
        // FROM 子句
        Expect(TokenType.FROM);
        var tableName = ExpectIdentifier();
        
        // WHERE 子句（可选）
        Expression? whereClause = null;
        if (Match(TokenType.WHERE))
        {
            whereClause = ParseExpression();
        }
        
        // JOIN 子句（可选）
        var joins = ParseJoins();
        
        // GROUP BY 子句（可选）
        var groupBy = ParseGroupBy();
        
        // HAVING 子句（可选）
        var having = ParseHaving();
        
        // ORDER BY 子句（可选）
        var orderBy = ParseOrderBy();
        
        // LIMIT/OFFSET 子句（可选）
        var (limit, offset) = ParseLimitOffset();
        
        return new SelectStatement
        {
            Columns = columns,
            TableName = tableName,
            WhereClause = whereClause,
            Joins = joins,
            GroupBy = groupBy,
            Having = having,
            OrderBy = orderBy,
            Limit = limit,
            Offset = offset
        };
    }
}
```

## 5. 抽象语法树 (`SqlAst`)

### 5.1 语句类型

```csharp
public abstract class Statement { }

public class SelectStatement : Statement
{
    public List<SelectColumn> Columns { get; set; }
    public String TableName { get; set; }
    public Expression? WhereClause { get; set; }
    public List<JoinClause> Joins { get; set; }
    public List<String> GroupBy { get; set; }
    public Expression? Having { get; set; }
    public List<OrderByClause> OrderBy { get; set; }
    public Int32? Limit { get; set; }
    public Int32? Offset { get; set; }
}

public class InsertStatement : Statement
{
    public String TableName { get; set; }
    public List<String> Columns { get; set; }
    public List<Expression> Values { get; set; }
}

public class UpdateStatement : Statement
{
    public String TableName { get; set; }
    public Dictionary<String, Expression> SetClauses { get; set; }
    public Expression? WhereClause { get; set; }
}

public class DeleteStatement : Statement
{
    public String TableName { get; set; }
    public Expression? WhereClause { get; set; }
}
```

### 5.2 表达式类型

```csharp
public abstract class Expression { }

public class LiteralExpression : Expression
{
    public Object? Value { get; set; } // 123, "hello", true, null
}

public class ColumnExpression : Expression
{
    public String ColumnName { get; set; }
    public String? TablePrefix { get; set; } // For JOIN: users.id
}

public class BinaryExpression : Expression
{
    public Expression Left { get; set; }
    public Operator Op { get; set; } // =, !=, <, >, AND, OR, +, -, *, /
    public Expression Right { get; set; }
}

public class FunctionCallExpression : Expression
{
    public String Name { get; set; }      // COUNT, SUM, UPPER, etc.
    public List<Expression> Arguments { get; set; }
}
```

## 6. 查询执行流程

### 6.1 单表查询

```
1. 获取所有行 (NovaTable.GetAll)
2. WHERE 过滤 → 保留满足条件的行
3. GROUP BY 分组 → 按指定列分组
4. HAVING 过滤 → 过滤分组
5. 聚合函数计算 (COUNT, SUM, AVG, etc.)
6. ORDER BY 排序 → 按指定列排序
7. OFFSET/LIMIT 分页 → 跳过前 N 行,取 M 行
8. 投影 (列选择/别名) → 返回最终列
```

### 6.2 JOIN 查询

**实现方式**：Nested Loop Join

```
1. 获取左表所有行 (FROM table1)
2. 对于每个 JOIN 子句:
   a. 获取右表所有行
   b. 对于左表的每一行:
      - 遍历右表的每一行
      - 评估 ON 条件
      - 如果匹配,合并为宽行
      - INNER JOIN: 只保留匹配行
      - LEFT JOIN: 无匹配时右侧补 NULL
      - RIGHT JOIN: 无匹配时左侧补 NULL
3. WHERE 过滤
4. ORDER BY 排序
5. OFFSET/LIMIT 分页
6. 投影
```

**示例**：

```sql
SELECT u.name, o.total 
FROM users u 
INNER JOIN orders o ON u.id = o.user_id
WHERE o.total > 100;

执行流程:
1. 读取 users 表所有行 → [{id:1, name:'Alice'}, {id:2, name:'Bob'}]
2. 对每个 user:
   - 读取 orders 表所有行 → [{order_id:1, user_id:1, total:200}, ...]
   - 匹配 u.id = o.user_id
   - 合并为宽行 → {u.id:1, u.name:'Alice', o.order_id:1, o.user_id:1, o.total:200}
3. WHERE 过滤 o.total > 100
4. 投影 u.name, o.total → {name:'Alice', total:200}
```

## 7. SQL 函数系统架构

详见原架构设计文档 §9.7,此处仅概述:

### 7.1 函数分类

| 类别 | 示例 | 优先级 |
|------|------|--------|
| 聚合函数 | COUNT, SUM, AVG, MIN, MAX, STRING_AGG | P0 |
| 字符串函数 | CONCAT, LENGTH, SUBSTRING, UPPER, LOWER, TRIM | P0 |
| 数值函数 | ABS, ROUND, CEILING, FLOOR, MOD, POWER, SQRT | P0 |
| 日期时间函数 | NOW, YEAR, MONTH, DAY, DATEDIFF, DATEADD | P0 |
| 类型转换函数 | CAST, CONVERT, COALESCE, ISNULL | P0 |
| 条件表达式 | CASE WHEN, IF, IIF | P0 |
| 系统函数 | DATABASE, VERSION, USER, ROW_COUNT | P1 |
| 加密哈希函数 | MD5, SHA1, SHA2 | P1 |
| 地理向量函数 | DISTANCE, COSINE_SIMILARITY, DOT_PRODUCT | P1 |

完整清单见《SQL函数支持清单.md》。

### 7.2 函数执行模型

- **标量函数**：输入单行,输出单值（如 `UPPER('hello')` → `'HELLO'`）
- **聚合函数**：输入一组行,输出单值（如 `SUM(price)` → 对分组内所有 price 求和）

### 7.3 用户自定义函数（UDF）

```csharp
// 扩展 API
sqlEngine.RegisterFunction("CRC32", args =>
{
    if (args.Count == 0 || args[0] == null) return null;
    var bytes = Encoding.UTF8.GetBytes(args[0].ToString()!);
    return CalculateCrc32(bytes);
});

// SQL 中使用
sqlEngine.Execute("SELECT CRC32(name) FROM users");
```

## 8. 参数化查询

```csharp
// 参数化 SQL
var sql = "SELECT * FROM users WHERE age > @minAge AND name LIKE @pattern";
var parameters = new Dictionary<String, Object?>
{
    ["@minAge"] = 18,
    ["@pattern"] = "A%"
};

var result = sqlEngine.Query(sql, parameters);
```

**防 SQL 注入**：参数值不参与 SQL 解析,直接作为数据使用。

## 9. 性能优化

### 9.1 查询计划缓存

```csharp
// 缓存已解析的 AST
private readonly Dictionary<String, Statement> _queryPlanCache = new();

public List<Dictionary<String, Object?>> Query(String sql, 
    Dictionary<String, Object?>? parameters = null)
{
    // 1. 查找缓存
    if (!_queryPlanCache.TryGetValue(sql, out var statement))
    {
        // 2. 解析 SQL
        statement = _parser.Parse(sql);
        _queryPlanCache[sql] = statement;
    }
    
    // 3. 执行查询
    return Execute(statement, parameters);
}
```

### 9.2 索引利用

```csharp
// WHERE id = 123 → 使用主键索引直接查找
if (whereClause is BinaryExpression { Op: Operator.Equals } binExpr
    && binExpr.Left is ColumnExpression { ColumnName: "id" })
{
    var primaryKey = Evaluate(binExpr.Right);
    return table.Get(primaryKey, tx);
}
```

### 9.3 谓词下推

```csharp
// 先过滤再 JOIN,减少数据量
var filteredLeft = leftRows.Where(row => EvaluateWhere(row, whereClause));
var joinedRows = PerformJoin(filteredLeft, rightRows, joinCondition);
```

## 10. 设计决策

### D1: SQL 子集策略
覆盖 ~60% 的 SQL92 标准,聚焦业务常用场景,不追求完整性。避免复杂特性（如子查询、窗口函数、CTE）增加实现复杂度。

### D2: Nested Loop Join
实现简单,适合小数据集。未来可扩展 Hash Join、Merge Join 优化大数据集 JOIN。

### D3: 递归下降解析
代码清晰,易于理解和维护。性能足够（解析开销远小于执行开销）。

### D4: 函数注册表
内置函数通过注册表管理,支持运行时扩展（UDF）。函数名大小写不敏感。

### D5: 参数化查询
防 SQL 注入,支持查询计划复用。参数值不参与解析,直接作为数据使用。

## 11. 未来扩展

### 11.1 子查询
- 支持 IN 子查询：`WHERE id IN (SELECT user_id FROM orders)`
- 支持标量子查询：`SELECT (SELECT MAX(age) FROM users) AS max_age`

### 11.2 窗口函数
- ROW_NUMBER, RANK, DENSE_RANK
- LEAD, LAG
- 分区与排序：`ROW_NUMBER() OVER (PARTITION BY dept_id ORDER BY salary DESC)`

### 11.3 CTE（公共表表达式）
```sql
WITH regional_sales AS (
    SELECT region, SUM(amount) AS total_sales
    FROM orders
    GROUP BY region
)
SELECT * FROM regional_sales WHERE total_sales > 1000;
```

### 11.4 执行计划优化器
- 成本估算（基于统计信息）
- JOIN 顺序优化
- 索引选择策略

### 11.5 Hash Join / Merge Join
- Hash Join: 适合等值连接,O(n+m) 复杂度
- Merge Join: 适合已排序数据,O(n+m) 复杂度
