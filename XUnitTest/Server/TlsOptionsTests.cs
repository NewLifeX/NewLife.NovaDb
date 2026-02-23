using System;
using NewLife.NovaDb.Server;
using Xunit;

namespace XUnitTest.Server;

public class TlsOptionsTests
{
    [Fact(DisplayName = "默认禁用 TLS")]
    public void DefaultDisabled()
    {
        var options = new TlsOptions();

        Assert.False(options.Enabled);
        Assert.Equal(String.Empty, options.CertificatePath);
        Assert.Equal(String.Empty, options.CertificatePassword);
        Assert.False(options.RequireClientCertificate);
        Assert.Equal("1.2", options.MinTlsVersion);
    }

    [Fact(DisplayName = "禁用时验证通过")]
    public void DisabledValidatesOk()
    {
        var options = new TlsOptions { Enabled = false };
        Assert.True(options.Validate());
    }

    [Fact(DisplayName = "启用但未设置证书路径验证失败")]
    public void EnabledWithoutCertFails()
    {
        var options = new TlsOptions { Enabled = true };
        Assert.False(options.Validate());
    }

    [Fact(DisplayName = "启用且设置证书路径验证通过")]
    public void EnabledWithCertPasses()
    {
        var options = new TlsOptions
        {
            Enabled = true,
            CertificatePath = "/path/to/cert.pfx"
        };
        Assert.True(options.Validate());
    }

    [Fact(DisplayName = "ValidateOrThrow 启用无证书抛异常")]
    public void ValidateOrThrowThrowsWhenNoCert()
    {
        var options = new TlsOptions { Enabled = true };
        Assert.Throws<InvalidOperationException>(() => options.ValidateOrThrow());
    }

    [Fact(DisplayName = "ValidateOrThrow 禁用不抛异常")]
    public void ValidateOrThrowDisabledOk()
    {
        var options = new TlsOptions { Enabled = false };
        options.ValidateOrThrow(); // Should not throw
    }

    [Fact(DisplayName = "RequireClientCertificate 可设置")]
    public void RequireClientCertificateCanBeSet()
    {
        var options = new TlsOptions
        {
            Enabled = true,
            CertificatePath = "/path/to/cert.pfx",
            RequireClientCertificate = true,
            MinTlsVersion = "1.3"
        };

        Assert.True(options.RequireClientCertificate);
        Assert.Equal("1.3", options.MinTlsVersion);
        Assert.True(options.Validate());
    }
}
