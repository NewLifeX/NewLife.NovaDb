# SQL 函数支持清单（用于实现与测试）

> 本文档列出 NewLife.NovaDb 的内置 SQL 函数（标量/聚合/系统/扩展），包含优先级、签名、返回类型、NULL/错误语义与兼容性说明。
>
> - 本文档是《需求规格说明书.md》§4.2/§5.12 的落地细化。
> - SQL 方言（标识符/注释/参数等）请以《需求规格说明书.md》§5.1 为准。
> - 明确不支持的特性见《需求规格说明书.md》§5.11。

更新日期：2026年2月23日

---

## 1. 全局约定

### 1.1 优先级

- **P0**：v1 必须实现（进入 v1 验收）
- **P1**：v1 应实现（同大版本内补齐）
- **P2**：远期规划（仅占位，不进入 v1 验收）

### 1.2 名称、大小写与别名

- 函数名 **大小写不敏感**（例如 `now()` 与 `NOW()` 等价）。
- 同一语义允许提供少量常见别名（例如 `LEN`/`LENGTH`），别名在表格中标注。

### 1.3 NULL 语义

- **标量函数**：默认"任意参数为 NULL 则结果为 NULL"，除非函数明确声明例外（例如 `COALESCE`）。
- **聚合函数**：默认"忽略 NULL（不计入）"，但 `COUNT(*)` 计入所有行。

### 1.4 错误语义（与 §5.12 对齐）

为减少业务侧异常分支，函数对"参数非法/越界"的默认策略为：

- **可判定的参数错误**：**返回 NULL**（例如 `SUBSTRING(x, 0, 10)`），并允许在诊断日志中记录；
- **不可恢复/执行期错误**（例如内存不足、执行取消）：由执行器抛出可区分的异常类型（不属于函数语义）。

若某个函数必须抛错（例如系统函数访问受限），需要在函数行的"错误语义"中显式声明。

### 1.5 时间类型与时区

- `DATETIME` 与 C# `DateTime` 一致，**不做时区换算**（详见规格书 §4.1）。

### 1.6 聚合函数使用约束

- 聚合函数在 `GROUP BY` 中作为列表达式使用，或在无 `GROUP BY` 时对全表聚合。
- 支持 `HAVING` 子句对聚合结果过滤。
- 聚合函数**不支持嵌套**（如 `MAX(COUNT(id))` 非法）。

---

## 2. P0 必备函数（v1 验收集）

### 2.1 聚合函数

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `COUNT` | - | `COUNT(*)` | INT | 计入所有行 | - | 返回结果集总行数 |
| `COUNT` | - | `COUNT(expr)` | INT | 忽略 NULL | - | 返回非 NULL 值的行数 |
| `COUNT` | - | `COUNT(DISTINCT expr)` | INT | 忽略 NULL | - | 返回不同非 NULL 值的个数 |
| `SUM` | - | `SUM(expr)` | DECIMAL/DOUBLE/LONG | 忽略 NULL；全 NULL 返回 NULL | 参数非法返回 NULL | 类型提升规则需稳定 |
| `AVG` | - | `AVG(expr)` | DOUBLE | 忽略 NULL；全 NULL 返回 NULL | 参数非法返回 NULL | 整数结果转 DOUBLE |
| `MIN` | - | `MIN(expr)` | 与 expr 同类 | 忽略 NULL；全 NULL 返回 NULL | - | - |
| `MAX` | - | `MAX(expr)` | 与 expr 同类 | 忽略 NULL；全 NULL 返回 NULL | - | - |

### 2.2 字符串函数

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `CONCAT` | - | `CONCAT(s1, s2, ...)` | STRING | 任意 NULL → NULL | - | 参数个数 ≥ 1；SQL-92 标准 |
| `LENGTH` | `LEN` | `LENGTH(s)` | INT | NULL → NULL | - | 返回 UTF-8 **字节数**（与存储一致）；字符数见 §6.2 |
| `SUBSTRING` | `SUBSTR` | `SUBSTRING(s, pos, len)` | STRING | NULL → NULL | pos/len 非法→NULL | `pos` 从 1 开始，超出返回空串 |
| `UPPER` | `UCASE` | `UPPER(s)` | STRING | NULL → NULL | - | 转大写 |
| `LOWER` | `LCASE` | `LOWER(s)` | STRING | NULL → NULL | - | 转小写 |
| `TRIM` | - | `TRIM(s)` | STRING | NULL → NULL | - | 去两端空白 |
| `LTRIM` | - | `LTRIM(s)` | STRING | NULL → NULL | - | 去左侧空白 |
| `RTRIM` | - | `RTRIM(s)` | STRING | NULL → NULL | - | 去右侧空白 |
| `REPLACE` | - | `REPLACE(s, from, to)` | STRING | NULL → NULL | - | 替换所有匹配 |
| `LEFT` | - | `LEFT(s, n)` | STRING | NULL → NULL | n 非法→NULL | 左截取 n 个字符 |
| `RIGHT` | - | `RIGHT(s, n)` | STRING | NULL → NULL | n 非法→NULL | 右截取 n 个字符 |

### 2.3 数值函数

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `ABS` | - | `ABS(x)` | 与 x 同类 | NULL → NULL | - | 绝对值 |
| `ROUND` | - | `ROUND(x, decimals)` | DECIMAL/DOUBLE | NULL → NULL | decimals 非法→NULL | 四舍五入；decimals=0 时取整 |
| `CEILING` | `CEIL` | `CEILING(x)` | 与 x 同类 | NULL → NULL | - | 向上取整 |
| `FLOOR` | - | `FLOOR(x)` | 与 x 同类 | NULL → NULL | - | 向下取整 |
| `MOD` | - | `MOD(a, b)` | 与参数同类 | NULL → NULL | b=0→NULL | 同时支持 `%` 运算符，语义一致 |

### 2.4 日期时间函数

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `NOW` | `GETDATE`, `CURRENT_TIMESTAMP` | `NOW()` | DATETIME | - | - | 非确定性；三种别名均支持 |
| `YEAR` | - | `YEAR(dt)` | INT | NULL → NULL | 参数非法→NULL | 提取年份 |
| `MONTH` | - | `MONTH(dt)` | INT | NULL → NULL | 参数非法→NULL | 提取月份 (1~12) |
| `DAY` | `DAYOFMONTH` | `DAY(dt)` | INT | NULL → NULL | 参数非法→NULL | 提取日期 (1~31) |
| `HOUR` | - | `HOUR(dt)` | INT | NULL → NULL | 参数非法→NULL | 提取小时 (0~23) |
| `MINUTE` | - | `MINUTE(dt)` | INT | NULL → NULL | 参数非法→NULL | 提取分钟 (0~59) |
| `SECOND` | - | `SECOND(dt)` | INT | NULL → NULL | 参数非法→NULL | 提取秒数 (0~59) |
| `DATEDIFF` | - | `DATEDIFF(dt1, dt2)` | LONG | NULL → NULL | 参数非法→NULL | 日期差（天数），dt1 - dt2；MySQL 风格 |
| `DATEADD` | - | `DATEADD(part, n, dt)` | DATETIME | NULL → NULL | part 非法→NULL | part：YEAR/MONTH/DAY/HOUR/MINUTE/SECOND；SQL Server 风格 |
| `DATE_ADD` | - | `DATE_ADD(dt, INTERVAL n unit)` | DATETIME | NULL → NULL | unit 非法→NULL | MySQL 风格（与 DATEADD 等价） |
| `DATE_SUB` | - | `DATE_SUB(dt, INTERVAL n unit)` | DATETIME | NULL → NULL | unit 非法→NULL | MySQL 风格 |

### 2.5 类型转换与 NULL 处理

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `CAST` | - | `CAST(x AS type)` | type | NULL → NULL | 转换失败→NULL | SQL-92 标准；支持所有 NovaDb 类型 |
| `CONVERT` | - | `CONVERT(type, x)` | type | NULL → NULL | 转换失败→NULL | SQL Server 风格别名 |
| `COALESCE` | - | `COALESCE(x1, x2, ...)` | 按规则提升 | 返回第一个非 NULL | - | 参数个数 ≥ 1；全 NULL 返回 NULL；SQL-92 标准 |
| `ISNULL` | `IFNULL` | `ISNULL(x, fallback)` | 按规则提升 | x 为 NULL 返回 fallback | - | 两种别名语义一致 |

### 2.6 条件表达式

| 函数/语法 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `CASE` | - | `CASE WHEN c THEN a ELSE b END` | 分支统一 | 条件 NULL 视为 FALSE | - | SQL-92 标准；支持多个 WHEN 分支 |
| `IF` | - | `IF(c, a, b)` | 分支统一 | 条件 NULL 视为 FALSE | - | MySQL 风格三元条件 |

---

## 3. P1 扩展函数（v1 应具备）

### 3.1 聚合扩展

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `STRING_AGG` | `GROUP_CONCAT` | `STRING_AGG(expr, sep)` | STRING | 忽略 NULL | - | 字符串聚合拼接 |

### 3.2 字符串扩展

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `CHARINDEX` | - | `CHARINDEX(substr, s [, start])` | INT | NULL → NULL | - | 查找子串位置（不存在返回 0），start 默认 1；SQL Server 风格 |
| `INSTR` | - | `INSTR(s, substr)` | INT | NULL → NULL | - | 查找子串位置；MySQL 风格（与 CHARINDEX 等价，参数顺序相反） |
| `CHAR` | - | `CHAR(num)` | STRING | NULL → NULL | - | ASCII/Unicode 码转字符 |
| `ASCII` | - | `ASCII(s)` | INT | NULL → NULL | - | 返回首字符的 ASCII 码 |

### 3.3 数值扩展

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `POWER` | `POW` | `POWER(base, exp)` | DOUBLE | NULL → NULL | - | 幂运算 |
| `SQRT` | - | `SQRT(n)` | DOUBLE | NULL → NULL | n<0→NULL | 平方根 |
| `RAND` | `RANDOM` | `RAND()` | DOUBLE | - | - | 随机数 [0.0, 1.0)；非确定性 |
| `SIGN` | - | `SIGN(n)` | INT | NULL → NULL | - | 符号：负数→-1，零→0，正数→1 |
| `TRUNCATE` | - | `TRUNCATE(n, decimals)` | DECIMAL/DOUBLE | NULL → NULL | decimals 非法→NULL | 截断（不四舍五入）；MySQL 风格 |

### 3.4 日期时间扩展

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `DATEPART` | - | `DATEPART(part, dt)` | INT | NULL → NULL | part 非法→NULL | part：YEAR/MONTH/DAY/HOUR/MINUTE/SECOND/QUARTER/WEEK 等；SQL Server 风格 |
| `DATE_FORMAT` | - | `DATE_FORMAT(dt, format)` | STRING | NULL → NULL | format 非法→NULL | MySQL 风格格式化（如 `'%Y-%m-%d'`） |
| `TIME_BUCKET` | - | `TIME_BUCKET(bucket, dt)` | DATETIME | NULL → NULL | bucket 非法→NULL | 时序聚合/物化视图常用；bucket 如 `'1 hour'`；参考 TimescaleDB |
| `WEEKDAY` | - | `WEEKDAY(dt)` | INT | NULL → NULL | 参数非法→NULL | 星期几，0=周日，6=周六；MySQL 风格 |
| `DAYOFWEEK` | - | `DAYOFWEEK(dt)` | INT | NULL → NULL | 参数非法→NULL | 星期几，1=周日，7=周六；SQL Server 风格 |
| `QUARTER` | - | `QUARTER(dt)` | INT | NULL → NULL | 参数非法→NULL | 提取季度 (1~4) |
| `WEEK` | - | `WEEK(dt)` | INT | NULL → NULL | 参数非法→NULL | 提取周数 (0~53) |

### 3.5 类型转换扩展

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `NULLIF` | - | `NULLIF(v1, v2)` | 与 v1 同类 | v1=v2 返回 NULL，否则返回 v1 | - | SQL-92 标准 |

### 3.6 条件扩展

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `IIF` | - | `IIF(c, a, b)` | 分支统一 | 条件 NULL 视为 FALSE | - | SQL Server 风格（与 IF 等价） |

### 3.7 系统/元数据

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `DATABASE` | `CURRENT_DATABASE` | `DATABASE()` | STRING | - | - | 当前数据库名 |
| `VERSION` | - | `VERSION()` | STRING | - | - | 版本字符串（如 `NovaDb 1.0.2026.0201`） |
| `ROW_COUNT` | `@@ROWCOUNT` | `ROW_COUNT()` | INT | - | - | 上一条 DML 影响行数 |
| `LAST_INSERT_ID` | `@@IDENTITY` | `LAST_INSERT_ID()` | LONG | - | - | 最后一次自增主键值；仅 INSERT 后可用 |

### 3.8 哈希

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `MD5` | - | `MD5(s)` | STRING | NULL → NULL | - | 32 字符十六进制；仅校验用途 |
| `SHA1` | - | `SHA1(s)` | STRING | NULL → NULL | - | 40 字符十六进制；仅校验用途 |
| `SHA2` | - | `SHA2(s, bits)` | STRING | NULL → NULL | bits 非法→NULL | bits：256/384/512 |

### 3.9 GeoPoint 地理位置（NovaDb 扩展）

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `DISTANCE` | - | `DISTANCE(p1, p2)` | DOUBLE | NULL → NULL | - | 两点间地理距离（米），Haversine 大圆距离 |
| `DISTANCE_KM` | - | `DISTANCE_KM(p1, p2)` | DOUBLE | NULL → NULL | - | 两点间距离（公里） |
| `WITHIN_RADIUS` | - | `WITHIN_RADIUS(p, center, radius)` | BOOL | NULL → NULL | - | 判断 p 是否在 center 半径 radius（米）范围内 |
| `WITHIN_POLYGON` | - | `WITHIN_POLYGON(p, polygon_wkt)` | BOOL | NULL → NULL | WKT 非法→NULL | 判断点是否在多边形区域内（WKT 格式） |

### 3.10 Vector 向量（NovaDb 扩展）

| 函数 | 别名 | 签名 | 返回类型 | NULL 语义 | 错误语义 | 备注 |
|---|---|---|---|---|---|---|
| `COSINE_SIMILARITY` | - | `COSINE_SIMILARITY(v1, v2)` | DOUBLE | NULL → NULL | 维度不匹配→NULL | 余弦相似度 [-1, 1]，值越大越相似 |
| `EUCLIDEAN_DISTANCE` | - | `EUCLIDEAN_DISTANCE(v1, v2)` | DOUBLE | NULL → NULL | 维度不匹配→NULL | L2 距离，值越小越相似 |
| `DOT_PRODUCT` | - | `DOT_PRODUCT(v1, v2)` | DOUBLE | NULL → NULL | 维度不匹配→NULL | 内积；性能优于余弦相似度 |
| `VECTOR_NEAREST` | - | `VECTOR_NEAREST(query_vec, table, top_k, metric)` | - | NULL → NULL | - | Top-K 最相似向量查询；metric 支持 `cosine`/`euclidean`/`dot` |

---

## 4. P2 占位（远期规划）

### 4.1 聚合

| 函数 | 签名 | 返回类型 | 备注 |
|---|---|---|---|
| `STDDEV` | `STDDEV(expr)` | DOUBLE | 样本标准差 |
| `VARIANCE` | `VARIANCE(expr)` | DOUBLE | 样本方差 |

### 4.2 字符串

| 函数 | 签名 | 返回类型 | 备注 |
|---|---|---|---|
| `REVERSE` | `REVERSE(s)` | STRING | 反转字符串 |
| `LPAD` | `LPAD(s, len, pad)` | STRING | 左填充至指定长度，pad 默认空格 |
| `RPAD` | `RPAD(s, len, pad)` | STRING | 右填充至指定长度，pad 默认空格 |

### 4.3 数值

| 函数 | 签名 | 返回类型 | 备注 |
|---|---|---|---|
| `PI` | `PI()` | DOUBLE | 圆周率常量 |
| `EXP` | `EXP(n)` | DOUBLE | 自然指数 e^n |
| `LOG` | `LOG(n)` | DOUBLE | 自然对数 ln(n) |
| `LOG10` | `LOG10(n)` | DOUBLE | 常用对数 log10(n) |

### 4.4 日期时间

| 函数 | 签名 | 返回类型 | 备注 |
|---|---|---|---|
| `LAST_DAY` | `LAST_DAY(dt)` | DATETIME | 月末日期；MySQL 扩展 |
| `TIMESTAMPDIFF` | `TIMESTAMPDIFF(unit, dt1, dt2)` | LONG | 按指定单位计算差值；unit 支持 SECOND/MINUTE/HOUR/DAY/MONTH/YEAR |
| `TIMESTAMPADD` | `TIMESTAMPADD(unit, n, dt)` | DATETIME | 按指定单位加减时间 |

### 4.5 系统/元数据

| 函数 | 签名 | 返回类型 | 备注 |
|---|---|---|---|
| `USER` / `CURRENT_USER` | `USER()` | STRING | 当前连接用户名（v1 无权限系统，返回 `'nova'`） |
| `CONNECTION_ID` | `CONNECTION_ID()` | LONG | 当前连接唯一 ID（服务器模式） |

---

## 5. 明确不支持（v1）

以下函数/类别在 v1 **不实现**，与需求规格书 §5.11 对齐：

| 类别 | 函数 | 原因 |
|---|---|---|
| **窗口函数** | `ROW_NUMBER()`、`RANK()`、`DENSE_RANK()`、`LAG()`、`LEAD()`、`FIRST_VALUE()`、`LAST_VALUE()` | v1 不支持窗口函数（规划 v1.1+） |
| **正则** | `REGEXP`/`RLIKE`、`REGEXP_REPLACE`、`REGEXP_SUBSTR` | 性能开销大，v1 不实现 |
| **全文检索** | - | v1 不承诺 |
| **格式化** | `FORMAT(value, format)` | 复杂格式化由应用层实现 |
| **音似** | `SOUNDEX(str)`、`DIFFERENCE(s1, s2)` | 冷门，性能低 |
| **三角函数** | `SIN`/`COS`/`TAN`/`ASIN`/`ACOS`/`ATAN` | 冷门，业务场景少 |
| **加密** | `ENCRYPT`/`AES_ENCRYPT`/`AES_DECRYPT` | 加密由应用层实现 |
| **JSON** | `JSON_EXTRACT`/`JSON_SET`/`JSON_ARRAY`/`JSON_OBJECT` | v1 不承诺，由应用层处理 |

---

## 6. 兼容性提示（实现时需要明确）

### 6.1 `date +/- INTERVAL` 形式

若 v1 不支持 `dt + INTERVAL n unit` / `dt - INTERVAL n unit` 这种运算符形式，需要在 SQL 方言说明中明确，并推荐使用 `DATE_ADD/DATE_SUB`。

### 6.2 字符串长度口径

`LENGTH/LEN` 返回"字节数"还是"字符数"需要在 v1 固定；建议：

- **v1 默认**：`LENGTH` = UTF-8 字节数（与存储层/索引一致）；
- **后续扩展**：引入 `CHAR_LENGTH` = 字符数（若用户需求充分）。

### 6.3 类型转换失败策略

v1 采用 `CAST`/`CONVERT` 转换失败返回 NULL 的策略，可降低业务异常分支。若未来改为抛错需在版本说明中明确。

### 6.4 多数据库迁移兼容

- 优先兼容 SQL-92 标准常用函数；
- 对 MySQL/PostgreSQL/SQL Server 的重要扩展予以支持并在本文档中标注来源（如 `MySQL 风格`、`SQL Server 风格`）；
- ORM 集成（XCode/EF Core/Dapper）常用函数投影需确保可用。

---

## 7. 实现与测试检查清单

### 7.1 函数实现检查清单

| 项目 | 要求 |
|---|---|
| **语法完整性** | 支持完整的函数签名与参数变体 |
| **NULL 传播** | 准确实现 NULL 处理逻辑（标量函数 NULL 传播，聚合函数忽略 NULL） |
| **类型强制转换** | 参数自动转换（如 `ROUND('3.14', 2)` 字符串自动转 DECIMAL） |
| **错误处理** | 参数范围错误返回 NULL 而非异常（与 §1.4 一致） |
| **性能基准** | 百万行数据下标量函数执行时间 < 1ms（地理/向量复杂计算除外） |
| **测试覆盖** | 正常值、NULL、边界值、类型不匹配场景均需测试用例 |

### 7.2 优先级实施建议

1. **v1.0 必须完成**：所有 P0 函数（§2，共约 40 个）
2. **v1.0 应该完成**：P1 函数中高频函数（§3，共约 30 个）
3. **v1.1+ 规划**：P2 及扩展函数（§4，窗口函数/JSON/正则等）

### 7.3 兼容性测试场景

| 场景 | 验证目标 |
|---|---|
| **多数据库迁移** | 常见 MySQL/PG/SQL Server 函数可无缝迁移 |
| **ORM 集成** | XCode/EF Core/Dapper 常用函数投影正常工作 |
| **性能压力** | 百万行聚合/字符串操作不超时（参考规格书 §6.1） |
