using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Tests;

/// <summary>
/// 灯带机型表匹配测试（表来源：clevo-xsm-wmi DMI 表 kb_full_color_with_extra_ops 条目，
/// 见 dchu-protocol-findings.md 第九节 9.4）。只测纯匹配函数，不触注册表。
/// </summary>
public sealed class LightbarDetectorTests
{
    [Theory]
    [InlineData("P870DM")]
    [InlineData("P7xxDM(-G)")]
    [InlineData("P750ZM")]
    [InlineData("P17SM-A")]
    [InlineData("Deimos/Phobos 1x15S")]
    [InlineData("P5 Pro SE")]
    [InlineData("P870DMX")]                 // 前缀命中（同系列衍生型号）
    [InlineData("p750zm")]                  // 大小写不敏感
    public void MatchesLightbar_KnownLightbarModels_True(string productName)
    {
        Assert.True(LightbarDetector.MatchesLightbar(productName));
    }

    [Theory]
    [InlineData("P955ET1")]                 // 本机：单分区，无灯带
    [InlineData("P65_67RSRP")]              // 同表 kb_full_color_ops（无 extra）
    [InlineData("P15SM")]                   // kb_8_color_ops
    [InlineData("NH5x")]
    [InlineData("")]
    public void MatchesLightbar_NonLightbarModels_False(string productName)
    {
        Assert.False(LightbarDetector.MatchesLightbar(productName));
    }

    [Fact]
    public void MatchesLightbar_Null_False()
    {
        Assert.False(LightbarDetector.MatchesLightbar(null));
    }
}
