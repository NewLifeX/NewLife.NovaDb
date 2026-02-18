using System;
using System.Collections.Generic;
using NewLife.NovaDb.Cluster;
using NewLife.NovaDb.WAL;
using Xunit;

namespace XUnitTest.Cluster;

public class ReplicaClientTests : IDisposable
{
    private readonly ReplicaClient _client;
    private readonly NodeInfo _localNode;

    public ReplicaClientTests()
    {
        _localNode = new NodeInfo
        {
            NodeId = "slave-1",
            Endpoint = "127.0.0.1:9001",
            Role = NodeRole.Slave
        };
        _client = new ReplicaClient(_localNode, "127.0.0.1:9000");
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    [Fact(DisplayName = "测试创建从节点客户端")]
    public void TestCreateReplicaClient()
    {
        Assert.NotNull(_client);
        Assert.Equal("slave-1", _client.LocalNode.NodeId);
        Assert.Equal("127.0.0.1:9000", _client.MasterEndpoint);
        Assert.Equal(0UL, _client.LastAppliedLsn);
        Assert.False(_client.IsConnected);
        Assert.Equal(0, _client.AppliedRecordCount);
    }

    [Fact(DisplayName = "测试连接和断开")]
    public void TestConnectAndDisconnect()
    {
        _client.Connect();
        Assert.True(_client.IsConnected);
        Assert.Equal(NodeState.Syncing, _localNode.State);

        _client.Disconnect();
        Assert.False(_client.IsConnected);
        Assert.Equal(NodeState.Offline, _localNode.State);
    }

    [Fact(DisplayName = "测试应用 WAL 记录")]
    public void TestApplyRecords()
    {
        var records = new List<WalRecord>
        {
            new() { Lsn = 1, RecordType = WalRecordType.UpdatePage, PageId = 10, Data = new Byte[] { 1, 2, 3 } },
            new() { Lsn = 2, RecordType = WalRecordType.CommitTx, TxId = 1, Data = Array.Empty<Byte>() },
            new() { Lsn = 3, RecordType = WalRecordType.UpdatePage, PageId = 20, Data = new Byte[] { 4, 5, 6 } }
        };

        _client.ApplyRecords(records);

        Assert.Equal(3UL, _client.LastAppliedLsn);
        Assert.Equal(3, _client.AppliedRecordCount);
        Assert.Equal(3UL, _localNode.ReplicatedLsn);
    }

    [Fact(DisplayName = "测试幂等应用")]
    public void TestIdempotentApply()
    {
        var records = new List<WalRecord>
        {
            new() { Lsn = 1, RecordType = WalRecordType.UpdatePage, PageId = 10, Data = new Byte[] { 1, 2 } },
            new() { Lsn = 2, RecordType = WalRecordType.UpdatePage, PageId = 20, Data = new Byte[] { 3, 4 } }
        };

        _client.ApplyRecords(records);
        Assert.Equal(2, _client.AppliedRecordCount);

        // 重复应用相同记录，不应增加计数
        _client.ApplyRecords(records);
        Assert.Equal(2, _client.AppliedRecordCount);
        Assert.Equal(2UL, _client.LastAppliedLsn);
    }

    [Fact(DisplayName = "测试断点续传")]
    public void TestResumeFromCheckpoint()
    {
        var batch1 = new List<WalRecord>
        {
            new() { Lsn = 1, RecordType = WalRecordType.UpdatePage, PageId = 10, Data = new Byte[] { 1 } },
            new() { Lsn = 2, RecordType = WalRecordType.UpdatePage, PageId = 20, Data = new Byte[] { 2 } }
        };

        _client.ApplyRecords(batch1);
        Assert.Equal(2UL, _client.GetResumePosition());

        // 从断点续传
        var batch2 = new List<WalRecord>
        {
            new() { Lsn = 2, RecordType = WalRecordType.UpdatePage, PageId = 20, Data = new Byte[] { 2 } },
            new() { Lsn = 3, RecordType = WalRecordType.UpdatePage, PageId = 30, Data = new Byte[] { 3 } }
        };

        _client.ApplyRecords(batch2);
        Assert.Equal(3UL, _client.GetResumePosition());
        Assert.Equal(3, _client.AppliedRecordCount);
    }

    [Fact(DisplayName = "测试重置 LSN")]
    public void TestResetToLsn()
    {
        var records = new List<WalRecord>
        {
            new() { Lsn = 1, RecordType = WalRecordType.UpdatePage, PageId = 10, Data = new Byte[] { 1 } },
            new() { Lsn = 2, RecordType = WalRecordType.UpdatePage, PageId = 20, Data = new Byte[] { 2 } }
        };

        _client.ApplyRecords(records);
        Assert.Equal(2UL, _client.LastAppliedLsn);

        _client.ResetToLsn(0);
        Assert.Equal(0UL, _client.LastAppliedLsn);
        Assert.Equal(0UL, _localNode.ReplicatedLsn);
    }

    [Fact(DisplayName = "测试应用回调")]
    public void TestApplyCallback()
    {
        var appliedPages = new Dictionary<UInt64, Byte[]>();
        using var client = new ReplicaClient(
            new NodeInfo { NodeId = "slave-2", Endpoint = "127.0.0.1:9002", Role = NodeRole.Slave },
            "127.0.0.1:9000",
            (pageId, data) => appliedPages[pageId] = data
        );

        var records = new List<WalRecord>
        {
            new() { Lsn = 1, RecordType = WalRecordType.UpdatePage, PageId = 10, Data = new Byte[] { 1, 2, 3 } },
            new() { Lsn = 2, RecordType = WalRecordType.CommitTx, TxId = 1, Data = Array.Empty<Byte>() },
            new() { Lsn = 3, RecordType = WalRecordType.UpdatePage, PageId = 20, Data = new Byte[] { 4, 5, 6 } }
        };

        client.ApplyRecords(records);

        // 只有 UpdatePage 类型会触发回调
        Assert.Equal(2, appliedPages.Count);
        Assert.True(appliedPages.ContainsKey(10));
        Assert.True(appliedPages.ContainsKey(20));
        Assert.Equal(new Byte[] { 1, 2, 3 }, appliedPages[10]);
        Assert.Equal(new Byte[] { 4, 5, 6 }, appliedPages[20]);
    }

    [Fact(DisplayName = "测试端到端复制")]
    public void TestEndToEndReplication()
    {
        // 模拟主节点
        var masterInfo = new NodeInfo { NodeId = "master-1", Endpoint = "127.0.0.1:9000", Role = NodeRole.Master };
        using var manager = new ReplicationManager("/tmp/e2e.wal", masterInfo);

        // 模拟从节点
        var slaveNode = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        var appliedPages = new Dictionary<UInt64, Byte[]>();
        using var replica = new ReplicaClient(
            slaveNode,
            "127.0.0.1:9000",
            (pageId, data) => appliedPages[pageId] = data
        );

        // 注册从节点
        manager.RegisterSlave(new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave });

        // 主节点写入
        manager.AppendRecord(new WalRecord { RecordType = WalRecordType.BeginTx, TxId = 1, Data = Array.Empty<Byte>() });
        manager.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 100, Data = new Byte[] { 10, 20, 30 } });
        manager.AppendRecord(new WalRecord { RecordType = WalRecordType.CommitTx, TxId = 1, Data = Array.Empty<Byte>() });

        Assert.Equal(3UL, manager.MasterLsn);
        Assert.Equal(3UL, manager.GetReplicationLag("slave-1"));

        // 从节点拉取并应用
        var pending = manager.GetPendingRecords("slave-1");
        Assert.Equal(3, pending.Count);

        replica.Connect();
        replica.ApplyRecords(pending);

        Assert.Equal(3UL, replica.LastAppliedLsn);
        Assert.True(appliedPages.ContainsKey(100));
        Assert.Equal(new Byte[] { 10, 20, 30 }, appliedPages[100]);

        // 确认复制
        manager.AcknowledgeReplication("slave-1", replica.LastAppliedLsn);

        Assert.Equal(0UL, manager.GetReplicationLag("slave-1"));
        Assert.True(manager.IsFullySynced(3));

        // 清理缓冲区
        var cleaned = manager.CleanupBuffer();
        Assert.Equal(3, cleaned);
    }
}
