namespace NewLife.NovaDb.Core;

/// <summary>NovaDb 网络模式配置选项，继承嵌入模式配置并增加网络模式专属设置</summary>
public class ServerDbOptions : DbOptions
{
    #region Binlog
    /// <summary>是否启用 Binlog。网络模式默认启用</summary>
    public Boolean EnableBinlog { get; set; } = true;

    /// <summary>单个 Binlog 文件最大大小（字节），达到后滚动到新文件。默认 256MB</summary>
    public Int64 BinlogMaxFileSize { get; set; } = 256L * 1024 * 1024;

    /// <summary>Binlog 保留天数，超过后自动清理旧文件。0 表示不按时间清理（仅按从节点确认清理）</summary>
    public Int32 BinlogRetentionDays { get; set; }

    /// <summary>Binlog 总磁盘空间上限（字节），超过后从最早文件开始删除。0 表示不限制</summary>
    public Int64 BinlogMaxTotalSize { get; set; }
    #endregion
}
