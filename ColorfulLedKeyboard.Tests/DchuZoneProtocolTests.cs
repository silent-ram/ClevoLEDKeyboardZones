using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Tests;

/// <summary>
/// DchuZoneProtocol 黄金向量（协议规格：dchu-protocol-findings.md 第九节）。
/// 字节序 BRG：B 在 bit16..23、R 在 bit8..15、G 在 bit0..7；zone 子命令 0xF0..0xF3。
/// </summary>
public sealed class DchuZoneProtocolTests
{
    public static TheoryData<int, uint> ZoneTopByte => new()
    {
        { 0, 0xF0000000u },
        { 1, 0xF1000000u },
        { 2, 0xF2000000u },
        { 3, 0xF3000000u },
    };

    [Theory]
    [MemberData(nameof(ZoneTopByte))]
    public void PackZoneColorArgs_White(int zone, uint expectedTop)
    {
        // 白 = 0xFFFFFF → 0xFZFFFFFF（白对通道互换不敏感，只验证 zone 编码）
        var args = DchuZoneProtocol.PackZoneColorArgs(zone, new RgbColor(255, 255, 255));
        Assert.Equal(expectedTop | 0x00FFFFFFu, (uint)args);
    }

    [Fact]
    public void PackZoneColorArgs_Zone0_Red()
    {
        // 红：R=FF → bit8..15 → 0xF000FF00（通道序判据用例）
        var args = DchuZoneProtocol.PackZoneColorArgs(0, new RgbColor(255, 0, 0));
        Assert.Equal(0xF000FF00u, (uint)args);
    }

    [Fact]
    public void PackZoneColorArgs_Zone0_Green()
    {
        // 绿：G=FF → bit0..7 → 0xF00000FF
        var args = DchuZoneProtocol.PackZoneColorArgs(0, new RgbColor(0, 255, 0));
        Assert.Equal(0xF00000FFu, (uint)args);
    }

    [Fact]
    public void PackZoneColorArgs_Zone0_Blue()
    {
        // 蓝：B=FF → bit16..23 → 0xF0FF0000
        var args = DchuZoneProtocol.PackZoneColorArgs(0, new RgbColor(0, 0, 255));
        Assert.Equal(0xF0FF0000u, (uint)args);
    }

    [Fact]
    public void PackZoneColorArgs_Zone1_Green_MatchesDocExample()
    {
        // 文档 9.3 编码示例：zone1 绿 = 0xF10000FF
        var args = DchuZoneProtocol.PackZoneColorArgs(1, new RgbColor(0, 255, 0));
        Assert.Equal(0xF10000FFu, (uint)args);
    }

    [Fact]
    public void PackZoneColorArgs_Zone2_Blue_MatchesTuxedoEncoding()
    {
        // TUXEDO SUB_RGB_ZONE_2 + 蓝：B<<16 → 0xF2FF0000
        var args = DchuZoneProtocol.PackZoneColorArgs(2, new RgbColor(0, 0, 255));
        Assert.Equal(0xF2FF0000u, (uint)args);
    }

    [Fact]
    public void PackZoneColorArgs_Zone3_LightbarAmber()
    {
        // 灯带琥珀（R=255,G=128,B=0）→ 0xF300FF80
        var args = DchuZoneProtocol.PackZoneColorArgs(3, new RgbColor(255, 128, 0));
        Assert.Equal(0xF300FF80u, (uint)args);
    }

    [Fact]
    public void PackZoneColorArgs_MixedBytes()
    {
        // R=0x12,G=0x34,B=0x56 → BRG = 0x561234 → 0xF1561234（与单区编码同字节序）
        var args = DchuZoneProtocol.PackZoneColorArgs(2, new RgbColor(0x12, 0x34, 0x56));
        Assert.Equal(0xF2561234u, (uint)args);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(7)]
    public void PackZoneColorArgs_ZoneOutOfRange_Throws(int zone)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DchuZoneProtocol.PackZoneColorArgs(zone, new RgbColor(255, 255, 255)));
    }

    [Theory]
    [InlineData(63, 0xF400003Fu)]
    [InlineData(126, 0xF400007Eu)]
    [InlineData(189, 0xF40000BDu)]
    [InlineData(252, 0xF40000FCu)]
    [InlineData(255, 0xF40000FFu)]
    public void PackZoneBrightnessArgs_RawBytePassthrough(byte level, uint expected)
    {
        // clevo-xsm-wmi 4 档 {63,126,189,252} + TUXEDO 连续上限 255
        Assert.Equal(expected, (uint)DchuZoneProtocol.PackZoneBrightnessArgs(level));
    }

    [Fact]
    public void PackCustomModeArgs_Is0x10000000()
    {
        Assert.Equal(0x10000000u, (uint)DchuZoneProtocol.PackCustomModeArgs());
    }

    [Fact]
    public void BuildZoneStaticSequence_SortsZonesAscending_AppendsBrightness()
    {
        var sequence = DchuZoneProtocol.BuildZoneStaticSequence(
        [
            (2, new RgbColor(0, 0, 255)),   // 乱序传入
            (0, new RgbColor(255, 0, 0)),
            (1, new RgbColor(0, 255, 0)),
        ], 63);

        Assert.Equal(
        [
            0x10000000u,        // CUSTOM 模式（时序第 1 步）
            0xF000FF00u,        // zone0 红
            0xF10000FFu,        // zone1 绿
            0xF2FF0000u,        // zone2 蓝
            0xF400003Fu,        // 亮度（时序第 3 步）
        ], sequence.Select(args => (uint)args));
    }

    [Fact]
    public void BuildZoneStaticSequence_LightbarSortsLast()
    {
        // zone3（灯带）升序后排最后；是否入列由调用方决定
        var sequence = DchuZoneProtocol.BuildZoneStaticSequence(
        [
            (3, new RgbColor(255, 128, 0)),
            (1, new RgbColor(255, 255, 255)),
            (0, new RgbColor(255, 255, 255)),
        ], 252);

        Assert.Equal(
        [
            0x10000000u,
            0xF0FFFFFFu,        // zone0
            0xF1FFFFFFu,        // zone1
            0xF300FF80u,        // zone3 灯带（升序排最后）
            0xF40000FCu,
        ], sequence.Select(args => (uint)args));
    }

    [Fact]
    public void BuildZoneStaticSequence_EmptyZones_StillModeAndBrightness()
    {
        var sequence = DchuZoneProtocol.BuildZoneStaticSequence([], 126);
        Assert.Equal([0x10000000u, 0xF400007Eu], sequence.Select(args => (uint)args));
    }
}
