namespace NewLife.NovaDb.Server;

/// <summary>客户端会话</summary>
public class NovaSession
{
    /// <summary>会话 ID</summary>
    public String SessionId { get; }

    /// <summary>是否已认证</summary>
    public Boolean IsAuthenticated { get; set; }

    /// <summary>连接时间</summary>
    public DateTime ConnectedAt { get; }

    /// <summary>最后活跃时间</summary>
    public DateTime LastActiveAt { get; set; }

    /// <summary>会话超时（秒），默认 300</summary>
    public Int32 TimeoutSeconds { get; set; } = 300;

    /// <summary>创建会话</summary>
    public NovaSession()
    {
        SessionId = Guid.NewGuid().ToString("N");
        ConnectedAt = DateTime.UtcNow;
        LastActiveAt = DateTime.UtcNow;
    }

    /// <summary>是否已过期</summary>
    /// <returns>过期返回 true</returns>
    public Boolean IsExpired() => (DateTime.UtcNow - LastActiveAt).TotalSeconds > TimeoutSeconds;

    /// <summary>刷新活跃时间</summary>
    public void Touch() => LastActiveAt = DateTime.UtcNow;
}
