namespace NewLife.NovaDb.Storage;

/// <summary>数据库状态枚举</summary>
public enum DatabaseStatus
{
    /// <summary>在线（目录和元数据文件均存在且有效）</summary>
    Online = 0,

    /// <summary>离线（此前记录的数据库在本次扫描中未发现）</summary>
    Offline = 1
}
