# QueryBench —— 三库查询性能对比基准

同一份确定性数据、同一套参数化 SQL（Q1~Q10，可选 Q11 连接），对比 NovaDb 嵌入模式 / SQLite / MySQL 的 p50/p95。数据由固定种子按行号生成，装载后逐表清理，CSV 支持中断续跑。

```powershell
# 冒烟（约 1 分钟）：三库两档小数据
dotnet run --project Benchmark/QueryBench -c Release -- --db nova,sqlite,mysql --scale tiny,small --table all --budget 5 --iterations 20

# 正式对比：生成 Doc/性能/data/query_bench.csv，并输出 Markdown 报告
dotnet run --project Benchmark/QueryBench -c Release -- --scale small --budget 30 --report

# 只装载数据供手工分析（保留表）：--load-only --keep
# 断点续跑：重复执行即可，已完成且哈希一致的组合自动跳过；--force 强制重跑
# MySQL 连接：--mysql "<连接串>" > 环境变量 QUERYBENCH_MYSQL > Bin/Benchmark/Config/mysql.config（见 mysql.config.sample）
```

规模：`tiny=100`、`small=10000`、`medium=1000000`、`large=20000000`。表：`t_noidx`（仅主键）、`t_singleidx`（`idx_user` + 唯一 `uq_code`）、`t_composite`（`idx_status_created` + `idx_user`）。CSV 每行一条（库/表/查询/p50/p95/行数/哈希/状态），`TIMEOUT` / `ERROR` / `MISMATCH` 直接标在状态列。SQLite 索引名带表名后缀（SQLite 索引名全局唯一），其余逻辑与另两库一致。数据库文件写在 `Bin/Benchmark/QueryBenchData`（已忽略），CSV 默认写到 `Doc/性能/data`。
