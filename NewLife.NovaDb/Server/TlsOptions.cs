namespace NewLife.NovaDb.Server;

/// <summary>TLS/SSL 传输加密配置选项</summary>
/// <remarks>
/// 对应模块 X06（TLS 传输加密）。
/// 配置服务器端 TLS 证书路径和密码，启用后所有客户端连接强制加密。
/// </remarks>
public class TlsOptions
{
    /// <summary>是否启用 TLS</summary>
    public Boolean Enabled { get; set; }

    /// <summary>PFX 证书文件路径</summary>
    public String CertificatePath { get; set; } = String.Empty;

    /// <summary>证书密码</summary>
    public String CertificatePassword { get; set; } = String.Empty;

    /// <summary>是否要求客户端证书</summary>
    public Boolean RequireClientCertificate { get; set; }

    /// <summary>最低 TLS 版本（默认 1.2）</summary>
    public String MinTlsVersion { get; set; } = "1.2";

    /// <summary>验证配置有效性</summary>
    /// <returns>验证通过返回 true</returns>
    public Boolean Validate()
    {
        if (!Enabled) return true;

        if (String.IsNullOrEmpty(CertificatePath)) return false;

        return true;
    }

    /// <summary>验证并抛出异常</summary>
    public void ValidateOrThrow()
    {
        if (!Enabled) return;

        if (String.IsNullOrEmpty(CertificatePath))
            throw new InvalidOperationException("TLS is enabled but CertificatePath is not set");
    }
}
