# SQL 函数支持清单

> 本文档列出 NewLife.NovaDb 的内置 SQL 函数，包含优先级、签名、返回类型、NULL/错误语义和实现状态。
> 
> - 落地细化自《需求规格说明书.md》§4.2
> - SQL 方言约定见《需求规格说明书.md》§5.1
> - 实现代码见 `SqlEngine.Expression.cs`

---

## 1. 全局约定

### 1.1 优先级
- **P0**：v1 必须实现
- **P1**：v1 应实现（同大版本内补齐）
- **P2**：远期规划

### 1.2 实现状态
- ✅ 已实现并通过测试
- ❌ 未实现

### 1.3 名称与大小写
- 函数名**大小写不敏感**（`now()` 与 `NOW()` 等价）
- 同一语义允许常见别名（表中标注）

### 1.4 NULL 语义
- **标量函数**：默认"任意参数 NULL → 结果 NULL"，除非明确例外（如 `COALESCE`）
- **聚合函数**：默认"忽略 NULL"，`COUNT(*)` 计入所有行

### 1.5 错误语义
- **参数非法/越界**：返回 NULL（不抛异常）
- **不可恢复错误**（内存不足、执行取消）：由执行器抛异常

### 1.6 聚合约束
- 聚合函数用于 `GROUP BY` 列表达式或全表聚合
- 支持 `HAVING` 过滤
- 聚合函数**不支持嵌套**（如 `MAX(COUNT(id))` 非法）

---

## 2. P0 函数（v1 必须）

### 2.1 聚合函数

| 函数 | 签名 | 返回类型 | 说明 | 状态 |
|------|------|---------|------|------|
| `COUNT` | `COUNT(*)` | INT | 所有行（含 NULL） | ✅ |
| `COUNT` | `COUNT(expr)` | INT | 非 NULL 行数 | ✅ |
| `COUNT` | `COUNT(DISTINCT expr)` | INT | 不同非 NULL 值数 | ✅ |
| `SUM` | `SUM(expr)` | DECIMAL/DOUBLE/LONG | 全 NULL → NULL | ✅ |
| `AVG` | `AVG(expr)` | DOUBLE | 全 NULL → NULL | ✅ |
| `MIN` | `MIN(expr)` | 与 expr 同类 | 全 NULL → NULL | ✅ |
| `MAX` | `MAX(expr)` | 与 expr 同类 | 全 NULL → NULL | ✅ |

### 2.2 字符串函数

| 函数 | 别名 | 签名 | 返回类型 | 说明 | 状态 |
|------|------|------|---------|------|------|
| `CONCAT` | — | `CONCAT(s1, s2, ...)` | STRING | 参数 ≥ 1 | ✅ |
| `LENGTH` | `LEN` | `LENGTH(s)` | INT | UTF-8 字节数 | ✅ |
| `SUBSTRING` | `SUBSTR` | `SUBSTRING(s, pos, len)` | STRING | pos 从 1 开始 | ✅ |
| `UPPER` | `UCASE` | `UPPER(s)` | STRING | 转大写 | ✅ |
| `LOWER` | `LCASE` | `LOWER(s)` | STRING | 转小写 | ✅ |
| `TRIM` | — | `TRIM(s)` | STRING | 去两端空白 | ✅ |
| `LTRIM` | — | `LTRIM(s)` | STRING | 去左侧空白 | ✅ |
| `RTRIM` | — | `RTRIM(s)` | STRING | 去右侧空白 | ✅ |
| `REPLACE` | — | `REPLACE(s, from, to)` | STRING | 替换所有匹配 | ✅ |
| `LEFT` | — | `LEFT(s, n)` | STRING | 左截取 n 字符 | ✅ |
| `RIGHT` | — | `RIGHT(s, n)` | STRING | 右截取 n 字符 | ✅ |

### 2.3 数值函数

| 函数 | 别名 | 签名 | 返回类型 | 说明 | 状态 |
|------|------|------|---------|------|------|
| `ABS` | — | `ABS(x)` | 与 x 同类 | 绝对值 | ✅ |
| `ROUND` | — | `ROUND(x, decimals)` | DECIMAL/DOUBLE | 四舍五入 | ✅ |
| `CEILING` | `CEIL` | `CEILING(x)` | 与 x 同类 | 向上取整 | ✅ |
| `FLOOR` | — | `FLOOR(x)` | 与 x 同类 | 向下取整 | ✅ |
| `MOD` | `%` | `MOD(a, b)` | 与参数同类 | b=0 → NULL | ✅ |

### 2.4 日期时间函数

| 函数 | 别名 | 签名 | 返回类型 | 说明 | 状态 |
|------|------|------|---------|------|------|
| `NOW` | `GETDATE`, `CURRENT_TIMESTAMP` | `NOW()` | DATETIME | 非确定性 | ✅ |
| `YEAR` | — | `YEAR(dt)` | INT | 提取年份 | ✅ |
| `MONTH` | — | `MONTH(dt)` | INT | 1~12 | ✅ |
| `DAY` | `DAYOFMONTH` | `DAY(dt)` | INT | 1~31 | ✅ |
| `HOUR` | — | `HOUR(dt)` | INT | 0~23 | ✅ |
| `MINUTE` | — | `MINUTE(dt)` | INT | 0~59 | ✅ |
| `SECOND` | — | `SECOND(dt)` | INT | 0~59 | ✅ |
| `DATEDIFF` | — | `DATEDIFF(dt1, dt2)` | LONG | 日期差（天），MySQL 风格 | ✅ |
| `DATEADD` | — | `DATEADD(part, n, dt)` | DATETIME | SQL Server 风格 | ✅ |
| `DATE_ADD` | — | `DATE_ADD(dt, INTERVAL n unit)` | DATETIME | MySQL 风格 | ✅ |
| `DATE_SUB` | — | `DATE_SUB(dt, INTERVAL n unit)` | DATETIME | MySQL 风格 | ✅ |

### 2.5 类型转换与 NULL 处理

| 函数 | 别名 | 签名 | 返回类型 | 说明 | 状态 |
|------|------|------|---------|------|------|
| `CAST` | — | `CAST(x AS type)` | type | SQL-92，转换失败 → NULL | ✅ |
| `CONVERT` | — | `CONVERT(type, x)` | type | SQL Server 风格 | ✅ |
| `COALESCE` | — | `COALESCE(x1, x2, ...)` | 按规则提升 | 返回第一个非 NULL | ✅ |
| `ISNULL` | `IFNULL` | `ISNULL(x, fallback)` | 按规则提升 | x 为 NULL 返回 fallback | ✅ |

### 2.6 条件表达式

| 函数/语法 | 签名 | 说明 | 状态 |
|----------|------|------|------|
| `CASE` | `CASE WHEN c THEN a ELSE b END` | SQL-92，多 WHEN 分支 | ✅ |
| `IF` | `IF(c, a, b)` | MySQL 风格三元条件 | ✅ |

---

## 3. P1 函数（v1 应具备）

### 3.1 聚合扩展

| 函数 | 别名 | 签名 | 说明 | 状态 |
|------|------|------|------|------|
| `STRING_AGG` | `GROUP_CONCAT` | `STRING_AGG(expr, sep)` | 字符串聚合拼接 | ✅ |

### 3.2 字符串扩展

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `CHARINDEX` | `CHARINDEX(substr, s [, start])` | 子串位置（0=不存在），SQL Server 风格 | ✅ |
| `INSTR` | `INSTR(s, substr)` | 子串位置，MySQL 风格 | ✅ |
| `CHAR` | `CHAR(num)` | ASCII/Unicode 码转字符 | ✅ |
| `ASCII` | `ASCII(s)` | 首字符 ASCII 码 | ✅ |

### 3.3 数值扩展

| 函数 | 别名 | 签名 | 说明 | 状态 |
|------|------|------|------|------|
| `POWER` | `POW` | `POWER(base, exp)` | 幂运算 | ✅ |
| `SQRT` | — | `SQRT(n)` | 平方根，n<0 → NULL | ✅ |
| `RAND` | `RANDOM` | `RAND()` | 随机数 [0.0, 1.0) | ✅ |
| `SIGN` | — | `SIGN(n)` | 符号：-1/0/1 | ✅ |
| `TRUNCATE` | `TRUNC` | `TRUNCATE(n, decimals)` | 截断（不四舍五入） | ✅ |

### 3.4 日期时间扩展

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `DATEPART` | `DATEPART(part, dt)` | SQL Server 风格，支持 QUARTER/WEEK | ✅ |
| `DATE_FORMAT` | `DATE_FORMAT(dt, format)` | MySQL 风格 `'%Y-%m-%d'` | ✅ |
| `TIME_BUCKET` | `TIME_BUCKET(bucket, dt)` | 时序聚合，bucket 如 `'1 hour'` | ✅ |
| `WEEKDAY` | `WEEKDAY(dt)` | 0=周日~6=周六 | ✅ |
| `DAYOFWEEK` | `DAYOFWEEK(dt)` | 1=周日~7=周六 | ✅ |
| `QUARTER` | `QUARTER(dt)` | 1~4 | ✅ |
| `WEEK` | `WEEK(dt)` | 0~53 | ✅ |

### 3.5 NULL 扩展

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `NULLIF` | `NULLIF(v1, v2)` | v1=v2 → NULL，否则 v1 | ✅ |
| `IIF` | `IIF(c, a, b)` | SQL Server 风格（同 IF） | ✅ |

### 3.6 系统/元数据

| 函数 | 别名 | 签名 | 说明 | 状态 |
|------|------|------|------|------|
| `DATABASE` | `CURRENT_DATABASE` | `DATABASE()` | 当前数据库名 | ✅ |
| `VERSION` | — | `VERSION()` | 版本字符串 | ✅ |
| `ROW_COUNT` | `@@ROWCOUNT` | `ROW_COUNT()` | 上一条 DML 影响行数 | ✅ |
| `LAST_INSERT_ID` | `@@IDENTITY` | `LAST_INSERT_ID()` | 最后自增值 | ✅ |

### 3.7 哈希

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `MD5` | `MD5(s)` | 32 字符十六进制 | ✅ |
| `SHA1` | `SHA1(s)` | 40 字符十六进制 | ✅ |
| `SHA2` | `SHA2(s, bits)` | bits：256/384/512 | ✅ |

### 3.8 GeoPoint 地理（NovaDb 扩展）

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `GEOPOINT` | `GEOPOINT(lat, lon)` | 构造地理坐标点 | ✅ |
| `DISTANCE` | `DISTANCE(p1, p2)` | 两点距离（米），Haversine | ✅ |
| `DISTANCE_KM` | `DISTANCE_KM(p1, p2)` | 两点距离（公里） | ✅ |
| `WITHIN_RADIUS` | `WITHIN_RADIUS(p, center, radius)` | 是否在半径内（米） | ✅ |
| `WITHIN_POLYGON` | `WITHIN_POLYGON(p, polygon_wkt)` | 是否在多边形内，射线法判断 | ✅ |

### 3.9 Vector 向量（NovaDb 扩展）

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `VECTOR` | `VECTOR(f1, f2, ...)` | 构造向量 | ✅ |
| `COSINE_SIMILARITY` | `COSINE_SIMILARITY(v1, v2)` | 余弦相似度 [-1, 1] | ✅ |
| `EUCLIDEAN_DISTANCE` | `EUCLIDEAN_DISTANCE(v1, v2)` | L2 距离 | ✅ |
| `DOT_PRODUCT` | `DOT_PRODUCT(v1, v2)` | 内积 | ✅ |
| `VECTOR_NEAREST` | `VECTOR_NEAREST(query, table, k, metric)` | Top-K KNN 查询，支持 cosine/euclidean/dot_product | ✅ |

---

## 4. P2 函数（远期规划）

### 4.1 聚合

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `STDDEV` | `STDDEV(expr)` | 样本标准差 | ✅ |
| `VARIANCE` | `VARIANCE(expr)` | 样本方差 | ✅ |

### 4.2 字符串

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `REVERSE` | `REVERSE(s)` | 字符串反转 | ✅ |
| `LPAD` | `LPAD(s, len, pad)` | 左填充 | ✅ |
| `RPAD` | `RPAD(s, len, pad)` | 右填充 | ✅ |

### 4.3 数值

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `PI` | `PI()` | 圆周率常量 | ✅ |
| `EXP` | `EXP(n)` | e 的 n 次方 | ✅ |
| `LOG` | `LOG(n)` / `LN(n)` | 自然对数 | ✅ |
| `LOG10` | `LOG10(n)` | 以 10 为底对数 | ✅ |

### 4.4 日期时间

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `LAST_DAY` | `LAST_DAY(dt)` | 当月最后一天 | ✅ |
| `TIMESTAMPDIFF` | `TIMESTAMPDIFF(unit, dt1, dt2)` | 时间差（指定单位） | ✅ |
| `TIMESTAMPADD` | `TIMESTAMPADD(unit, n, dt)` | 时间加减（指定单位） | ✅ |

### 4.5 系统

| 函数 | 签名 | 说明 | 状态 |
|------|------|------|------|
| `USER` | `USER()` / `CURRENT_USER()` | 当前用户 | ✅ |
| `CONNECTION_ID` | `CONNECTION_ID()` | 当前连接 ID | ✅ |

---

## 5. 实现统计

| 优先级 | 总数 | 已实现 | 未实现 | 完成率 |
|--------|------|--------|--------|--------|
| P0 | 38 | 38 | 0 | 100% |
| P1 | 27 | 27 | 0 | 100% |
| P2 | 14 | 14 | 0 | 100% |
| **合计** | **79** | **79** | **0** | **100%** |

### 待实现函数清单

所有函数均已实现。

---

## 6. 明确不支持（v1）

| 类别 | 函数 | 原因 |
|------|------|------|
| 窗口函数 | `ROW_NUMBER()`、`RANK()`、`LAG()`、`LEAD()` 等 | 规划 v1.1+ |
| 正则 | `REGEXP`、`REGEXP_REPLACE` 等 | 性能开销大 |
| 全文检索 | — | v1 不承诺 |
| JSON | `JSON_EXTRACT`、`JSON_SET` 等 | 由应用层处理 |
| 三角函数 | `SIN`/`COS`/`TAN` 等 | 业务场景少 |
| 加密 | `AES_ENCRYPT`/`AES_DECRYPT` | 由应用层实现 |

---

## 7. 兼容性说明

### 7.1 字符串长度
`LENGTH/LEN` 返回 UTF-8 **字节数**。后续可引入 `CHAR_LENGTH` 返回字符数。

### 7.2 类型转换失败
`CAST`/`CONVERT` 转换失败返回 NULL。

### 7.3 多数据库迁移
- 优先兼容 SQL-92 标准
- MySQL/PostgreSQL/SQL Server 重要扩展已标注来源
- ORM（XCode/EF Core/Dapper）常用函数投影已覆盖

---

（完）
