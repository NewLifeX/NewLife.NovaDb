using System;
using Xunit;
using NewLife.NovaDb.Tx;

namespace XUnitTest.Tx;

/// <summary>
/// 行版本单元测试
/// </summary>
public class RowVersionTests
{
    [Fact(DisplayName = "测试创建行版本")]
    public void TestCreateRowVersion()
    {
        var key = 123;
        var payload = new Byte[] { 1, 2, 3, 4 };
        var row = new RowVersion(1UL, key, payload);

        Assert.Equal(1UL, row.CreatedByTx);
        Assert.Equal(0UL, row.DeletedByTx);
        Assert.Equal(key, row.Key);
        Assert.Equal(payload, row.Payload);
    }

    [Fact(DisplayName = "测试标记删除")]
    public void TestMarkDeleted()
    {
        var row = new RowVersion(1UL, 123, [1, 2, 3]);

        Assert.Equal(0UL, row.DeletedByTx);

        row.MarkDeleted(5UL);

        Assert.Equal(5UL, row.DeletedByTx);
    }

    [Fact(DisplayName = "测试可见性检查")]
    public void TestIsVisible()
    {
        var manager = new TransactionManager();

        // 创建事务并提交
        var tx1 = manager.BeginTransaction();
        var row = new RowVersion(tx1.TxId, 123, [1, 2, 3]);
        tx1.Commit();

        // 新事务应该能看到已提交的行
        var tx2 = manager.BeginTransaction();
        Assert.True(row.IsVisible(manager, tx2.TxId));

        // 标记删除并提交
        var tx3 = manager.BeginTransaction();
        row.MarkDeleted(tx3.TxId);
        tx3.Commit();

        // 新事务不应该看到已删除的行
        var tx4 = manager.BeginTransaction();
        Assert.False(row.IsVisible(manager, tx4.TxId));
    }

    [Fact(DisplayName = "测试事务自己修改的可见性")]
    public void TestSelfVisibility()
    {
        var manager = new TransactionManager();
        var tx = manager.BeginTransaction();

        // 事务创建的行对自己可见
        var row = new RowVersion(tx.TxId, 123, [1, 2, 3]);
        Assert.True(row.IsVisible(manager, tx.TxId));

        // 事务删除的行对自己不可见
        row.MarkDeleted(tx.TxId);
        Assert.False(row.IsVisible(manager, tx.TxId));
    }

    [Fact(DisplayName = "测试未提交事务的可见性")]
    public void TestUncommittedVisibility()
    {
        var manager = new TransactionManager();

        // 事务 1 创建行但不提交
        var tx1 = manager.BeginTransaction();
        var row = new RowVersion(tx1.TxId, 123, [1, 2, 3]);

        // 事务 2 不应该看到事务 1 未提交的修改
        var tx2 = manager.BeginTransaction();
        Assert.False(row.IsVisible(manager, tx2.TxId));

        // 事务 1 提交后，事务 2 应该能看到
        tx1.Commit();
        Assert.True(row.IsVisible(manager, tx2.TxId));
    }
}
