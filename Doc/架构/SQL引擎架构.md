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

函数逐条清单与语义见《SQL函数支持清单.md》，此处仅概述:

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

单表查询由 `SqlEngine.Plan.cs` 推导访问计划（SELECT / UPDATE / DELETE / EXPLAIN 共用同一条推导，保证 EXPLAIN 与实际执行一致）：

| WHERE 形态 | 访问路径 | 实现 |
|---|---|---|
| `pk = v`、`pk IN (v...)` | `PK LOOKUP` | `NovaTable.Get` 逐键点查 |
| `pk > v`、`pk >= a AND pk < b` 等范围（数值/日期类型） | `PK RANGE` | `NovaTable.GetRange` 走 SkipList 范围定位，只反序列化范围内行 |
| `indexed_col = v`、`indexed_col IN (v...)`（单列索引） | `INDEX LOOKUP` | `NovaTable.LookupByIndex` 取主键后回表 |
| 联合索引 `(a,b)`：`a = v`、`a = v AND b <范围>`、`a = v AND b = v` | `INDEX LOOKUP` | 前缀走 `LookupByIndexPrefix`（按键有序分块扫描），全列等值走 `LookupByIndex` |
| `a = v OR b = v`（两侧都是可走索引的等值条件） | `INDEX MERGE` | 各路分别取主键后求并集去重，再统一回表 |
| `ORDER BY indexed_col`、`ORDER BY a, b`（命中最左前缀且同向） | `INDEX ORDER` | `NovaTable.ScanIndexOrder` 沿索引键序回表，免排序并支持 LIMIT 提前停止 |
| 其他（`LIKE`、函数、未建索引列、OR 有一侧无索引） | `FULL SCAN` | `GetAll` 顺序扫描 |

选择规则：
- 联合索引只按最左前缀下推：前缀列全部等值后才用下一列范围，字符串列不做下一列范围下推（排序口径与 SQL 不一致）。前缀命中行数按各前缀列 NDV 连乘估算，下一列范围用直方图选择率折算。
- 索引合并成本 = 各路成本之和 + 去重成本（`0.6/行`）；任一侧分支无法被单个索引完整覆盖时放弃合并，回退全表扫描。
- `SELECT COUNT(*)` 且条件被索引完整覆盖（单列/联合等值、`IN`、联合前缀）时，直接统计索引主键中当前事务可见的版本数，不反序列化行；`EXPLAIN` 的 `extra` 标 `Index-only COUNT`。需要残余 WHERE 复核或走合并路径时不做该优化。没有并发写事务时二级索引条目与可见行一一对应，直接数条目即可（`NovaTable.CountIndexKey`/`CountIndexPrefix`）；存在其他活跃事务时回退到逐主键检查可见性。
- 索引只负责缩小候选集，**最终结果始终再走一遍完整 WHERE 复核**，语义与全表扫描一致。
- 字符串索引键在写入时统一大写，与 SQL 大小写不敏感的 `=` 语义对齐（`.idx` 文件版本 2；旧版本文件启动时自动从行数据重建）。
- 命中行数超过全表 1/4 且超过 32 行时，回退全表扫描：随机回表比顺序扫描更慢。
- 字符串主键不做范围下推：SkipList 的字符串排序是区域性敏感比较，与 SQL 的大小写不敏感比较不一致，下推可能漏行。
- `ORDER BY` 可由索引顺序满足时免排序：排序列以主键打头走主键正/反向扫描；排序列是单列二级索引或联合索引最左前缀且同向时走 `INDEX ORDER`；等值命中后排序列为常量时任意方向都免排序。`EXPLAIN` 的 `extra` 标 `using index order` 并省略 `SORT` 步骤。

代价选择（s2 起）：先枚举候选（全扫 / 主键点查 / 主键范围 / 单列索引等值），再由 `Sql/SqlCostModel.cs` 估算比较。
- 成本单位＝一次行反序列化：顺序扫描 1、索引探测 1、回表 4、残余过滤 0.2、索引顺序扫描 0.5；有 `ORDER BY` 且计划不保序时另计 `0.1*n*log2(n)`，`ProvidesOrder` 的计划不计排序成本。
- 有统计时取成本最小者，并列优先全表扫描（`reason=stats`）；统计缺失时沿用原优先级：主键点查 → 索引 → 主键范围 → 索引顺序 → 全扫（`reason=fallback`）。
- 索引候选命中超过全表 1/4 且超过 32 行时直接排除（随机回表比顺序扫描更慢）。
- `EXPLAIN` 的 `extra` 追加 `cost=...`，`rows` 显示估算行数。

```csharp
// 执行阶段的形态
var plan = BuildTableAccessPlan(table, tableName, alias, schema, stmt.Where, parameters);
var rows = FetchPlanRows(plan, table, tx, maxRows);
if (stmt.Where != null)
    rows = rows.Where(row => EvaluateCondition(stmt.Where, row, schema, parameters)).ToList();
```

**已知限制**：二级索引是内存驻留并按写入即时维护的，未像主键索引那样按 MVCC 版本化。并发未提交的删除/更新期间，索引查询拿到的候选行可能与全表扫描略有差异（行本身仍按可见性 + WHERE 复核，不会返回不可见行，极端情况下可能少看到仍在可见窗口内的行）。主键访问路径不受影响。

### 9.3 LIMIT 下推

无 `GROUP BY` / 聚合、且 WHERE 已被访问路径完整覆盖时，把 `OFFSET + LIMIT` 压到取数阶段：全表扫描、主键范围与索引顺序扫描（`ProvidesOrder`）都只反序列化需要的行，`SELECT ... LIMIT 10` 不再反序列化整表，`ORDER BY col LIMIT n` 也能边扫边停。无 `ORDER BY` 时不保序的二级索引等值查找仍不做提前截断。

### 9.4 谓词下推

```csharp
// 先过滤再 JOIN,减少数据量
var filteredLeft = leftRows.Where(row => EvaluateWhere(row, whereClause));
var joinedRows = PerformJoin(filteredLeft, rightRows, joinCondition);
```

JOIN 的左右表仍为全表取数后嵌套循环连接；JOIN 侧索引下推（含别名解析与 ON 条件下推）尚未实现。

### 9.5 统计信息与 ANALYZE

`ANALYZE [TABLE] t`（不带表名时分析全库）由 `Engine/NovaTable.Stats.cs` 采集，经 `Sql/SqlEngine.Analyze.cs` 缓存并暴露给优化器：

- 表级：行数、统计版本号、分析时间、采样行数；列级：NDV、可空、16 桶等深直方图；索引级：索引名 → 键数量。
- 索引列直接遍历内存索引键（分块 ≤1024）得到精确 NDV 与直方图；非索引列水库采样 1000 行后按比例放大。
- 数据版本/行数变化超过 20%（下限 64 次写入）或表结构变化后统计自动失效，优化器回退 `RowCount` 粗估。
- `_sys.stats` 暴露统计明细，`information_schema.statistics.CARDINALITY` 使用真实 NDV，`EXPLAIN` 的 `rows` 列为统计估算值。

### 9.6 协作式查询取消

`SqlEngine.Execute` 增加 `CancellationToken` 重载（原签名等价 `CancellationToken.None`），令牌传到 `NovaTable.GetAll/GetRange`、WHERE 过滤、GROUP BY、聚合、JOIN 嵌套循环与投影。
大表扫描每 1024 行检查一次令牌，取消时抛 `OperationCanceledException`；已读行不影响数据，自动事务沿 `using` 回滚，引擎与连接保持可用。
`NovaCommand.CommandTimeout` 改为协作式：`CancellationTokenSource(CommandTimeout)` 把令牌传入引擎，超时抛 `NovaException(ErrorCode.Timeout)`，命令与连接可继续使用。

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
- 成本估算（基于统计信息）：当前只用“命中行数 / 表行数”做粗略选择性判断（见 9.2）
- JOIN 顺序优化
- JOIN 侧索引下推与 ON 条件下推

### 11.5 Hash Join / Merge Join
- Hash Join: 适合等值连接,O(n+m) 复杂度
- Merge Join: 适合已排序数据,O(n+m) 复杂度
