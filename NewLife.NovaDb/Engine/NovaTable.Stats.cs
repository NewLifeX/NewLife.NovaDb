using NewLife.NovaDb.Tx;

namespace NewLife.NovaDb.Engine;

partial class NovaTable
{
    #region 统计

    /// <summary>统计采样行数上限</summary>
    private const Int32 StatisticsSampleRows = 1000;

    /// <summary>统计扫描分块大小，避免一次性取出全部索引条目</summary>
    private const Int32 StatisticsChunkSize = 1024;

    /// <summary>构建表统计信息（行数、列 NDV、等深直方图、索引键数）供查询优化器使用</summary>
    /// <remarks>
    /// 只读、幂等：不修改表数据与索引，不产生持久化副作用。
    /// 索引列直接遍历内存索引键（分块 ≤1024），NDV 与直方图均为精确值；非索引列水库采样 1000 行后按比例放大。
    /// </remarks>
    /// <returns>表统计信息</returns>
    public TableStatistics BuildStatistics() => BuildStatistics(null);

    /// <summary>构建表统计信息（可复用调用方事务做可见性判断）</summary>
    /// <param name="tx">事务。为空时内部创建只读事务</param>
    /// <returns>表统计信息</returns>
    public TableStatistics BuildStatistics(Transaction? tx)
    {
        var ownsTx = false;
        if (tx == null)
        {
            tx = _txManager.BeginTransaction();
            ownsTx = true;
        }

        try
        {
            var stats = new TableStatistics
            {
                TableName = _schema.TableName,
                EntryCount = _primaryIndex.Count,
                DataVersion = DataVersion,
                SchemaSignature = TableStatistics.BuildSchemaSignature(_schema),
                AnalyzedAt = DateTime.Now,
                StatVersion = 1,
                Owner = this,
            };

            // 第一遍：统计可见行并水库采样，样本只保留载荷引用，扫描结束后再反序列化
            var samples = new List<Byte[]>();
            var random = new Random(20260101);
            var seenRows = 0L;
            var visibleRows = ScanPrimaryIndex(tx, (key, payload) =>
            {
                if (samples.Count < StatisticsSampleRows)
                    samples.Add(payload);
                else
                {
                    // 水库采样：已有 seenRows 行时，以 1/(seenRows+1) 的概率替换样本
                    var slot = (Int32)(random.NextDouble() * (seenRows + 1));
                    if (slot < StatisticsSampleRows) samples[slot] = payload;
                }

                seenRows++;
            });

            stats.RowCount = visibleRows;
            stats.SampledRows = samples.Count;
            stats.Indexes["PRIMARY"] = new IndexStatistics { IndexName = "PRIMARY", IsUnique = true, KeyCount = visibleRows };

            if (visibleRows > 0)
            {
                // 主键列：键有序，NDV 用相邻键比较精确计数
                var pkCol = _schema.GetPrimaryKeyColumn()!;
                var builder = new HistogramBuilder(visibleRows, TableStatistics.DefaultBucketCount);
                var distinct = 0L;
                var minValue = (Object?)null;
                ComparableObject? previousKey = null;

                ScanPrimaryIndex(tx, (key, payload) =>
                {
                    if (previousKey == null || previousKey.CompareTo(key) != 0) distinct++;
                    previousKey = key;

                    if (minValue == null) minValue = key.Value;
                    builder.Add(key.Value);
                });

                var pkStats = new ColumnStatistics
                {
                    ColumnName = pkCol.Name,
                    Nullable = pkCol.Nullable,
                    DistinctCount = distinct,
                    MinValue = minValue,
                    MaxValue = previousKey?.Value,
                    Buckets = builder.Buckets,
                    IsSampled = false,
                };
                stats.Columns[pkCol.Name] = pkStats;

                // 二级索引：键数量即 NDV；单列索引同时产出该列的等深直方图
                foreach (var indexDef in _schema.Indexes)
                {
                    if (!_secondaryIndexes.TryGetValue(indexDef.IndexName, out var index)) continue;

                    var keyCount = index.Count;
                    stats.Indexes[indexDef.IndexName] = new IndexStatistics
                    {
                        IndexName = indexDef.IndexName,
                        IsUnique = indexDef.IsUnique,
                        KeyCount = keyCount,
                    };

                    if (indexDef.Columns.Count != 1 || keyCount <= 0) continue;

                    var column = _schema.GetColumn(indexDef.Columns[0]);
                    if (stats.Columns.ContainsKey(column.Name)) continue;

                    stats.Columns[column.Name] = BuildIndexColumnStatistics(column, index, keyCount);
                }

                // 非索引列：按采样值估算 NDV 与直方图
                var rows = new List<Object?[]>(samples.Count);
                foreach (var payload in samples)
                    rows.Add(DeserializeRow(payload));

                foreach (var column in _schema.Columns)
                {
                    if (stats.Columns.ContainsKey(column.Name)) continue;

                    var columnStats = BuildSampledColumnStatistics(column, rows, visibleRows);
                    if (columnStats != null) stats.Columns[column.Name] = columnStats;
                }
            }

            return stats;
        }
        finally
        {
            if (ownsTx) tx.Dispose();
        }
    }

    /// <summary>扫描主键索引并逐个回调可见行，分块持锁避免长时间阻塞写入</summary>
    /// <param name="tx">事务</param>
    /// <param name="onVisible">可见行回调（主键键、行载荷）</param>
    /// <returns>可见行数</returns>
    private Int64 ScanPrimaryIndex(Transaction tx, Action<ComparableObject, Byte[]>? onVisible)
    {
        var count = 0L;
        ComparableObject? from = null;
        var includeFrom = true;

        while (true)
        {
            List<KeyValuePair<ComparableObject, List<RowVersion>>> chunk;
            lock (_lock)
            {
                chunk = _primaryIndex.GetRange(from, includeFrom, null, true, StatisticsChunkSize);
                if (chunk.Count == 0) break;

                foreach (var entry in chunk)
                {
                    var payload = FindVisibleVersion(entry.Value, tx.TxId)?.Payload;
                    if (payload == null) continue;

                    count++;
                    onVisible?.Invoke(entry.Key, payload);
                }

                from = chunk[chunk.Count - 1].Key;
                includeFrom = false;
            }

            if (chunk.Count < StatisticsChunkSize) break;
        }

        return count;
    }

    /// <summary>按索引键流式构建单列索引的列统计</summary>
    /// <param name="column">列定义</param>
    /// <param name="index">二级索引</param>
    /// <param name="keyCount">索引键数量</param>
    /// <returns>列统计</returns>
    private static ColumnStatistics BuildIndexColumnStatistics(ColumnDefinition column, SkipList<ComparableObject, List<ComparableObject>> index, Int32 keyCount)
    {
        var builder = new HistogramBuilder(keyCount, TableStatistics.DefaultBucketCount);
        var distinct = 0L;
        var minValue = (Object?)null;
        ComparableObject? previousKey = null;

        ScanIndexKeys(index, key =>
        {
            if (previousKey == null || previousKey.CompareTo(key) != 0) distinct++;
            previousKey = key;

            if (minValue == null) minValue = key.Value;
            builder.Add(key.Value);
        });

        return new ColumnStatistics
        {
            ColumnName = column.Name,
            Nullable = column.Nullable,
            DistinctCount = distinct,
            MinValue = minValue,
            MaxValue = previousKey?.Value,
            Buckets = builder.Buckets,
            IsSampled = false,
        };
    }

    /// <summary>按索引键分块遍历，每块不超过 <see cref="StatisticsChunkSize"/> 条</summary>
    private static void ScanIndexKeys(SkipList<ComparableObject, List<ComparableObject>> index, Action<ComparableObject> onKey)
    {
        ComparableObject? from = null;
        var includeFrom = true;

        while (true)
        {
            var chunk = index.GetRange(from, includeFrom, null, true, StatisticsChunkSize);
            if (chunk.Count == 0) break;

            foreach (var entry in chunk)
                onKey(entry.Key);

            from = chunk[chunk.Count - 1].Key;
            includeFrom = false;
            if (chunk.Count < StatisticsChunkSize) break;
        }
    }

    /// <summary>根据采样行估算列级统计：NDV 按采样比例放大，直方图桶数按行数等比放大</summary>
    private static ColumnStatistics? BuildSampledColumnStatistics(ColumnDefinition column, List<Object?[]> samples, Int64 rowCount)
    {
        var values = new List<Object>(samples.Count);
        foreach (var row in samples)
        {
            var value = column.Ordinal < row.Length ? row[column.Ordinal] : null;
            if (value != null) values.Add(value);
        }

        // 全部为 NULL 时无法估算
        if (values.Count == 0) return null;

        values.Sort((x, y) => new ComparableObject(x).CompareTo(new ComparableObject(y)));

        var distinct = 1L;
        for (var i = 1; i < values.Count; i++)
        {
            if (new ComparableObject(values[i]).CompareTo(new ComparableObject(values[i - 1])) != 0) distinct++;
        }

        var exact = samples.Count >= rowCount;
        var ndv = distinct;
        if (!exact)
        {
            ndv = (Int64)Math.Round((Double)distinct * rowCount / samples.Count);
            if (ndv < distinct) ndv = distinct;
            if (ndv > rowCount) ndv = rowCount;
        }

        var builder = new HistogramBuilder(values.Count, TableStatistics.DefaultBucketCount);
        foreach (var value in values)
            builder.Add(value);

        if (!exact)
        {
            var scale = (Double)rowCount / values.Count;
            foreach (var bucket in builder.Buckets)
            {
                var count = (Int64)Math.Round(bucket.Count * scale);
                bucket.Count = count < 1 ? 1 : count;
            }
        }

        return new ColumnStatistics
        {
            ColumnName = column.Name,
            Nullable = column.Nullable,
            DistinctCount = ndv,
            MinValue = values[0],
            MaxValue = values[values.Count - 1],
            Buckets = builder.Buckets,
            IsSampled = !exact,
        };
    }

    /// <summary>查找对指定事务可见的首个行版本</summary>
    private RowVersion? FindVisibleVersion(List<RowVersion>? versions, UInt64 txId)
    {
        if (versions == null) return null;

        foreach (var version in versions)
        {
            if (version.IsVisible(_txManager, txId)) return version;
        }

        return null;
    }

    /// <summary>等深直方图流式构建器：按键升序输入，按位次切分桶</summary>
    private sealed class HistogramBuilder
    {
        private readonly Int64 _total;
        private readonly Int32 _bucketCount;
        private readonly List<HistogramBucket> _buckets = [];
        private Int64 _position;
        private Int64 _bucketStart;
        private Int32 _nextBucket = 1;

        public HistogramBuilder(Int64 total, Int32 bucketCount)
        {
            _total = total;
            _bucketCount = total <= 0 ? 0 : (Int32)Math.Min((Int64)Math.Max(bucketCount, 1), total);
        }

        public List<HistogramBucket> Buckets => _buckets;

        public void Add(Object? value)
        {
            if (_bucketCount == 0) return;

            _position++;
            while (_nextBucket <= _bucketCount && _position >= _total * _nextBucket / _bucketCount)
            {
                var boundary = _total * _nextBucket / _bucketCount;
                _buckets.Add(new HistogramBucket(value, boundary - _bucketStart));
                _bucketStart = boundary;
                _nextBucket++;
            }
        }
    }

    #endregion
}
