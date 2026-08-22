using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NewLife.Data;
using NewLife.NovaDb.Client;
using NewLife.NovaDb.Vector;
using Xunit;

namespace XUnitTest.Vector;

/// <summary>NovaVectorStore 单元测试。覆盖 Upsert/Get/Search/Delete/Count 及边界场景</summary>
public class NovaVectorStoreTests : IDisposable
{
    private readonly String _dataDir;
    private readonly NovaConnection _conn;
    private readonly NovaVectorStore _store;

    public NovaVectorStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"NovaVector_{Guid.NewGuid():N}");
        _conn = new NovaConnection { ConnectionString = $"Data Source={_dataDir}" };
        _conn.Open();

        _store = new NovaVectorStore(_conn, "vec_items");
    }

    public void Dispose()
    {
        _conn.Dispose();

        if (Directory.Exists(_dataDir))
        {
            try { Directory.Delete(_dataDir, true); }
            catch { }
        }
    }

    private static VectorRecord NewRecord(String id, Single[] vec, String? tag = null)
    {
        return new VectorRecord
        {
            Id = id,
            Vector = vec,
            Payload = tag == null ? [] : new Dictionary<String, Object?> { ["tag"] = tag }
        };
    }

    [Fact(DisplayName = "向量存储-插入并回读")]
    public async Task UpsertAndGet()
    {
        await _store.UpsertAsync(NewRecord("a", [1f, 0f, 0f], "x"));

        var got = await _store.GetAsync("a");
        Assert.NotNull(got);
        Assert.Equal("a", got!.Id);
        Assert.Equal(3, got.Vector.Length);
        Assert.Equal(1f, got.Vector[0]);
        Assert.Equal("x", got.Payload["tag"]);
    }

    [Fact(DisplayName = "向量存储-覆盖更新")]
    public async Task UpsertOverwrite()
    {
        await _store.UpsertAsync(NewRecord("a", [1f, 0f, 0f]));
        await _store.UpsertAsync(NewRecord("a", [0f, 1f, 0f], "y"));

        var got = await _store.GetAsync("a");
        Assert.NotNull(got);
        Assert.Equal(0f, got!.Vector[0]);
        Assert.Equal(1f, got.Vector[1]);
        Assert.Equal("y", got.Payload["tag"]);

        Assert.Equal(1, await _store.CountAsync());
    }

    [Fact(DisplayName = "向量存储-批量插入")]
    public async Task UpsertBatch()
    {
        await _store.UpsertBatchAsync(
        [
            NewRecord("a", [1f, 0f, 0f]),
            NewRecord("b", [0f, 1f, 0f]),
            NewRecord("c", [0.9f, 0.1f, 0f]),
        ]);

        Assert.Equal(3, await _store.CountAsync());
    }

    [Fact(DisplayName = "向量存储-Top-K 余弦相似度检索")]
    public async Task SearchTopK()
    {
        await _store.UpsertBatchAsync(
        [
            NewRecord("a", [1f, 0f, 0f]),
            NewRecord("b", [0f, 1f, 0f]),
            NewRecord("c", [0.9f, 0.1f, 0f]),
            NewRecord("d", [0f, 0f, 1f]),
        ]);

        var result = await _store.SearchAsync([1f, 0f, 0f], topK: 2);

        Assert.Equal(2, result.Count);
        // 最相似的是 a（完全匹配），其次是 c
        Assert.Equal("a", result[0].Record.Id);
        Assert.True(result[0].Score > 0.9);
        Assert.Equal("c", result[1].Record.Id);
    }

    [Fact(DisplayName = "向量存储-最低相似度门槛过滤")]
    public async Task SearchMinScore()
    {
        await _store.UpsertBatchAsync(
        [
            NewRecord("a", [1f, 0f, 0f]),
            NewRecord("b", [0f, 1f, 0f]),
        ]);

        // 高门槛只保留完全匹配
        var result = await _store.SearchAsync([1f, 0f, 0f], topK: 5, minScore: 0.99);
        Assert.Single(result);
        Assert.Equal("a", result[0].Record.Id);

        // 低门槛两个都返回
        result = await _store.SearchAsync([1f, 0f, 0f], topK: 5, minScore: 0);
        Assert.Equal(2, result.Count);
    }

    [Fact(DisplayName = "向量存储-删除与计数")]
    public async Task DeleteAndCount()
    {
        await _store.UpsertBatchAsync(
        [
            NewRecord("a", [1f, 0f, 0f]),
            NewRecord("b", [0f, 1f, 0f]),
        ]);

        await _store.DeleteAsync("a");

        Assert.Null(await _store.GetAsync("a"));
        Assert.Equal(1, await _store.CountAsync());
    }

    [Fact(DisplayName = "向量存储-空库检索返回空")]
    public async Task SearchEmpty()
    {
        var result = await _store.SearchAsync([1f, 0f, 0f], topK: 3);
        Assert.Empty(result);
    }

    [Fact(DisplayName = "向量存储-非法表名被拒绝")]
    public void InvalidTableName()
    {
        Assert.Throws<ArgumentException>(() => new NovaVectorStore(_conn, "bad;drop table"));
    }

    [Fact(DisplayName = "向量存储-空 Id 被拒绝")]
    public async Task EmptyIdRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpsertAsync(new VectorRecord { Id = "", Vector = [1f] }));
    }

    [Fact(DisplayName = "向量存储-多集合表隔离")]
    public async Task MultiTableIsolation()
    {
        var store2 = new NovaVectorStore(_conn, "vec_other");

        await _store.UpsertAsync(NewRecord("a", [1f, 0f, 0f]));
        await store2.UpsertAsync(NewRecord("a", [0f, 1f, 0f]));

        Assert.Equal(1, await _store.CountAsync());
        Assert.Equal(1, await store2.CountAsync());

        var r1 = await _store.GetAsync("a");
        var r2 = await store2.GetAsync("a");
        Assert.Equal(1f, r1!.Vector[0]);
        Assert.Equal(0f, r2!.Vector[0]);
    }
}
