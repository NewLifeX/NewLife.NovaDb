# NewLife.NovaDb - Flux 引擎架构（时序 + MQ）

## 1. 概述

Flux Engine 是 NovaDb 的时序与消息队列引擎,采用**统一数据模型**:每一条 MQ 消息就是时序表的一行数据。Stream/Topic 名即时序表名,实现了时序存储与消息队列的深度融合。

## 2. 统一数据模型

### 2.1 核心设计理念

```
时序数据 = MQ 消息

FluxEntry {
    MessageId: String         # 消息 ID = 时序表主键
    Timestamp: DateTime       # 时间戳（从 MessageId 解析）
    Sequence: Int64           # 序列号（从 MessageId 解析）
    Fields: Map<String, Object> # 消息字段 = 时序表列
}

操作映射:
- 发送消息 = 向时序表追加一行（INSERT）
- 消费消息 = 按 MessageId 顺序读取行（SELECT）
- ACK 消息 = 标记消费位置（UPDATE 消费组偏移）
```

### 2.2 MessageId 格式

```
格式: {timestamp}-{sequence}
示例: 1640995200123-0001

解析:
  timestamp = 1640995200123  # 毫秒级时间戳
  sequence = 1               # 同毫秒内自增序列号

特性:
- 全局单调递增
- 时间有序
- 同毫秒支持多条消息（sequence 自增）
- 可直接作为时序表主键
```

## 3. 时间分片

### 3.1 分片粒度

按配置粒度（小时/天/月）管理时间分片:

| 粒度 | 适用场景 | 单片大小估算 |
|------|---------|------------|
| 小时 | 高频写入（如物联网传感器数据） | ~1-10 GB/片 |
| 天 | 中频写入（如应用日志） | ~10-100 GB/片 |
| 月 | 低频写入（如报表数据） | ~100GB-1TB/片 |

### 3.2 分片文件组织

详见[存储架构.md](../存储架构.md),此处仅概述:

```
{db_path}/
  flux/
    {stream_name}/
      {stream_name}_20241201.flux   # 2024-12-01 的数据分片
      {stream_name}_20241202.flux   # 2024-12-02 的数据分片
      {stream_name}_20241203.flux   # 2024-12-03 的数据分片
```

### 3.3 自动创建与滚动

```csharp
public class FluxEngine
{
    /// <summary>追加消息到时序表/流</summary>
    public void Append(String streamName, FluxEntry entry)
    {
        // 1. 根据时间戳计算分片名
        var shardName = CalculateShardName(entry.Timestamp);
        
        // 2. 获取或创建分片
        var shard = GetOrCreateShard(streamName, shardName);
        
        // 3. 追加数据
        shard.Append(entry);
    }
    
    private String CalculateShardName(DateTime timestamp)
    {
        return timestamp.ToString("yyyyMMdd"); // 按天分片
    }
}
```

## 4. MQ 功能（Stream）

### 4.1 组件架构

```
Producer → StreamManager → FluxEngine (时序表追加)
                ↓
ConsumerGroup → Consumer → Pending → Ack → UpdateOffset
```

| 组件 | 职责 |
|------|------|
| StreamManager | 流管理器,维护消费组、消费者、偏移量 |
| ConsumerGroup | 消费组,记录消费偏移和 Pending 队列 |
| Consumer | 消费者,从流读取消息 |
| Pending | 待确认队列,记录已读取但未 ACK 的消息 |

### 4.2 消费组与偏移量

```csharp
public class ConsumerGroup
{
    public String Name { get; set; }                // 消费组名
    public String StreamName { get; set; }          // 所属流名
    public String LastConsumedMessageId { get; set; } // 最后消费的 MessageId
    public Dictionary<String, PendingMessage> Pending { get; set; } = []; // 待确认消息
}

public class PendingMessage
{
    public String MessageId { get; set; }       // 消息 ID
    public String ConsumerId { get; set; }      // 消费者 ID
    public DateTime DeliveryTime { get; set; }  // 投递时间
    public Int32 RetryCount { get; set; }       // 重试次数
}
```

### 4.3 消费流程（At-Least-Once）

```
1. Consumer 读取消息 (XREAD / XREADGROUP)
   → SELECT * FROM flux_stream WHERE MessageId > '{LastConsumedMessageId}' LIMIT {count}
   
2. 消息进入 Pending 队列
   → INSERT INTO pending (MessageId, ConsumerId, DeliveryTime, RetryCount)
   
3. 业务处理消息
   
4. 成功后 ACK (XACK)
   → DELETE FROM pending WHERE MessageId = '{MessageId}'
   → UPDATE ConsumerGroup SET LastConsumedMessageId = '{MessageId}'
   
5. 失败或超时未 ACK
   → 消息留在 Pending 队列
   → 达到重试次数后进入死信队列
```

### 4.4 阻塞读取

支持阻塞读取 + 超时（语义类似 Redis `XREADGROUP BLOCK`）:

```csharp
public List<FluxEntry> ReadGroup(String groupName, String consumerName, 
    Int32 count, Int32 blockMillis)
{
    var endTime = DateTime.Now.AddMilliseconds(blockMillis);
    
    while (DateTime.Now < endTime)
    {
        // 尝试读取新消息
        var messages = ReadNewMessages(groupName, count);
        
        if (messages.Count > 0)
            return messages;
        
        // 等待新消息到达（事件通知或短暂休眠）
        WaitForNewMessages(timeout: 100);
    }
    
    return []; // 超时返回空
}
```

**优势**:
- 避免客户端忙轮询
- 降低 CPU 消耗
- 实时性更好

### 4.5 延迟消息

```csharp
public class FluxEntry
{
    public String MessageId { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime? DeliverAt { get; set; }  // 延迟投递时间（可选）
    public Dictionary<String, Object?> Fields { get; set; }
}

// 发送延迟消息
producer.Send(new FluxEntry
{
    MessageId = GenerateMessageId(),
    Timestamp = DateTime.Now,
    DeliverAt = DateTime.Now.AddMinutes(30), // 30 分钟后投递
    Fields = new() { ["orderId"] = "123", ["action"] = "cancel" }
});
```

**实现机制**:

1. **延迟队列索引**（专用索引）
   ```
   DelayIndex: SortedSet<(DeliverAt, MessageId)>
   ```

2. **后台扫描线程**
   ```csharp
   while (true)
   {
       var now = DateTime.Now;
       var readyMessages = DelayIndex.GetRange(min: DateTime.MinValue, max: now);
       
       foreach (var (deliverAt, messageId) in readyMessages)
       {
           // 移除延迟索引
           DelayIndex.Remove((deliverAt, messageId));
           
           // 标记消息可消费（设置 DeliverAt = NULL 或移动到普通队列）
           MarkAsReady(messageId);
       }
       
       Thread.Sleep(1000); // 每秒扫描一次
   }
   ```

3. **消费过滤**
   - 普通消费读取时过滤 `DeliverAt > NOW()` 的消息
   - 到期后消息自动可被消费

**应用场景**:
- 订单超时取消（30 分钟未支付）
- 定时通知（活动开始前 1 小时提醒）
- 重试退避（失败后延迟 5 分钟重试）

### 4.6 死信队列（DLQ）

```csharp
public class StreamManager
{
    /// <summary>处理超时或失败的 Pending 消息</summary>
    public void ProcessPendingTimeouts()
    {
        var timeoutThreshold = DateTime.Now.AddMinutes(-5); // 5 分钟超时
        
        foreach (var (messageId, pending) in GetPendingMessages())
        {
            if (pending.DeliveryTime < timeoutThreshold)
            {
                pending.RetryCount++;
                
                // 达到最大重试次数 → 死信队列
                if (pending.RetryCount >= MaxRetryCount)
                {
                    SendToDeadLetterQueue(messageId, pending);
                    RemoveFromPending(messageId);
                }
                else
                {
                    // 重新投递
                    Redeliver(messageId, pending);
                }
            }
        }
    }
    
    private void SendToDeadLetterQueue(String messageId, PendingMessage pending)
    {
        var dlqStreamName = $"{pending.StreamName}_DLQ";
        var dlqEntry = new FluxEntry
        {
            MessageId = GenerateMessageId(),
            Timestamp = DateTime.Now,
            Fields = new()
            {
                ["OriginalMessageId"] = messageId,
                ["FailureReason"] = "超时未 ACK",
                ["RetryCount"] = pending.RetryCount,
                ["LastDeliveryTime"] = pending.DeliveryTime,
                ["ConsumerId"] = pending.ConsumerId
            }
        };
        
        FluxEngine.Append(dlqStreamName, dlqEntry);
    }
}
```

**死信队列特性**:
- 命名: `{QueueName}_DLQ`（如 `orders_DLQ`）
- 内容: 原始消息 + 失败原因 + 失败次数等诊断信息
- 处理: 可独立订阅死信队列,人工处理或自动重试
- 管理: 支持重新投递（Retry）或永久删除

### 4.7 可观测性

系统表/统计应可查询队列状态:

```sql
-- 查询消费组状态
SELECT * FROM sys_consumer_groups WHERE stream_name = 'orders';

-- 查询 Pending 消息
SELECT * FROM sys_pending_messages WHERE group_name = 'payment_service';

-- 查询延迟消息
SELECT * FROM sys_delay_messages WHERE stream_name = 'notifications';

-- 查询死信队列
SELECT * FROM orders_DLQ ORDER BY timestamp DESC LIMIT 100;
```

**监控指标**:
- 消费组滞后量（Lag）: `LastProducedMessageId - LastConsumedMessageId`
- Pending 消息数量
- 延迟消息数量
- 死信队列消息数量
- 消费速率（条/秒）

## 5. 数据保留（TTL）

### 5.1 保留策略

Flux Engine 支持两种保留策略:

| 策略 | 配置项 | 说明 |
|------|-------|------|
| 按时间保留 | `FluxDefaultTtlSeconds` | 保留最近 N 秒的数据,删除更早的分片 |
| 按文件大小 | `FluxMaxTotalSize` | 总大小超过限制时,删除最旧的分片 |

### 5.2 分片删除流程

```csharp
public void CleanupExpiredShards()
{
    var ttlThreshold = DateTime.Now.AddSeconds(-FluxDefaultTtlSeconds);
    
    foreach (var shard in GetAllShards())
    {
        // 按时间删除
        if (shard.Timestamp < ttlThreshold)
        {
            DeleteShard(shard);
            continue;
        }
        
        // 按总大小删除
        var totalSize = GetTotalSize();
        if (totalSize > FluxMaxTotalSize)
        {
            DeleteOldestShard();
        }
    }
}
```

### 5.3 删除安全保证

删除分片前检查:
- 所有消费组是否已消费该分片
- 是否有 Pending 消息在该分片中
- 如果有未消费消息,记录警告日志

## 6. 性能优化

### 6.1 批量追加

```csharp
public void BatchAppend(String streamName, List<FluxEntry> entries)
{
    // 按分片分组
    var shardGroups = entries.GroupBy(e => CalculateShardName(e.Timestamp));
    
    foreach (var group in shardGroups)
    {
        var shard = GetOrCreateShard(streamName, group.Key);
        shard.BatchAppend(group.ToList());
    }
}
```

### 6.2 分片预读

```csharp
/// <summary>预读即将访问的分片到内存</summary>
public void PrefetchShards(String streamName, DateTime startTime, DateTime endTime)
{
    var shardNames = CalculateShardRange(startTime, endTime);
    
    foreach (var shardName in shardNames)
    {
        var shard = GetOrCreateShard(streamName, shardName);
        shard.LoadToMemory(); // 预加载到页面缓存
    }
}
```

### 6.3 压缩

对历史分片启用压缩:
- 冷分片（超过 N 天未访问）自动压缩
- 压缩算法: LZ4、Snappy
- 查询时解压（透明）

## 7. 设计决策

### D1: 时序数据与 MQ 消息统一
- 简化架构,避免维护两套存储
- MessageId 即主键,天然有序
- 复用时序引擎的分片、TTL、压缩等能力

### D2: 时间分片
- 天然支持 TTL（删除旧分片）
- 查询性能优化（只扫描相关分片）
- 文件大小可控,便于备份与迁移

### D3: At-Least-Once 语义
- 通过 Pending 队列保证
- 消费者崩溃后可重新投递
- 适合大多数业务场景

### D4: 延迟消息与死信队列
- 延迟消息满足定时任务场景
- 死信队列保证消息不丢失,便于排查问题

### D5: 可观测性优先
- 系统表查询队列状态
- 支持 SQL 诊断堆积、超时、重试等问题

## 8. 未来扩展

### 8.1 Exactly-Once 语义
- 幂等生产者（去重）
- 事务性消费（消费 + 业务操作原子提交）

### 8.2 分区并行消费
- 按 Hash Key 分区
- 每个分区独立消费,提升吞吐量

### 8.3 优先级队列
- 消息带优先级字段
- 高优先级消息优先投递

### 8.4 消息过滤
- 消费时按条件过滤（如 `WHERE status='pending'`）
- 减少无效消息传输
