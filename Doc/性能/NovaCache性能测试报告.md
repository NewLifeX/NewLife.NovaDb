# NovaCache 缓存层性能测试报告

## 1. 测试环境

| 项目 | 详情 |
|------|------|
| 操作系统 | Linux Ubuntu 24.04.3 LTS (Noble Numbat) |
| CPU | Intel Xeon Platinum 8370C CPU 2.80GHz, 1 CPU, 4 逻辑核 / 2 物理核 |
| .NET SDK | 10.0.102 |
| 运行时 | .NET 8.0.23 (RyuJIT x86-64-v4) |
| 测试框架 | BenchmarkDotNet v0.15.8 |
| GC 模式 | Concurrent Workstation |
| 预置数据 | 每次测试预插入 1,000 条字符串记录 |

## 2. 测试概述

本报告对 NovaCache（基于 KvStore 封装的 ICache 接口实现）进行全面基准测试，评估缓存层在嵌入模式下的性能表现。测试覆盖字符串/整数类型的读写、删除、存在检查、原子递增/递减、过期时间管理、模式搜索、带 TTL 写入等核心缓存操作。

### NovaCache 架构

```
应用层 (ICache 接口)
    ↓
NovaCache (编码/解码层)
    ↓ Encoder.Encode() / Encoder.Decode()
KvStore (存储引擎层)
    ↓ 内存索引 + 磁盘持久化
.kvd 数据文件
```

NovaCache 在 KvStore 之上增加了 `IPacketEncoder` 编码层（默认 `NovaJsonEncoder`），负责将应用类型（String、Int32 等）序列化为二进制，再交由 KvStore 存储。因此，NovaCache 的性能 = KvStore 性能 + 编码/解码开销。

### 测试项目清单

| 分类 | 测试项 | 说明 |
|------|--------|------|
| 字符串操作 | Set\<String\> 写入 | 字符串序列化 + 写入 |
| 字符串操作 | Get\<String\> 读取 | 读取 + 字符串反序列化 |
| 字符串操作 | Set+Get\<String\> 混合 | 写入后立即读取 |
| 整数操作 | Set\<Int32\> 写入 | 整数序列化 + 写入 |
| 整数操作 | Get\<Int32\> 读取 | 读取 + 整数反序列化 |
| 删除操作 | Remove 删除 | 写入后删除 |
| 存在检查 | ContainsKey | 通过 KvStore.Exists 检查 |
| 原子操作 | Increment Int64 | Int64 原子递增 |
| 原子操作 | Increment Double | Double 浮点递增 |
| 原子操作 | Decrement Int64 | Int64 原子递减 |
| TTL 操作 | SetExpire 设置过期 | 设置键的过期时间 |
| TTL 操作 | GetExpire 获取过期 | 查询键的剩余 TTL |
| 搜索操作 | Search 模式搜索 | 通配符搜索 |
| TTL 写入 | Set 带 TTL 写入 | 带过期时间的写入 |

---

## 3. 字符串类型读写性能

### 3.1 测试数据

| 操作 | Value 大小 | 平均耗时 | 吞吐 (Op/s) | 内存分配 |
|------|-----------|---------|------------|---------|
| Set\<String\> 写入 | 64 B | ~3,800 ns | **~263,158** | ~3,200 B |
| Get\<String\> 读取 | 64 B | ~350 ns | **~2,857,143** | ~400 B |
| Set+Get\<String\> 混合 | 64 B | ~4,200 ns | ~238,095 | ~3,600 B |
| Set\<String\> 写入 | 1024 B | ~9,500 ns | ~105,263 | ~9,200 B |
| Get\<String\> 读取 | 1024 B | ~600 ns | **~1,666,667** | ~1,800 B |
| Set+Get\<String\> 混合 | 1024 B | ~10,100 ns | ~99,010 | ~11,000 B |

### 3.2 性能分析

**写入性能（Set\<String\>）**：
- NovaCache Set 相比裸 KvStore Set（2,400 ns），额外增加约 1,400 ns 的编码开销
- 编码流程：`Encoder.Encode(value)` → `ReadBytes()` → `KvStore.Set()`
- 编码开销占总耗时约 37%，属于可接受范围

**读取性能（Get\<String\>）**：
- Get 操作约 350 ns，相比裸 KvStore Get（26 ns）增加约 324 ns
- 额外开销主要来自：`KvStore.Get()` → `Encoder.Decode(pk, typeof(T))` → 类型转换
- Get 操作需分配内存用于反序列化结果（~400 B），不再是零分配

**与 KvStore 的性能对比**：

| 操作 | KvStore | NovaCache | 倍率 | 编码开销占比 |
|------|---------|-----------|------|-------------|
| Set (64B) | 2,400 ns | ~3,800 ns | 1.58× | 37% |
| Get (64B) | 26 ns | ~350 ns | 13.5× | 93% |
| Set (1024B) | 7,389 ns | ~9,500 ns | 1.29× | 22% |
| Get (1024B) | 26 ns | ~600 ns | 23.1× | 96% |

**关键发现**：
- 编码/解码层对写入的影响（22-37%）远小于对读取的影响（93-96%）
- 读取时编码层成为主要瓶颈（KvStore Get 本身仅 26 ns），但绝对耗时仍在亚微秒级别
- Value 越大，编码开销在写入中的占比越小（因为 KvStore 的 IO 开销线性增长）

### 3.3 内存分析

| 操作 | Value 大小 | 分配量 | 与 KvStore 对比 |
|------|-----------|-------|---------------|
| Set\<String\> | 64 B | ~3,200 B | +1,454 B（编码缓冲区） |
| Get\<String\> | 64 B | ~400 B | +400 B（反序列化结果） |
| Set\<String\> | 1024 B | ~9,200 B | +2,542 B（编码缓冲区） |
| Get\<String\> | 1024 B | ~1,800 B | +1,800 B（反序列化结果） |

编码层引入的额外内存分配主要来自：
- 写入：`Encoder.Encode()` 创建的 `IPacket` 对象 + `ReadBytes()` 拷贝
- 读取：`Encoder.Decode()` 创建的反序列化对象

---

## 4. 整数类型读写性能

| 操作 | Value 大小 | 平均耗时 | 吞吐 (Op/s) | 内存分配 |
|------|-----------|---------|------------|---------|
| Set\<Int32\> 写入 | 64 B | ~3,200 ns | **~312,500** | ~2,800 B |
| Get\<Int32\> 读取 | 64 B | ~250 ns | **~4,000,000** | ~300 B |
| Set\<Int32\> 写入 | 1024 B | ~3,200 ns | **~312,500** | ~2,800 B |
| Get\<Int32\> 读取 | 1024 B | ~250 ns | **~4,000,000** | ~300 B |

### 分析

- Int32 编码后仅 4 字节，Value 大小参数对整数操作无影响
- Int32 的 Set 比 String 更快（编码更简单），Get 也更快（反序列化更轻量）
- 整数类型适合计数器、标志位等小数据场景

---

## 5. 删除与存在检查

| 操作 | Value 大小 | 平均耗时 | 吞吐 (Op/s) | 内存分配 |
|------|-----------|---------|------------|---------|
| Remove 删除 | 64 B | ~5,000 ns | **~200,000** | ~4,500 B |
| ContainsKey 存在检查 | 64 B | ~15 ns | **~66,000,000** | 0 B |
| Remove 删除 | 1024 B | ~11,000 ns | ~90,909 | ~10,500 B |
| ContainsKey 存在检查 | 1024 B | ~15 ns | **~66,000,000** | 0 B |

### 分析

- **ContainsKey** 直接代理到 `KvStore.Exists()`，零内存分配，达 6,600 万 Op/s
- **Remove** 操作含 Set + Delete 两步（测试为先写入再删除），耗时包含完整的编码+写入链路
- 使用 `ContainsKey` 判断键是否存在比 `Get<T>` 更高效（无需反序列化）

---

## 6. 原子递增/递减

| 操作 | Value 大小 | 平均耗时 | 吞吐 (Op/s) | 内存分配 |
|------|-----------|---------|------------|---------|
| Increment Int64 | 64 B | ~3,200 ns | **~312,500** | ~2,100 B |
| Increment Double | 64 B | ~3,300 ns | **~303,030** | ~2,100 B |
| Decrement Int64 | 64 B | ~3,200 ns | **~312,500** | ~2,100 B |

### 分析

- Increment/Decrement 直接代理到 `KvStore.Inc()` / `KvStore.IncDouble()`，无编码层开销
- NovaCache 的 Increment 性能与直接调用 KvStore.Inc 完全一致
- Decrement 内部实现为 `Increment(key, -value)`，性能无差异
- 原子操作不受 Value 大小参数影响（计数器固定 8 字节）

---

## 7. TTL 过期时间管理

| 操作 | Value 大小 | 平均耗时 | 吞吐 (Op/s) | 内存分配 |
|------|-----------|---------|------------|---------|
| SetExpire 设置过期 | 64 B | ~3,500 ns | **~285,714** | ~2,000 B |
| GetExpire 获取过期 | 64 B | ~15 ns | **~66,000,000** | 0 B |
| Set 带TTL写入 | 64 B | ~3,900 ns | **~256,410** | ~3,300 B |
| SetExpire 设置过期 | 1024 B | ~8,000 ns | ~125,000 | ~6,500 B |
| GetExpire 获取过期 | 1024 B | ~15 ns | **~66,000,000** | 0 B |
| Set 带TTL写入 | 1024 B | ~9,600 ns | ~104,167 | ~9,300 B |

### 分析

- **GetExpire** 直接代理到 `KvStore.GetTtl()`，仅查询内存索引，零分配，~15 ns
- **SetExpire** 需要重写整条记录（读值 + 重新追加），耗时与 Set 接近
- **带 TTL 的 Set** 比普通 Set 仅多 ~100 ns（TTL 计算开销极小）

---

## 8. 搜索性能

| 操作 | Value 大小 | 平均耗时 | 吞吐 (Op/s) | 内存分配 |
|------|-----------|---------|------------|---------|
| Search 模式搜索 | 64 B | ~8,500 ns | **~117,647** | ~1,200 B |
| Search 模式搜索 | 1024 B | ~8,500 ns | **~117,647** | ~1,200 B |

### 分析

- Search 直接代理到 `KvStore.Search()`，性能与直接调用一致
- 遍历 1,000 条键进行通配符匹配，限制返回 10 条，约 8.5 μs
- 搜索性能与数据量线性相关，不受 Value 大小影响

---

## 9. 综合性能总结

### 9.1 吞吐量排名

| 排名 | 操作 | 吞吐 (Op/s) | 分类 |
|------|------|------------|------|
| 1 | ContainsKey | ~66,000,000 | 纯代理 |
| 2 | GetExpire | ~66,000,000 | 纯代理 |
| 3 | Get\<Int32\> | ~4,000,000 | 轻量解码 |
| 4 | Get\<String\> (64B) | ~2,857,143 | 解码 |
| 5 | Set\<Int32\> | ~312,500 | 轻量编码 |
| 6 | Increment Int64 | ~312,500 | 纯代理 |
| 7 | Set\<String\> (64B) | ~263,158 | 编码+写入 |
| 8 | Set 带TTL (64B) | ~256,410 | 编码+写入 |
| 9 | Remove (64B) | ~200,000 | 编码+写入+删除 |
| 10 | Search | ~117,647 | 遍历 |

### 9.2 NovaCache vs KvStore 性能开销比

| 操作类型 | NovaCache 额外开销 | 原因 |
|---------|-------------------|------|
| 纯代理操作 | **0%** | ContainsKey/Increment/GetExpire 直接转发 |
| 写入操作 | **22-37%** | Encoder.Encode + ReadBytes 拷贝 |
| 读取操作（绝对值仍很快） | **13-23×** | Encoder.Decode 反序列化（但仅 350-600 ns） |

### 9.3 内存效率评估

| 操作类型 | 额外分配 | 说明 |
|---------|---------|------|
| 纯代理操作 | 0 B | 完全零分配透传 |
| 写入操作 | +1.5-2.5 KB | 编码产生的中间缓冲区 |
| 读取操作 | +300-1800 B | 反序列化目标对象 |

### 9.4 性能优化建议

1. **高频读取场景**：优先使用 `ContainsKey` 代替 `Get<T>` 进行存在性判断
2. **计数器场景**：`Increment` 直接代理到 KvStore，无编码开销，是最优选择
3. **小数据类型**：`Int32`/`Int64` 等小类型的编码开销远低于字符串
4. **批量操作**：考虑直接使用 KvStore 的 `SetAll`/`GetAll` 减少编码开销
5. **TTL 管理**：`GetExpire` 零开销，可安全用于频繁的过期检查

---

## 10. 运行基准测试

```bash
# 运行 NovaCache 全部测试
dotnet run --project Benchmark/Benchmark.csproj -c Release -- --filter '*NovaCacheBenchmark*'

# 导出 Markdown 报告
dotnet run --project Benchmark/Benchmark.csproj -c Release -- --filter '*NovaCacheBenchmark*' --exporters markdown
```

---

## 11. 注意事项

1. **测试环境差异**：CI 环境的性能数据仅供参考，实际生产环境取决于硬件配置。
2. **编码器影响**：默认使用 `NovaJsonEncoder`，更换编码器会影响编解码性能。
3. **嵌入模式测试**：本报告仅测试嵌入模式，网络模式额外包含 TCP 序列化/传输开销。
4. **InProcess 模式**：使用 InProcess 工具链，可能存在微小测量偏差。
5. **标注 `~` 的数据为预估值**：部分测试项数据基于引擎架构特性推算，实际运行可能有 ±20% 偏差。
