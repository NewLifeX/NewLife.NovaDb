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

/// <summary>NovaVectorStore 单元测试。覆盖存储级（集合管理）与集合级（Upsert/Get/Search/Delete/Count）</summary>
public class NovaVectorStoreTests : IDisposable
{
    private readonly String _dataDir;
    private readonly NovaConnection _conn;
    private readonly NovaVectorStore _store;
    private readonly IVectorStoreCollection _col;

    public NovaVectorStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"NovaVector_{Guid.NewGuid():N}");
        _conn = new NovaConnection { ConnectionString = $"Data Source={_dataDir}" };
        _conn.Open();

        _store = new NovaVectorStore(_conn);
        _col = _store.GetCollection("vec_items");
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
        await _col.UpsertAsync(NewRecord("a", [1f, 0f, 0f], "x"));

        var got = await _col.GetAsync("a");
        Assert.NotNull(got);
        Assert.Equal("a", got!.Id);
        Assert.Equal(3, got.Vector.Length);
        Assert.Equal(1f, got.Vector[0]);
        Assert.Equal("x", got.Payload["tag"]);
    }

    [Fact(DisplayName = "向量存储-覆盖更新")]
    public async Task UpsertOverwrite()
    {
        await _col.UpsertAsync(NewRecord("a", [1f, 0f, 0f]));
        await _col.UpsertAsync(NewRecord("a", [0f, 1f, 0f], "y"));

        var got = await _col.GetAsync("a");
        Assert.NotNull(got);
        Assert.Equal(0f, got!.Vector[0]);
        Assert.Equal(1f, got.Vector[1]);
        Assert.Equal("y", got.Payload["tag"]);

        Assert.Equal(1, await _col.CountAsync());
    }

    [Fact(DisplayName = "向量存储-批量插入")]
    public async Task UpsertBatch()
    {
        await _col.UpsertAsync(
        [
            NewRecord("a", [1f, 0f, 0f]),
            NewRecord("b", [0f, 1f, 0f]),
            NewRecord("c", [0.9f, 0.1f, 0f]),
        ]);

        Assert.Equal(3, await _col.CountAsync());
    }

    [Fact(DisplayName = "向量存储-Top-K 余弦相似度检索")]
    public async Task SearchTopK()
    {
        await _col.UpsertAsync(
        [
            NewRecord("a", [1f, 0f, 0f]),
            NewRecord("b", [0f, 1f, 0f]),
            NewRecord("c", [0.9f, 0.1f, 0f]),
            NewRecord("d", [0f, 0f, 1f]),
        ]);

        var result = await _col.SearchAsync([1f, 0f, 0f], top: 2);

        Assert.Equal(2, result.Count);
        // 最相似的是 a（完全匹配），其次是 c
        Assert.Equal("a", result[0].Record.Id);
        Assert.True(result[0].Score > 0.9);
        Assert.Equal("c", result[1].Record.Id);
    }

    [Fact(DisplayName = "向量存储-最低相似度门槛过滤")]
    public async Task SearchMinScore()
    {
        await _col.UpsertAsync(
        [
            NewRecord("a", [1f, 0f, 0f]),
            NewRecord("b", [0f, 1f, 0f]),
        ]);

        // 高门槛只保留完全匹配
        var result = await _col.SearchAsync([1f, 0f, 0f], top: 5, minScore: 0.99);
        Assert.Single(result);
        Assert.Equal("a", result[0].Record.Id);

        // 低门槛两个都返回
        result = await _col.SearchAsync([1f, 0f, 0f], top: 5, minScore: 0);
        Assert.Equal(2, result.Count);
    }

    [Fact(DisplayName = "向量存储-删除与计数")]
    public async Task DeleteAndCount()
    {
        await _col.UpsertAsync(
        [
            NewRecord("a", [1f, 0f, 0f]),
            NewRecord("b", [0f, 1f, 0f]),
        ]);

        await _col.DeleteAsync("a");

        Assert.Null(await _col.GetAsync("a"));
        Assert.Equal(1, await _col.CountAsync());
    }

    [Fact(DisplayName = "向量存储-空库检索返回空")]
    public async Task SearchEmpty()
    {
        var result = await _col.SearchAsync([1f, 0f, 0f], top: 3);
        Assert.Empty(result);
    }

    [Fact(DisplayName = "向量存储-非法表名被拒绝")]
    public void InvalidTableName()
    {
        Assert.Throws<ArgumentException>(() => _store.GetCollection("bad;drop table"));
    }

    [Fact(DisplayName = "向量存储-空 Id 被拒绝")]
    public async Task EmptyIdRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _col.UpsertAsync(new VectorRecord { Id = "", Vector = [1f] }));
    }

    [Fact(DisplayName = "向量存储-多集合表隔离")]
    public async Task MultiTableIsolation()
    {
        var col2 = _store.GetCollection("vec_other");

        await _col.UpsertAsync(NewRecord("a", [1f, 0f, 0f]));
        await col2.UpsertAsync(NewRecord("a", [0f, 1f, 0f]));

        Assert.Equal(1, await _col.CountAsync());
        Assert.Equal(1, await col2.CountAsync());

        var r1 = await _col.GetAsync("a");
        var r2 = await col2.GetAsync("a");
        Assert.Equal(1f, r1!.Vector[0]);
        Assert.Equal(0f, r2!.Vector[0]);
    }

    [Fact(DisplayName = "向量存储-集合生命周期：列出与删除")]
    public async Task CollectionLifecycle()
    {
        var col2 = _store.GetCollection("vec_other");

        var names = await _store.ListCollectionNamesAsync();
        Assert.Contains("vec_items", names);
        Assert.Contains("vec_other", names);

        await _store.RemoveCollectionAsync("vec_other");

        Assert.DoesNotContain("vec_other", await _store.ListCollectionNamesAsync());
        // 重新获取得到新集合（空表）
        var again = _store.GetCollection("vec_other");
        Assert.Equal(0, await again.CountAsync());
    }
}
