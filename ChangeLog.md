# NewLife.NovaDb 版本更新记录

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
