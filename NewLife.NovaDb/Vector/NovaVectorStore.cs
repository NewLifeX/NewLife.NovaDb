using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NewLife.Data;
using NewLife.NovaDb.Client;
using NewLife.Serialization;
using NewLife.Threading;

namespace NewLife.NovaDb.Vector;

/// <summary>NovaDb 向量存储。基于关系型引擎持久化高维向量，实现 <see cref="IVectorStore"/></summary>
/// <remarks>
/// 通过 ADO.NET 连接（<see cref="NovaConnection"/>）操作，嵌入/网络双模通用。
/// 表结构：Id 主键 + Vector 向量列 + Payload 载荷列（LONGTEXT 存 JSON）。
/// 检索使用 NovaDb 原生 VECTOR_NEAREST 函数（余弦相似度）。
/// 由 NewLife.Core 的 <see cref="IVectorStore"/> 接口连接 NovaDb 与 NewLife.AI，双方无直接引用。
/// </remarks>
public class NovaVectorStore : IVectorStore
{
    #region 属性
    private readonly NovaConnection _connection;
    private readonly String _tableName;
    private Boolean _ensured;

    /// <summary>连接</summary>
    public NovaConnection Connection => _connection;

    /// <summary>存储表名</summary>
    public String TableName => _tableName;
    #endregion

    #region 构造
    /// <summary>创建 NovaDb 向量存储</summary>
    /// <param name="connection">已配置连接字符串的 NovaDb 连接（未打开时自动打开）</param>
    /// <param name="tableName">向量表名，默认 vector_store</param>
    public NovaVectorStore(NovaConnection connection, String tableName = "vector_store")
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));

        if (_tableName.Length == 0)
            throw new ArgumentException("表名不能为空", nameof(tableName));

        // 表名必须是合法标识符，防止 SQL 注入
        foreach (var ch in _tableName)
        {
            if (!Char.IsLetterOrDigit(ch) && ch != '_')
                throw new ArgumentException($"非法的表名 '{tableName}'，仅允许字母、数字、下划线", nameof(tableName));
        }
    }
    #endregion

    #region IVectorStore 成员
    /// <summary>新增或更新向量记录。若 Id 已存在则覆盖</summary>
    /// <param name="record">向量记录</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task UpsertAsync(VectorRecord record, CancellationToken cancellationToken = default)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (String.IsNullOrEmpty(record.Id)) throw new ArgumentException("向量记录 Id 不能为空", nameof(record));

        EnsureTable();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"Insert Into `{_tableName}` (`Id`, `Vector`, `Payload`) Values(@id, @vector, @payload) " +
            "On Duplicate Key Update `Vector`=Values(`Vector`), `Payload`=Values(`Payload`)";
        cmd.Parameters.Add(new NovaParameter { ParameterName = "@id", Value = record.Id });
        cmd.Parameters.Add(new NovaParameter { ParameterName = "@vector", Value = record.Vector });
        cmd.Parameters.Add(new NovaParameter { ParameterName = "@payload", Value = (Object?)record.Payload.ToJson() });
        cmd.ExecuteNonQuery();

        return TaskEx.CompletedTask;
    }

    /// <summary>批量新增或更新</summary>
    /// <param name="records">向量记录列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task UpsertBatchAsync(IEnumerable<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        if (records == null) throw new ArgumentNullException(nameof(records));

        EnsureTable();

        foreach (var record in records)
        {
            if (String.IsNullOrEmpty(record.Id)) continue;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"Insert Into `{_tableName}` (`Id`, `Vector`, `Payload`) Values(@id, @vector, @payload) " +
                "On Duplicate Key Update `Vector`=Values(`Vector`), `Payload`=Values(`Payload`)";
            cmd.Parameters.Add(new NovaParameter { ParameterName = "@id", Value = record.Id });
            cmd.Parameters.Add(new NovaParameter { ParameterName = "@vector", Value = record.Vector });
            cmd.Parameters.Add(new NovaParameter { ParameterName = "@payload", Value = (Object?)record.Payload.ToJson() });
            cmd.ExecuteNonQuery();
        }

        return TaskEx.CompletedTask;
    }

    /// <summary>按 Id 获取记录</summary>
    /// <param name="id">记录 Id</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>找到则返回记录，否则返回 null</returns>
    public Task<VectorRecord?> GetAsync(String id, CancellationToken cancellationToken = default)
    {
        EnsureTable();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"Select `Id`, `Vector`, `Payload` From `{_tableName}` Where `Id`=@id";
        cmd.Parameters.Add(new NovaParameter { ParameterName = "@id", Value = id });

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<VectorRecord?>(null);

        var record = new VectorRecord
        {
            Id = reader.GetString(0),
            Vector = (Single[])reader.GetValue(1)!,
        };

        if (!reader.IsDBNull(2))
        {
            var json = reader.GetString(2);
            if (!json.IsNullOrEmpty())
                record.Payload = json.ToJsonEntity<Dictionary<String, Object?>>() ?? [];
        }

        return Task.FromResult<VectorRecord?>(record);
    }

    /// <summary>Top-K 相似度检索</summary>
    /// <param name="queryVector">查询向量</param>
    /// <param name="topK">返回数量</param>
    /// <param name="minScore">最低相似度门槛（0–1）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>按相似度降序的检索结果</returns>
    public Task<IList<VectorSearchResult>> SearchAsync(Single[] queryVector, Int32 topK = 5, Double minScore = 0, CancellationToken cancellationToken = default)
    {
        if (queryVector == null) throw new ArgumentNullException(nameof(queryVector));
        if (topK <= 0) topK = 5;

        EnsureTable();

        // 使用 NovaDb 原生 VECTOR_NEAREST 做余弦相似度 Top-K 检索
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "Select VECTOR_NEAREST(@query, @table, @k, 'cosine')";
        cmd.Parameters.Add(new NovaParameter { ParameterName = "@query", Value = queryVector });
        cmd.Parameters.Add(new NovaParameter { ParameterName = "@table", Value = _tableName });
        cmd.Parameters.Add(new NovaParameter { ParameterName = "@k", Value = topK });

        var text = Convert.ToString(cmd.ExecuteScalar());
        var result = new List<VectorSearchResult>();
        if (text.IsNullOrEmpty()) return Task.FromResult<IList<VectorSearchResult>>(result);

        // VECTOR_NEAREST 返回 "id1:score1,id2:score2,..."
        var pairs = text!.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var idx = pair.LastIndexOf(':');
            if (idx <= 0) continue;

            var id = pair[..idx];
            if (!Double.TryParse(pair[(idx + 1)..], out var score)) continue;

            if (score < minScore) continue;

            var record = GetAsync(id).GetAwaiter().GetResult();
            if (record == null) continue;

            result.Add(new VectorSearchResult { Record = record, Score = score });
        }

        return Task.FromResult<IList<VectorSearchResult>>(result);
    }

    /// <summary>删除指定记录</summary>
    /// <param name="id">记录 Id</param>
    /// <param name="cancellationToken">取消令牌</param>
    public Task DeleteAsync(String id, CancellationToken cancellationToken = default)
    {
        EnsureTable();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"Delete From `{_tableName}` Where `Id`=@id";
        cmd.Parameters.Add(new NovaParameter { ParameterName = "@id", Value = id });
        cmd.ExecuteNonQuery();

        return TaskEx.CompletedTask;
    }

    /// <summary>获取存储中的记录总数</summary>
    public Task<Int64> CountAsync(CancellationToken cancellationToken = default)
    {
        EnsureTable();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"Select Count(*) From `{_tableName}`";
        var value = Convert.ToInt64(cmd.ExecuteScalar());

        return Task.FromResult(value);
    }
    #endregion

    #region 辅助
    /// <summary>确保向量表存在（首次写入时自动建表）</summary>
    private void EnsureTable()
    {
        if (_ensured) return;

        if (_connection.State != System.Data.ConnectionState.Open)
            _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"Create Table If Not Exists `{_tableName}`(`Id` VARCHAR(128) Primary Key, `Vector` VECTOR Not Null, `Payload` LONGTEXT)";
        cmd.ExecuteNonQuery();

        _ensured = true;
    }
    #endregion
}
