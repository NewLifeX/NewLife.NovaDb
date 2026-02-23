using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NewLife.NovaDb.Tx;

namespace XUnitTest.Tx;

/// <summary>
/// 事务管理器单元测试
/// </summary>
public class TransactionManagerTests
{
    [Fact(DisplayName = "测试开始事务")]
    public void TestBeginTransaction()
    {
        var manager = new TransactionManager();

        var tx1 = manager.BeginTransaction();
        Assert.NotNull(tx1);
        Assert.Equal(1UL, tx1.TxId);
        Assert.Equal(TransactionState.Active, tx1.State);

        var tx2 = manager.BeginTransaction();
        Assert.NotNull(tx2);
        Assert.Equal(2UL, tx2.TxId);
        Assert.Equal(TransactionState.Active, tx2.State);

        // 验证事务 ID 递增
        Assert.True(tx2.TxId > tx1.TxId);
    }

    [Fact(DisplayName = "测试提交事务")]
    public void TestCommitTransaction()
    {
        var manager = new TransactionManager();
        var tx = manager.BeginTransaction();

        // 初始状态
        Assert.Equal(TransactionState.Active, tx.State);
        Assert.True(manager.IsTransactionActive(tx.TxId));

        // 提交事务
        tx.Commit();

        // 验证状态
        Assert.Equal(TransactionState.Committed, tx.State);
        Assert.True(tx.CommitTs > 0);
        Assert.False(manager.IsTransactionActive(tx.TxId));
    }

    [Fact(DisplayName = "测试回滚事务")]
    public void TestRollbackTransaction()
    {
        var manager = new TransactionManager();
        var tx = manager.BeginTransaction();

        var rollbackExecuted = false;
        tx.RegisterRollbackAction(() => { rollbackExecuted = true; });

        // 回滚事务
        tx.Rollback();

        // 验证状态
        Assert.Equal(TransactionState.Aborted, tx.State);
        Assert.True(rollbackExecuted);
        Assert.False(manager.IsTransactionActive(tx.TxId));
    }

    [Fact(DisplayName = "测试事务自动回滚")]
    public void TestTransactionAutoRollback()
    {
        var manager = new TransactionManager();
        var rollbackExecuted = false;

        using (var tx = manager.BeginTransaction())
        {
            tx.RegisterRollbackAction(() => { rollbackExecuted = true; });
            // 不调用 Commit 或 Rollback，依赖 Dispose
        }

        // 验证自动回滚
        Assert.True(rollbackExecuted);
    }

    [Fact(DisplayName = "测试获取活跃事务列表")]
    public void TestGetActiveTransactions()
    {
        var manager = new TransactionManager();

        var tx1 = manager.BeginTransaction();
        var tx2 = manager.BeginTransaction();
        var tx3 = manager.BeginTransaction();

        // 获取活跃事务
        var active = manager.GetActiveTransactions();
        Assert.Equal(3, active.Length);
        Assert.Contains(tx1.TxId, active);
        Assert.Contains(tx2.TxId, active);
        Assert.Contains(tx3.TxId, active);

        // 提交一个事务
        tx2.Commit();
        active = manager.GetActiveTransactions();
        Assert.Equal(2, active.Length);
        Assert.DoesNotContain(tx2.TxId, active);
    }

    [Fact(DisplayName = "测试 Read Committed 可见性规则")]
    public void TestReadCommittedVisibility()
    {
        var manager = new TransactionManager();

        // 场景 1：已提交事务的修改对后续事务可见
        var tx1 = manager.BeginTransaction();
        var createdByTx = tx1.TxId;
        tx1.Commit();

        var tx2 = manager.BeginTransaction();
        var visible = manager.IsVisible(createdByTx, 0, tx2.TxId);
        Assert.True(visible);

        // 场景 2：活跃事务的修改对其他事务不可见
        var tx3 = manager.BeginTransaction();
        var tx4 = manager.BeginTransaction();
        visible = manager.IsVisible(tx3.TxId, 0, tx4.TxId);
        Assert.False(visible);

        // 场景 3：事务自己的修改对自己可见
        visible = manager.IsVisible(tx3.TxId, 0, tx3.TxId);
        Assert.True(visible);

        // 场景 4：已删除的行不可见
        tx3.Commit();
        var tx5 = manager.BeginTransaction();
        var deleteTxId = tx5.TxId;
        tx5.Commit();

        var tx6 = manager.BeginTransaction();
        visible = manager.IsVisible(tx3.TxId, deleteTxId, tx6.TxId);
        Assert.False(visible);
    }

    [Fact(DisplayName = "测试并发事务提交")]
    public async Task TestConcurrentCommit()
    {
        var manager = new TransactionManager();
        var commitTsList = new List<UInt64>();
        var lockObj = new Object();

        // 创建多个事务并发提交
        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var tx = manager.BeginTransaction();
                Thread.Sleep(10); // 模拟一些工作
                tx.Commit();

                lock (lockObj)
                {
                    commitTsList.Add(tx.CommitTs);
                }
            }));
        }

        await Task.WhenAll(tasks.ToArray());

        // 验证提交时间戳唯一且递增
        Assert.Equal(10, commitTsList.Count);
        var sortedList = commitTsList.OrderBy(x => x).ToList();
        for (var i = 0; i < sortedList.Count; i++)
        {
            Assert.Equal(sortedList[i], commitTsList.Contains(sortedList[i]) ? sortedList[i] : 0UL);
            if (i > 0)
                Assert.True(sortedList[i] > sortedList[i - 1]);
        }
    }

    [Fact(DisplayName = "测试回滚动作执行顺序")]
    public void TestRollbackActionOrder()
    {
        var manager = new TransactionManager();
        var tx = manager.BeginTransaction();

        var executionOrder = new List<Int32>();
        tx.RegisterRollbackAction(() => executionOrder.Add(1));
        tx.RegisterRollbackAction(() => executionOrder.Add(2));
        tx.RegisterRollbackAction(() => executionOrder.Add(3));

        tx.Rollback();

        // 验证回滚动作倒序执行
        Assert.Equal(new[] { 3, 2, 1 }, executionOrder);
    }
}
