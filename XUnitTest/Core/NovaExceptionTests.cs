using System;
using NewLife.NovaDb.Core;
using Xunit;

namespace XUnitTest.Core;

/// <summary>NovaException 异常测试</summary>
public class NovaExceptionTests
{
    [Fact]
    public void TestConstructorWithMessage()
    {
        var exception = new NovaException(ErrorCode.TableNotFound, "Table 'users' not found");

        Assert.Equal(ErrorCode.TableNotFound, exception.Code);
        Assert.Equal("Table 'users' not found", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void TestConstructorWithInnerException()
    {
        var innerException = new InvalidOperationException("Inner error");
        var exception = new NovaException(
            ErrorCode.IoError,
            "Failed to read file",
            innerException
        );

        Assert.Equal(ErrorCode.IoError, exception.Code);
        Assert.Equal("Failed to read file", exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public void TestErrorCodePreservedThroughCatch()
    {
        try
        {
            throw new NovaException(ErrorCode.ChecksumFailed, "Checksum mismatch");
        }
        catch (NovaException ex)
        {
            Assert.Equal(ErrorCode.ChecksumFailed, ex.Code);
        }
    }

    [Theory]
    [InlineData(ErrorCode.Unknown, 0)]
    [InlineData(ErrorCode.FileCorrupted, 1000)]
    [InlineData(ErrorCode.ChecksumFailed, 1001)]
    [InlineData(ErrorCode.IncompatibleFileFormat, 1002)]
    [InlineData(ErrorCode.ParseFailed, 2000)]
    [InlineData(ErrorCode.SyntaxError, 2001)]
    [InlineData(ErrorCode.TransactionConflict, 3000)]
    [InlineData(ErrorCode.Deadlock, 3001)]
    [InlineData(ErrorCode.TransactionError, 3002)]
    [InlineData(ErrorCode.TableExists, 4000)]
    [InlineData(ErrorCode.TableNotFound, 4001)]
    [InlineData(ErrorCode.PrimaryKeyConflict, 4002)]
    [InlineData(ErrorCode.ConstraintViolation, 4003)]
    [InlineData(ErrorCode.ShardNotFound, 4004)]
    [InlineData(ErrorCode.ShardLimitExceeded, 4005)]
    [InlineData(ErrorCode.StreamNotFound, 4006)]
    [InlineData(ErrorCode.ConsumerGroupNotFound, 4007)]
    [InlineData(ErrorCode.MessageExpired, 4008)]
    [InlineData(ErrorCode.KeyNotFound, 4009)]
    [InlineData(ErrorCode.KeyExpired, 4010)]
    [InlineData(ErrorCode.NotSupported, 5000)]
    [InlineData(ErrorCode.InvalidArgument, 5001)]
    [InlineData(ErrorCode.IoError, 6000)]
    [InlineData(ErrorCode.DiskFull, 6001)]
    [InlineData(ErrorCode.ConnectionFailed, 7000)]
    [InlineData(ErrorCode.AuthenticationFailed, 7001)]
    [InlineData(ErrorCode.ProtocolError, 7002)]
    [InlineData(ErrorCode.SessionExpired, 7003)]
    [InlineData(ErrorCode.ReplicationError, 8000)]
    [InlineData(ErrorCode.NodeNotFound, 8001)]
    [InlineData(ErrorCode.NotMaster, 8002)]
    [InlineData(ErrorCode.ReplicationLag, 8003)]
    public void TestErrorCodeValues(ErrorCode code, Int32 expectedValue)
    {
        Assert.Equal(expectedValue, (Int32)code);
    }

    [Fact]
    public void TestErrorCodeCategories()
    {
        // 文件错误 (1000-1999)
        Assert.InRange((Int32)ErrorCode.FileCorrupted, 1000, 1999);
        Assert.InRange((Int32)ErrorCode.ChecksumFailed, 1000, 1999);

        // 解析错误 (2000-2999)
        Assert.InRange((Int32)ErrorCode.ParseFailed, 2000, 2999);
        Assert.InRange((Int32)ErrorCode.SyntaxError, 2000, 2999);

        // 事务错误 (3000-3999)
        Assert.InRange((Int32)ErrorCode.TransactionConflict, 3000, 3999);
        Assert.InRange((Int32)ErrorCode.Deadlock, 3000, 3999);

        // 数据错误 (4000-4999)
        Assert.InRange((Int32)ErrorCode.TableNotFound, 4000, 4999);
        Assert.InRange((Int32)ErrorCode.PrimaryKeyConflict, 4000, 4999);

        // 通用错误 (5000-5999)
        Assert.InRange((Int32)ErrorCode.NotSupported, 5000, 5999);
        Assert.InRange((Int32)ErrorCode.InvalidArgument, 5000, 5999);

        // I/O 错误 (6000-6999)
        Assert.InRange((Int32)ErrorCode.IoError, 6000, 6999);
        Assert.InRange((Int32)ErrorCode.DiskFull, 6000, 6999);

        // 网络错误 (7000-7999)
        Assert.InRange((Int32)ErrorCode.ConnectionFailed, 7000, 7999);
        Assert.InRange((Int32)ErrorCode.ProtocolError, 7000, 7999);

        // 集群错误 (8000-8999)
        Assert.InRange((Int32)ErrorCode.ReplicationError, 8000, 8999);
        Assert.InRange((Int32)ErrorCode.NotMaster, 8000, 8999);
    }

    [Fact]
    public void TestInheritanceFromException()
    {
        var exception = new NovaException(ErrorCode.Unknown, "Test");
        Assert.IsAssignableFrom<Exception>(exception);
    }

    [Fact]
    public void TestMultipleCatchBlocks()
    {
        Exception? caughtException = null;

        try
        {
            throw new NovaException(ErrorCode.TableNotFound, "Table missing");
        }
        catch (NovaException ex)
        {
            caughtException = ex;
        }
        catch (Exception)
        {
            Assert.Fail("Should have caught NovaException specifically");
        }

        Assert.NotNull(caughtException);
        Assert.IsType<NovaException>(caughtException);
    }
}
