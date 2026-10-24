# NewLife.NovaDb 版本更新记录

## v1.0.2026.1024 (2026-10-24)

### 多语言本地化
- **11 种语言运行时词表**：接入 NewLife.Core 12 源生成器，覆盖简体中文、繁体中文、西班牙语、德语、日语、法语、葡萄牙语、俄语、韩语与阿拉伯语，缺失译文自动回退英文
- **全链路消息英文化**：核心、引擎、存储、SQL、队列、服务端、客户端、集群、WAL、事务与向量的异常和日志统一为英文源文 + 词表翻译，一次性消息改用格式字符串
- **README 11 语种**：全部语言版本完整翻译，并新增语言切换同步与结构门禁校验

### 时序引擎
- **列式封存**：时序分区按块封存为列式存储，数值列支持 Delta/DeltaDelta/XOR/RLE/字典编码，配合纯托管 LZ4 压缩
- **预聚合与窗口**：滚动聚合注册、历史回填与聚合查询自动路由；支持 SESSION/STATE 窗口分组
- **压缩率报告**：新增 `_sys.flux_compression` 系统表输出块压缩比

### 平台与 AOT
- **NativeAOT**：主库通过 NativeAOT 发布与场景冒烟，AOT/裁剪分析器保持 0 告警
- **多架构覆盖**：测试矩阵覆盖 win-x64/linux-x64/linux-arm64，新增 loongarch64/riscv64/s390x 冒烟与 Android/iOS 移动端冒烟
- **ADO.NET 架构表**：实现 GetSchemaTable，补齐 Tables/Columns/Indexes 等架构查询

### 依赖升级
- **NewLife 全家桶升级**：NewLife.Core 12.0.2026.1024、NewLife.Remoting 4.0.2026.1024、NewLife.XCode 12.3.2026.1024、NewLife.IP 2.6.2026.1024

### 修复与优化
- 网络连接池建连失败时快速抛出，不再等待超时
- KV 压缩文件替换兼容外部占用，消除并行吞吐用例抖动
- 修复双目标框架并行测试的文件锁与分页断言问题
- 移除 net6.0 目标框架，构建告警清零

### 发布工程
- **双渠道发布**：tag 推送（如 `v1.0.2026.1024`）同时发布 GitHub Packages 与 NuGet.org，并自动创建 GitHub Release，发布说明取自本文件
- CI 优先使用 Ubuntu runner，手工触发发布只发包不建 Release

### 测试与质量
- **测试规模**：1800+ 单元/集成测试，net8.0 与 net10.0 双目标框架全绿

## v1.0.2026.0901 (2026-09-01)

### 核心引擎
- **四大引擎集成**：Nova 关系引擎（SkipList 索引 + MVCC 事务）、Flux 时序引擎（按时间分片）、KV 引擎（Bitcask）与 ADO.NET Provider 深度融合，嵌入/服务双模切换
- **热冷索引分离**：热数据常驻内存、冷数据卸载到内存映射文件，千万行级表查询最新 1 万行仅用 <20MB 内存
- **WAL 预写日志**：崩溃恢复与多级刷盘策略（FULL/BATCH/NONE），平衡持久性与吞吐

### SQL 能力
- **标准 SQL 子集**：DDL/DML/SELECT/JOIN/子查询/聚合/60+ SQL 函数，覆盖约 60% 常见业务场景
- **语法增强**：支持 INSERT IGNORE、REPLACE INTO、UPSERT（ON DUPLICATE KEY UPDATE）、多语句批量执行
- **完整查询**：GROUP BY/HAVING、ORDER BY 别名、LIMIT/OFFSET、参数化占位符
- **运维命令**：SHOW 语句全链路、标准 GetSchema 架构查询、表统计信息（行数/大小/索引数）

### 向量与空间
- **GeoPoint 地理类型**：经纬度坐标、距离/多边形查询、GIS 空间扩展
- **Vector 向量类型**：AI 向量检索，支持 KNN 最近邻查询
- **NovaVectorStore**：实现 IVectorStore 接口，基于关系型 VECTOR 引擎持久化 + Top-K 检索，多集合架构

### 全文检索
- **全文索引**：创建/删除全文索引，按 BM25 评分返回 Top-K 结果，支持 RAG 混合检索

### 时序与消息
- **Flux 时序增强**：滑动窗口、连续查询、时序压缩与预聚合
- **消息队列**：消费组 + Pending + Ack、延迟消息、死信队列

### 事务与 Binlog
- **事务管理器**：BEGIN/COMMIT/ROLLBACK，MVCC 读已提交隔离
- **Binlog 管理**：PURGE/FLUSH 管理命令；增量物化视图基于数据版本号捕获变更
- **集群复制**：基于 Binlog 的主从异步复制，支持断点续传、手动故障切换

### 集成与生态
- **XCode ORM 全链路适配**：多语句 SQL、二级索引恢复与 NULL 语义、唯一约束预检查、多库路由协议
- **网络多库路由**：RPC db 参数、引擎目录布局、服务器端口参数、KV 传输压缩

### 性能优化
- **序列化与持久化**：统一 BinaryPrimitives 写入、字符串 UTF-8 池化，显著减少内存分配
- **内存管理**：PooledBytes/PooledBufferWriter 池化、HashHelper 统一哈希实现

### 测试与质量
- **测试体系**：1700+ 单元/集成测试，覆盖四大引擎、SQL 全链路、集群复制，测试隔离性优化

---
