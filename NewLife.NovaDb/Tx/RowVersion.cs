namespace NewLife.NovaDb.Tx;

/// <summary>行版本信息（MVCC）</summary>
public class RowVersion
{
    /// <summary>创建该版本的事务 ID</summary>
    public UInt64 CreatedByTx { get; set; }

    /// <summary>删除该版本的事务 ID（0 表示未删除）</summary>
    public UInt64 DeletedByTx { get; set; }

    /// <summary>主键值</summary>
    public Object? Key { get; set; }

    /// <summary>行数据负载（序列化后的列值）</summary>
    /// <remarks>提交并写入行日志后置空，之后按 <see cref="DataOffset"/>/<see cref="DataLength"/> 从行日志读取，避免整表载荷常驻内存</remarks>
    public Byte[]? Payload { get; set; }

    /// <summary>行数据负载在行日志文件中的起始偏移（0 表示载荷仍在内存）</summary>
    public Int64 DataOffset { get; set; }

    /// <summary>行数据负载在行日志文件中的字节长度</summary>
    public Int32 DataLength { get; set; }

    /// <summary>主键索引中的值节点偏移（0 表示尚未进入磁盘索引）</summary>
    internal Int64 IndexValueOffset { get; set; }

    /// <summary>创建新的行版本</summary>
    /// <param name="createdByTx">创建事务 ID</param>
    /// <param name="key">主键值</param>
    /// <param name="payload">数据负载</param>
    public RowVersion(UInt64 createdByTx, Object? key, Byte[]? payload)
    {
        CreatedByTx = createdByTx;
        DeletedByTx = 0;
        Key = key;
        Payload = payload;
    }

    /// <summary>检查该版本对指定事务是否可见</summary>
    /// <param name="txManager">事务管理器</param>
    /// <param name="readTxId">读取事务 ID</param>
    /// <returns>是否可见</returns>
    public Boolean IsVisible(TransactionManager txManager, UInt64 readTxId)
    {
        if (txManager == null)
            throw new ArgumentNullException(nameof(txManager));

        return txManager.IsVisible(CreatedByTx, DeletedByTx, readTxId);
    }

    /// <summary>
    /// 标记为已删除
    /// </summary>
    /// <param name="deletedByTx">删除事务 ID</param>
    public void MarkDeleted(UInt64 deletedByTx)
    {
        DeletedByTx = deletedByTx;
    }
}
