using System;
using System.Collections.Generic;
using NewLife.NovaDb.Cluster;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.WAL;
using Xunit;

namespace XUnitTest.Cluster;

public class FailoverManagerTests : IDisposable
{
    private readonly ReplicationManager _replication;
    private readonly FailoverManager _failover;
    private readonly NodeInfo _masterInfo;

    public FailoverManagerTests()
    {
        _masterInfo = new NodeInfo
        {
            NodeId = "master-1",
            Endpoint = "127.0.0.1:9000",
            Role = NodeRole.Master
        };
        _replication = new ReplicationManager("/tmp/failover-test.wal", _masterInfo);
        _failover = new FailoverManager(_replication);
    }

    public void Dispose()
    {
        _replication.Dispose();
    }

    [Fact(DisplayName = "提升从节点为主节点")]
    public void PromoteSlaveToMaster()
    {
        // 注册从节点
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _replication.RegisterSlave(slave);

        // 追加一些记录并让从节点同步
        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 1, Data = [1] });
        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 2, Data = [2] });
        _replication.AcknowledgeReplication("slave-1", 2);

        // 执行故障切换
        var result = _failover.Promote("slave-1");

        Assert.True(result.Success);
        Assert.Equal("slave-1", result.NewMaster.NodeId);
        Assert.Equal(NodeRole.Master, result.NewMaster.Role);
        Assert.Equal(NodeState.Online, result.NewMaster.State);
        Assert.Equal(NodeState.Offline, result.OldMaster.State);
        Assert.Equal(0UL, result.DataLossLsn);
    }

    [Fact(DisplayName = "延迟过大时拒绝切换")]
    public void RejectPromoteWhenLagTooHigh()
    {
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _replication.RegisterSlave(slave);

        // 写入大量记录但从节点不同步
        for (var i = 0; i < 200; i++)
        {
            _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = (UInt64)i, Data = [1] });
        }

        _failover.MaxAllowedLag = 100;

        var ex = Assert.Throws<NovaException>(() => _failover.Promote("slave-1"));
        Assert.Equal(ErrorCode.ReplicationLag, ex.Code);
    }

    [Fact(DisplayName = "强制提升忽略延迟")]
    public void ForcePromoteIgnoresLag()
    {
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _replication.RegisterSlave(slave);

        for (var i = 0; i < 200; i++)
        {
            _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = (UInt64)i, Data = [1] });
        }

        var result = _failover.ForcePromote("slave-1");

        Assert.True(result.Success);
        Assert.Equal(200UL, result.DataLossLsn);
    }

    [Fact(DisplayName = "自动选择最佳从节点")]
    public void PromoteBestSelectsHighestLsn()
    {
        var slave1 = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        var slave2 = new NodeInfo { NodeId = "slave-2", Endpoint = "127.0.0.1:9002", Role = NodeRole.Slave };
        _replication.RegisterSlave(slave1);
        _replication.RegisterSlave(slave2);

        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 1, Data = [1] });
        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 2, Data = [2] });
        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 3, Data = [3] });

        // slave-2 同步更多
        _replication.AcknowledgeReplication("slave-1", 1);
        _replication.AcknowledgeReplication("slave-2", 3);

        var result = _failover.PromoteBest();

        Assert.True(result.Success);
        Assert.Equal("slave-2", result.NewMaster.NodeId);
        Assert.Equal(0UL, result.DataLossLsn);
    }

    [Fact(DisplayName = "无可用从节点时抛异常")]
    public void PromoteBestThrowsWhenNoSlaves()
    {
        var ex = Assert.Throws<NovaException>(() => _failover.PromoteBest());
        Assert.Equal(ErrorCode.ReplicationError, ex.Code);
    }

    [Fact(DisplayName = "提升不存在的节点抛异常")]
    public void PromoteNonexistentNodeThrows()
    {
        var ex = Assert.Throws<NovaException>(() => _failover.Promote("nonexistent"));
        Assert.Equal(ErrorCode.NodeNotFound, ex.Code);
    }

    [Fact(DisplayName = "提升离线节点抛异常")]
    public void PromoteOfflineNodeThrows()
    {
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _replication.RegisterSlave(slave);
        slave.State = NodeState.Offline;

        var ex = Assert.Throws<NovaException>(() => _failover.Promote("slave-1"));
        Assert.Equal(ErrorCode.ReplicationError, ex.Code);
    }

    [Fact(DisplayName = "故障切换历史记录")]
    public void FailoverHistoryIsRecorded()
    {
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _replication.RegisterSlave(slave);
        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 1, Data = [1] });
        _replication.AcknowledgeReplication("slave-1", 1);

        _failover.Promote("slave-1");

        Assert.Single(_failover.History);
        Assert.Equal("master-1", _failover.History[0].OldMasterId);
        Assert.Equal("slave-1", _failover.History[0].NewMasterId);
    }

    [Fact(DisplayName = "提升后剩余从节点标记为同步中")]
    public void RemainingSlavesSyncingAfterPromote()
    {
        var slave1 = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        var slave2 = new NodeInfo { NodeId = "slave-2", Endpoint = "127.0.0.1:9002", Role = NodeRole.Slave };
        _replication.RegisterSlave(slave1);
        _replication.RegisterSlave(slave2);

        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 1, Data = [1] });
        _replication.AcknowledgeReplication("slave-1", 1);
        _replication.AcknowledgeReplication("slave-2", 1);

        var result = _failover.Promote("slave-1");

        Assert.Equal(1, result.RemainingSlaves);
        // slave-2 应标记为 Syncing
        var remaining = _replication.GetAllSlaves();
        Assert.Single(remaining);
        Assert.Equal(NodeState.Syncing, remaining[0].State);
    }

    [Fact(DisplayName = "部分同步时有数据丢失")]
    public void DataLossWhenPartialSync()
    {
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _replication.RegisterSlave(slave);

        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 1, Data = [1] });
        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 2, Data = [2] });
        _replication.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 3, Data = [3] });

        // 从节点只同步了 1 条
        _replication.AcknowledgeReplication("slave-1", 1);
        _failover.MaxAllowedLag = 10; // 允许一定延迟

        var result = _failover.ForcePromote("slave-1");

        Assert.True(result.Success);
        Assert.Equal(2UL, result.DataLossLsn); // 丢失 2 条
    }
}
