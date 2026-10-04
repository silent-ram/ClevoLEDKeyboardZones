namespace ColorfulLedKeyboard.Core;

/// <summary>
/// 三区 + 灯带协议纯编码函数（docs/reverse-engineering/dchu-protocol-findings.md 第九节）。
/// 只做 ARGS 编码，不做任何传输；是否下发由 <see cref="DchuKeyboardDevice"/> 的能力门控决定。
/// 字节序为 BRG：B 在 bit16..23、R 在 bit8..15、G 在 bit0..7
/// （TUXEDO 常量 SUB_RGB_ZONE_0/1/2/3 与 BRIGHTNESS，见 9.3 常量对照）。
/// </summary>
public static class DchuZoneProtocol
{
    /// <summary>SCMD 写命令（0x67）：单区三槽位与三区/灯带/亮度共用的总入口。</summary>
    public const int SetDchuLedCommand = 103;

    /// <summary>能力探测读命令 GET_BIOS_FEATURES_1（第九节 9.9，读类，零 EC 写）。</summary>
    public const int GetBiosFeatures1Command = 0x52;

    /// <summary>
    /// 三区/灯带颜色：ARGS = (0xF0|zone)&lt;&lt;24 | B&lt;&lt;16 | R&lt;&lt;8 | G。
    /// zone：0 左 / 1 中 / 2 右 / 3 灯带；越界抛 <see cref="ArgumentOutOfRangeException"/>。
    /// 黄金向量：zone0 白 = 0xF0FFFFFF，zone0 红 = 0xF000FF00，zone1 绿 = 0xF10000FF。
    /// </summary>
    public static int PackZoneColorArgs(int zone, RgbColor color)
    {
        if (zone is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(zone), zone, "zone 必须在 0..3（0 左 / 1 中 / 2 右 / 3 灯带）。");
        }

        return unchecked((int)(((uint)(0xF0 | zone) << 24) | ((uint)color.B << 16) | ((uint)color.R << 8) | color.G));
    }

    /// <summary>亮度：ARGS = 0xF4000000 | level（原始字节直传；连续值或 63/126/189/252 档由调用方决定）。</summary>
    public static int PackZoneBrightnessArgs(byte level) => unchecked((int)(0xF4000000u | level));

    /// <summary>静态应用时序第 1 步：切 CUSTOM/静态模式（clevo-xsm-wmi CUSTOM 分支；DSDT 映射 EC 0xC4/0x03 参数 0）。</summary>
    public static int PackCustomModeArgs() => unchecked((int)0x10000000u);

    /// <summary>
    /// 静态应用序列：[0x10000000, 各区颜色按 zone 升序（zone 可含 3=灯带，升序后自然排最后）, 亮度]。
    /// 灯带是否入列由调用方组装 zones 时决定；本函数只做编码。
    /// </summary>
    public static IReadOnlyList<int> BuildZoneStaticSequence(IReadOnlyList<(int zone, RgbColor color)> zones, byte level)
    {
        var sequence = new List<int>(zones.Count + 2) { PackCustomModeArgs() };
        foreach (var (zone, color) in zones.OrderBy(entry => entry.zone))
        {
            sequence.Add(PackZoneColorArgs(zone, color));
        }

        sequence.Add(PackZoneBrightnessArgs(level));
        return sequence;
    }
}
