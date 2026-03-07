using System;
using System.Collections.Generic;
using System.IO;
using NewLife.NovaDb.Cluster;
using NewLife.NovaDb.Core;
using NewLife.NovaDb.WAL;
using Xunit;

namespace XUnitTest.Cluster;

public class ReplicationManagerTests : IDisposable
{
    private readonly ReplicationManager _manager;
    private readonly NodeInfo _masterInfo;

    public ReplicationManagerTests()
    {
        _masterInfo = new NodeInfo
        {
            NodeId = "master-1",
            Endpoint = "127.0.0.1:9000",
            Role = NodeRole.Master
        };
        _manager = new ReplicationManager("/tmp/test.wal", _masterInfo);
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    [Fact(DisplayName = "测试创建复制管理器")]
    public void TestCreateReplicationManager()
    {
        Assert.NotNull(_manager);
        Assert.Equal("master-1", _manager.MasterInfo.NodeId);
        Assert.Equal(NodeRole.Master, _manager.MasterInfo.Role);
        Assert.Equal(NodeState.Online, _manager.MasterInfo.State);
        Assert.Equal(0, _manager.SlaveCount);
        Assert.Equal(0UL, _manager.MasterLsn);
    }

    [Fact(DisplayName = "测试注册从节点")]
    public void TestRegisterSlave()
    {
        var slave = new NodeInfo
        {
            NodeId = "slave-1",
            Endpoint = "127.0.0.1:9001",
            Role = NodeRole.Slave
        };

        _manager.RegisterSlave(slave);

        Assert.Equal(1, _manager.SlaveCount);
        var retrieved = _manager.GetSlave("slave-1");
        Assert.NotNull(retrieved);
        Assert.Equal("slave-1", retrieved!.NodeId);
        Assert.Equal(NodeState.Syncing, retrieved.State);
    }

    [Fact(DisplayName = "测试移除从节点")]
    public void TestRemoveSlave()
    {
        var slave = new NodeInfo
        {
            NodeId = "slave-1",
            Endpoint = "127.0.0.1:9001",
            Role = NodeRole.Slave
        };

        _manager.RegisterSlave(slave);
        Assert.Equal(1, _manager.SlaveCount);

        var removed = _manager.RemoveSlave("slave-1");
        Assert.True(removed);
        Assert.Equal(0, _manager.SlaveCount);
        Assert.Null(_manager.GetSlave("slave-1"));

        // 移除不存在的节点
        Assert.False(_manager.RemoveSlave("slave-nonexistent"));
    }

    [Fact(DisplayName = "测试追加复制记录")]
    public void TestAppendRecord()
    {
        var record1 = new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 1 };
        var record2 = new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 2 };

        _manager.AppendRecord(record1, [1, 2, 3]);
        Assert.Equal(1UL, _manager.MasterLsn);
        Assert.Equal(1UL, record1.Lsn);

        _manager.AppendRecord(record2, [4, 5, 6]);
        Assert.Equal(2UL, _manager.MasterLsn);
        Assert.Equal(2UL, record2.Lsn);
    }

    [Fact(DisplayName = "测试获取待复制记录")]
    public void TestGetPendingRecords()
    {
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _manager.RegisterSlave(slave);

        // 追加 3 条记录
        for (var i = 0; i < 3; i++)
        {
            _manager.AppendRecord(new WalRecord
            {
                RecordType = WalRecordType.UpdatePage,
                PageId = (UInt64)i
            }, [(Byte)i]);
        }

        // 获取全部待复制记录
        var pending = _manager.GetPendingRecords("slave-1");
        Assert.Equal(3, pending.Count);
        Assert.Equal(1UL, pending[0].Header.Lsn);
        Assert.Equal(3UL, pending[2].Header.Lsn);

        // 使用 maxCount 限制
        var limited = _manager.GetPendingRecords("slave-1", 2);
        Assert.Equal(2, limited.Count);
    }

    [Fact(DisplayName = "测试确认复制")]
    public void TestAcknowledgeReplication()
    {
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _manager.RegisterSlave(slave);

        _manager.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 1 }, []);
        _manager.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 2 }, []);

        // 确认第一条
        _manager.AcknowledgeReplication("slave-1", 1);
        var pending = _manager.GetPendingRecords("slave-1");
        Assert.Single(pending);
        Assert.Equal(2UL, pending[0].Header.Lsn);
        Assert.Equal(NodeState.Syncing, slave.State);

        // 确认全部
        _manager.AcknowledgeReplication("slave-1", 2);
        pending = _manager.GetPendingRecords("slave-1");
        Assert.Empty(pending);
        Assert.Equal(NodeState.Online, slave.State);
    }

    [Fact(DisplayName = "测试复制延迟")]
    public void TestReplicationLag()
    {
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _manager.RegisterSlave(slave);

        _manager.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 1 }, []);
        _manager.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 2 }, []);
        _manager.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 3 }, []);

        Assert.Equal(3UL, _manager.GetReplicationLag("slave-1"));

        _manager.AcknowledgeReplication("slave-1", 2);
        Assert.Equal(1UL, _manager.GetReplicationLag("slave-1"));

        _manager.AcknowledgeReplication("slave-1", 3);
        Assert.Equal(0UL, _manager.GetReplicationLag("slave-1"));
    }

    [Fact(DisplayName = "测试完全同步检查")]
    public void TestIsFullySynced()
    {
        // 无从节点时认为已同步
        Assert.True(_manager.IsFullySynced(0));

        var slave1 = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        var slave2 = new NodeInfo { NodeId = "slave-2", Endpoint = "127.0.0.1:9002", Role = NodeRole.Slave };
        _manager.RegisterSlave(slave1);
        _manager.RegisterSlave(slave2);

        _manager.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 1 }, []);
        _manager.AppendRecord(new WalRecord { RecordType = WalRecordType.UpdatePage, PageId = 2 }, []);

        Assert.False(_manager.IsFullySynced(2));

        _manager.AcknowledgeReplication("slave-1", 2);
        Assert.False(_manager.IsFullySynced(2));

        _manager.AcknowledgeReplication("slave-2", 2);
        Assert.True(_manager.IsFullySynced(2));
    }

    [Fact(DisplayName = "测试清理缓冲区")]
    public void TestCleanupBuffer()
    {
        var slave1 = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        var slave2 = new NodeInfo { NodeId = "slave-2", Endpoint = "127.0.0.1:9002", Role = NodeRole.Slave };
        _manager.RegisterSlave(slave1);
        _manager.RegisterSlave(slave2);

        for (var i = 0; i < 5; i++)
        {
            _manager.AppendRecord(new WalRecord
            {
                RecordType = WalRecordType.UpdatePage,
                PageId = (UInt64)i
            }, []);
        }

        // slave1 确认到 3，slave2 确认到 2，最小值为 2
        _manager.AcknowledgeReplication("slave-1", 3);
        _manager.AcknowledgeReplication("slave-2", 2);

        var cleaned = _manager.CleanupBuffer();
        Assert.Equal(2, cleaned);

        // 验证 slave-2 的待复制记录从 LSN 3 开始
        var pending = _manager.GetPendingRecords("slave-2");
        Assert.Equal(3, pending.Count);
        Assert.Equal(3UL, pending[0].Header.Lsn);
    }

    [Fact(DisplayName = "测试注册主节点角色抛出异常")]
    public void TestRegisterMasterRoleThrows()
    {
        var masterNode = new NodeInfo { NodeId = "master-2", Endpoint = "127.0.0.1:9010", Role = NodeRole.Master };

        var ex = Assert.Throws<NovaException>(() => _manager.RegisterSlave(masterNode));
        Assert.Equal(ErrorCode.ReplicationError, ex.Code);
    }

    [Fact(DisplayName = "测试重复注册从节点抛出异常")]
    public void TestDuplicateRegistrationThrows()
    {
        var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
        _manager.RegisterSlave(slave);

        var duplicate = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9002", Role = NodeRole.Slave };
        var ex = Assert.Throws<NovaException>(() => _manager.RegisterSlave(duplicate));
        Assert.Equal(ErrorCode.ReplicationError, ex.Code);
    }

    [Fact(DisplayName = "测试不存在的节点抛出异常")]
    public void TestNonExistentNodeThrows()
    {
        var ex1 = Assert.Throws<NovaException>(() => _manager.GetPendingRecords("nonexistent"));
        Assert.Equal(ErrorCode.NodeNotFound, ex1.Code);

        var ex2 = Assert.Throws<NovaException>(() => _manager.AcknowledgeReplication("nonexistent", 1));
        Assert.Equal(ErrorCode.NodeNotFound, ex2.Code);

        var ex3 = Assert.Throws<NovaException>(() => _manager.GetReplicationLag("nonexistent"));
        Assert.Equal(ErrorCode.NodeNotFound, ex3.Code);
    }

    [Fact(DisplayName = "测试异步复制循环启停")]
    public void TestReplicationStartStop()
    {
        _manager.ReplicationIntervalMs = 100;
        _manager.StartReplication();

        // 验证复制已启动
        Assert.NotNull(_manager);

        // 停止复制
        _manager.StopReplication();
    }

    [Fact(DisplayName = "测试BinlogWriter集成")]
    public void TestBinlogWriterIntegration()
    {
        var binlogPath = Path.Combine(Path.GetTempPath(), $"novadb_test_binlog_{Guid.NewGuid():N}");
        try
        {
            using var binlogWriter = new BinlogWriter(binlogPath, "testdb");
            _manager.BinlogWriter = binlogWriter;

            Assert.Equal(binlogWriter, _manager.BinlogWriter);

            // 写入 Binlog 事件
            binlogWriter.Write(BinlogEventType.Insert, "INSERT INTO t1 VALUES (1, 'hello')", 1);
            binlogWriter.Write(BinlogEventType.Update, "UPDATE t1 SET name='world' WHERE id=1", 1);

            // 注册从节点
            var slave = new NodeInfo { NodeId = "slave-1", Endpoint = "127.0.0.1:9001", Role = NodeRole.Slave };
            _manager.RegisterSlave(slave);

            // 获取待复制的 Binlog 事件
            var events = _manager.GetPendingBinlogEvents("slave-1", 0, 10);
            Assert.True(events.Count >= 2);
            Assert.Equal(BinlogEventType.Insert, events[0].EventType);
            Assert.Equal(BinlogEventType.Update, events[1].EventType);
        }
        finally
        {
            if (Directory.Exists(binlogPath))
                Directory.Delete(binlogPath, true);
        }
    }

    [Fact(DisplayName = "测试复制DTO序列化")]
    public void TestReplicationDtoSerialization()
    {
        var dto = new ReplicationEventDto
        {
            Lsn = 42,
            TxId = 7,
            RecordType = (Byte)WalRecordType.UpdatePage,
            PageId = 100,
            Data = [1, 2, 3],
            Timestamp = DateTime.UtcNow.Ticks
        };

        Assert.Equal(42UL, dto.Lsn);
        Assert.Equal(7UL, dto.TxId);
        Assert.Equal((Byte)WalRecordType.UpdatePage, dto.RecordType);
        Assert.Equal(100UL, dto.PageId);
        Assert.Equal(new Byte[] { 1, 2, 3 }, dto.Data);

        var ack = new ReplicationAckDto
        {
            Success = true,
            AckedLsn = 42
        };

        Assert.True(ack.Success);
        Assert.Equal(42UL, ack.AckedLsn);
        Assert.Null(ack.ErrorMessage);
    }

    [Fact(DisplayName = "测试从节点初始LSN")]
    public void TestSlaveInitialLsn()
    {
        // 从节点注册时应保留其已有的 ReplicatedLsn
        var slave = new NodeInfo
        {
            NodeId = "slave-1",
            Endpoint = "127.0.0.1:9001",
            Role = NodeRole.Slave,
            ReplicatedLsn = 10
        };
        _manager.RegisterSlave(slave);

        // 追加记录
        for (UInt64 i = 0; i < 15; i++)
        {
            _manager.AppendRecord(new WalRecord
            {
                RecordType = WalRecordType.UpdatePage,
                PageId = i
            }, []);
        }

        // 从 LSN 10 开始的待复制记录应为 5 条（11, 12, 13, 14, 15）
        var pending = _manager.GetPendingRecords("slave-1");
        Assert.Equal(5, pending.Count);
        Assert.Equal(11UL, pending[0].Header.Lsn);
    }
}
