using System;
using System.Text;
using NewLife.Data;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Server;

/// <summary>协议头单元测试</summary>
public class ProtocolTests
{
    [Fact(DisplayName = "测试协议头序列化与反序列化")]
    public void TestHeaderRoundTrip()
    {
        var header = new ProtocolHeader
        {
            Version = 1,
            RequestType = RequestType.Execute,
            SequenceId = 12345,
            PayloadLength = 100,
            Status = ResponseStatus.Ok
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        Assert.Equal(ProtocolHeader.HeaderSize, bytes.Length);

        var parsed = ProtocolHeader.Read(new ArrayPacket(bytes));
        Assert.Equal(header.Version, parsed.Version);
        Assert.Equal(header.RequestType, parsed.RequestType);
        Assert.Equal(header.SequenceId, parsed.SequenceId);
        Assert.Equal(header.PayloadLength, parsed.PayloadLength);
        Assert.Equal(header.Status, parsed.Status);
    }

    [Fact(DisplayName = "测试协议魔数验证")]
    public void TestMagicNumberValidation()
    {
        var bytes = new Byte[ProtocolHeader.HeaderSize];
        // 设置错误的魔数
        bytes[0] = 0xFF;
        bytes[1] = 0xFF;

        Assert.Throws<InvalidOperationException>(() => ProtocolHeader.Read(new ArrayPacket(bytes)));
    }

    [Fact(DisplayName = "测试协议魔数值")]
    public void TestMagicNumberValue()
    {
        Assert.Equal((UInt16)0x4E56, ProtocolHeader.Magic);

        var header = new ProtocolHeader();
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();

        // 验证前两个字节为魔数
        Assert.Equal(0x4E, bytes[0]);
        Assert.Equal(0x56, bytes[1]);
    }

    [Fact(DisplayName = "测试所有请求类型序列化")]
    public void TestAllRequestTypes()
    {
        var requestTypes = new[]
        {
            RequestType.Handshake,
            RequestType.Execute,
            RequestType.Query,
            RequestType.Fetch,
            RequestType.Close,
            RequestType.Ping,
            RequestType.BeginTx,
            RequestType.CommitTx,
            RequestType.RollbackTx
        };

        foreach (var reqType in requestTypes)
        {
            var header = new ProtocolHeader { RequestType = reqType, SequenceId = (UInt32)reqType };
            using var pk = header.ToPacket();
            var bytes = pk.GetSpan().ToArray();
            var parsed = ProtocolHeader.Read(new ArrayPacket(bytes));

            Assert.Equal(reqType, parsed.RequestType);
            Assert.Equal((UInt32)reqType, parsed.SequenceId);
        }
    }

    [Fact(DisplayName = "测试缓冲区过小抛出异常")]
    public void TestBufferTooSmallThrows()
    {
        var bytes = new Byte[8];
        Assert.Throws<ArgumentException>(() => ProtocolHeader.Read(new ArrayPacket(bytes)));
    }

    [Fact(DisplayName = "测试空缓冲区抛出异常")]
    public void TestNullBufferThrows()
    {
        Assert.Throws<ArgumentNullException>(() => ProtocolHeader.Read(null!));
    }

    [Fact(DisplayName = "测试头部大小为 16 字节")]
    public void TestHeaderSize()
    {
        Assert.Equal(16, ProtocolHeader.HeaderSize);

        var header = new ProtocolHeader();
        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        Assert.Equal(16, bytes.Length);
    }

    [Fact(DisplayName = "测试大序列号")]
    public void TestLargeSequenceId()
    {
        var header = new ProtocolHeader
        {
            SequenceId = UInt32.MaxValue,
            PayloadLength = 1024
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        var parsed = ProtocolHeader.Read(new ArrayPacket(bytes));

        Assert.Equal(UInt32.MaxValue, parsed.SequenceId);
        Assert.Equal(1024, parsed.PayloadLength);
    }

    [Fact(DisplayName = "测试超大负载长度抛出异常")]
    public void TestExcessivePayloadLengthThrows()
    {
        var header = new ProtocolHeader
        {
            PayloadLength = Int32.MaxValue
        };

        using var pk = header.ToPacket();
        var bytes = pk.GetSpan().ToArray();
        Assert.Throws<InvalidOperationException>(() => ProtocolHeader.Read(new ArrayPacket(bytes)));
    }
}
