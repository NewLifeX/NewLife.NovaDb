using System;
using System.ComponentModel;
using System.Globalization;
using NewLife;
using NewLife.Log;
using NewLife.NovaDb.Core;
using Xunit;
using L10n = NewLife.Localization;

namespace XUnitTest.Localization;

/// <summary>本地化测试集合。Localization.Culture 是进程级全局状态，禁用并行避免相互干扰</summary>
[CollectionDefinition("Localization", DisableParallelization = true)]
public class LocalizationCollection { }

/// <summary>运行时多语言测试。覆盖中英翻译、占位符格式化、异常与日志语言、未注册语言回退与 10 种语言表注册</summary>
[Collection("Localization")]
public class LocalizationTests
{
    private static readonly String[] _cultures =
    [
        "zh-Hans", "zh-Hant", "es", "de", "ja", "fr", "pt", "ru", "ko", "ar",
    ];

    private static T WithCulture<T>(String name, Func<T> func)
    {
        var old = L10n.Culture;
        try
        {
            L10n.Culture = new CultureInfo(name);
            return func();
        }
        finally
        {
            L10n.Culture = old;
        }
    }

    [Fact]
    [DisplayName("简体中文词表翻译英文源文")]
    public void Chinese_Translate()
    {
        _ = typeof(NovaException);

        Assert.Equal("不支持的数据类型：255", WithCulture("zh-CN", () => L10n.Translate("Unsupported data type: {0}", 255)));
    }

    [Fact]
    [DisplayName("繁体中文词表翻译英文源文")]
    public void TraditionalChinese_Translate()
    {
        _ = typeof(NovaException);

        var text = WithCulture("zh-TW", () => L10n.Translate("Unsupported data type: {0}", 255));
        Assert.Equal("不支援的資料類型：255", text);
    }

    [Fact]
    [DisplayName("占位符按目标语言格式化")]
    public void Placeholder_Formatted()
    {
        _ = typeof(NovaException);

        Assert.Equal(
            "LZ4 解压长度不匹配：期望 100，实际 80。",
            WithCulture("zh-CN", () => L10n.Translate("LZ4 decompressed length mismatch: expected {0}, actual {1}.", 100, 80)));
    }

    [Fact]
    [DisplayName("未注册语言回退英文源文")]
    public void UnsupportedCulture_FallsBackToEnglish()
    {
        _ = typeof(NovaException);

        Assert.Equal(
            "Unsupported data type: 255",
            WithCulture("sw-KE", () => L10n.Translate("Unsupported data type: {0}", 255)));
    }

    [Fact]
    [DisplayName("NovaException 在构造时按当前语言翻译且保留错误码")]
    public void NovaException_Translated()
    {
        _ = typeof(NovaException);

        var ex = WithCulture("zh-CN", () => new NovaException(ErrorCode.InvalidArgument, "Unsupported data type: {0}", 255));
        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Equal("不支持的数据类型：255", ex.Message);
    }

    [Fact]
    [DisplayName("异常语言与日志语言相互独立")]
    public void LogCulture_Independent()
    {
        _ = typeof(NovaException);

        var oldCulture = L10n.Culture;
        var oldLogCulture = L10n.LogCulture;
        try
        {
            L10n.Culture = new CultureInfo("zh-CN");
            L10n.LogCulture = new CultureInfo("en-US");

            Assert.Equal("不支持的数据类型：255", L10n.Translate("Unsupported data type: {0}", 255));
            Assert.Equal("Unsupported data type: 255", L10n.TranslateForLog("Unsupported data type: {0}", 255));
        }
        finally
        {
            L10n.Culture = oldCulture;
            L10n.LogCulture = oldLogCulture;
        }
    }

    [Fact]
    [DisplayName("日志格式化按日志语言翻译")]
    public void LoggerFormat_Translated()
    {
        _ = typeof(NovaException);

        var oldLogCulture = L10n.LogCulture;
        try
        {
            L10n.LogCulture = new CultureInfo("zh-CN");
            Assert.Equal("不支持的数据类型：255", Logger.FormatMessage(null, "Unsupported data type: {0}", [255]));

            L10n.LogCulture = new CultureInfo("en-US");
            Assert.Equal("Unsupported data type: 255", Logger.FormatMessage(null, "Unsupported data type: {0}", [255]));
        }
        finally
        {
            L10n.LogCulture = oldLogCulture;
        }
    }

    [Fact]
    [DisplayName("10 种语言词表均已注册")]
    public void AllLocales_Registered()
    {
        _ = typeof(NovaException);

        foreach (var code in _cultures)
        {
            Assert.True(L10n.IsSupported(new CultureInfo(code)), $"缺少语言表：{code}");
        }
    }

    [Fact]
    [DisplayName("程序集标题与描述词条可本地化")]
    public void AssemblyMetadata_Translate()
    {
        _ = typeof(NovaException);

        Assert.Equal("NewLife.NovaDb 混合数据库", WithCulture("zh-CN", () => L10n.Translate("NewLife.NovaDb Hybrid Database")));
        Assert.Equal("NewLife.NovaDb 混合資料庫", WithCulture("zh-TW", () => L10n.Translate("NewLife.NovaDb Hybrid Database")));
    }
}
