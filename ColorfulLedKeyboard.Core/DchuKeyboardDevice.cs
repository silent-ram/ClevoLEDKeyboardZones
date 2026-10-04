using System.Runtime.InteropServices;

namespace ColorfulLedKeyboard.Core;

/// <summary>
/// DCHU 键盘灯接口：单区路径（现有行为，零改动）+ 三区/灯带路径（能力门控）。
///
/// <para>单区路径 <see cref="SetColor"/>：Local7=0xF / Local4=0..2 三槽位写（Clevo 标准三区编码，
/// 见 dchu-protocol-findings.md 第三/九节）。在 P955ET1 这类单分区固件上三个槽位退化为
/// 同一全局寄存器组，等价于"全键盘上色"；在三区固件上三槽位即左/中/右分区寄存器，
/// 统一上色同样是正确行为——因此该路径对所有机型都成立，保持零改动。</para>
///
/// <para>三区路径 <see cref="SetZoneColor"/> / <see cref="SetZoneBrightness"/> /
/// <see cref="ApplyZoneStatic"/>：仅当 <see cref="Has3ZoneKeyboard"/>（GET_BIOS_FEATURES_1
/// 的 0x00400000 位）为真时放行；未命中时抛 <see cref="NotSupportedException"/>，
/// 绝不静默降级下发——这是"单分区用户行为零变化"的门控保证。</para>
/// </summary>
public sealed class DchuKeyboardDevice
{
    private const int SetDchuLedCommand = 103; // SCMD 0x67
    private const int GetBiosFeatures1Command = 0x52; // GET_BIOS_FEATURES_1（第九节 9.9，读类，零 EC 写）
    private const int KeyboardFeatures3ZoneRgb = 0x00400000;

    private readonly IDchuTransport _transport;
    private bool? _has3ZoneKeyboard;

    /// <summary>生产构造：P/Invoke 直通，与旧版行为完全一致。</summary>
    public DchuKeyboardDevice() : this(new DchuPInvokeTransport())
    {
    }

    /// <summary>测试/模拟器注入构造：传入 FakeDchuTransport 即可进程内验证，零真实 EC 写入。</summary>
    public DchuKeyboardDevice(IDchuTransport transport)
    {
        _transport = transport;
    }

    // ---- 单区路径（现有行为，逐字节保持不变）----

    /// <summary>把整块键盘面板设为指定颜色（三槽位写：Local4=0/1/2，最后一次触发 EC mode 5 应用）。</summary>
    public void SetColor(RgbColor color)
    {
        WriteSequenceSlot(color, slot: 0);
        WriteSequenceSlot(color, slot: 1);
        WriteSequenceSlot(color, slot: 2);
    }

    /// <summary>
    /// 向 SCMD 0x67 Local7=0xF 路径的指定槽位写入颜色。
    /// ARGS 位字段：bit 31..28 = Local7 = 0xF；bit 27..24 = Local4 = slot（0..2）；
    /// bit 23..16 = B；bit 15..8 = R；bit 7..0 = G（BRG 字节序，与 TUXEDO 三区常量一致）。
    /// </summary>
    internal static int BuildSequenceSlotArgs(RgbColor color, int slot)
    {
        var commandByte = 0xF0 | (slot & 0x0F);
        var encodedColor = (color.B << 16) | (color.R << 8) | color.G;
        return (commandByte << 24) | encodedColor;
    }

    private void WriteSequenceSlot(RgbColor color, int slot)
    {
        _transport.SetData(SetDchuLedCommand, BuildSequenceSlotArgs(color, slot));
    }

    // ---- 能力探测（读类，零 EC 写）----

    /// <summary>GET_BIOS_FEATURES_1（0x52）原始返回值；调用方一般用 <see cref="Has3ZoneKeyboard"/>。</summary>
    public int GetKeyboardFeatures() => _transport.GetInteger(GetBiosFeatures1Command);

    /// <summary>
    /// 三区 RGB 键盘能力位（0x00400000）探测，结果缓存。
    /// 探测异常或命令不支持（如 0x80000002）一律按 false 处理——安全默认，回退单区路径。
    /// </summary>
    public bool Has3ZoneKeyboard
    {
        get
        {
            if (_has3ZoneKeyboard.HasValue)
            {
                return _has3ZoneKeyboard.Value;
            }

            bool detected;
            try
            {
                detected = (GetKeyboardFeatures() & KeyboardFeatures3ZoneRgb) != 0;
            }
            catch
            {
                detected = false;
            }

            _has3ZoneKeyboard = detected;
            return detected;
        }
    }

    // ---- 三区/灯带路径（门控，未命中即拒绝）----

    /// <summary>设置单个分区颜色。zone：0 左 / 1 中 / 2 右 / 3 灯带。</summary>
    public void SetZoneColor(int zone, RgbColor color)
    {
        Ensure3Zone();
        _transport.SetData(SetDchuLedCommand, DchuZoneProtocol.PackZoneColorArgs(zone, color));
    }

    /// <summary>设置键盘亮度（0xF4000000 | level 原始字节直传）。</summary>
    public void SetZoneBrightness(byte level)
    {
        Ensure3Zone();
        _transport.SetData(SetDchuLedCommand, DchuZoneProtocol.PackZoneBrightnessArgs(level));
    }

    /// <summary>
    /// 应用静态三区状态（时序见第九节 9.6）：0x10000000 → 各区颜色（zone 升序）→ 亮度。
    /// 灯带是否入列由调用方传入的 zones 决定（依据 <see cref="LightbarDetector"/>）。
    /// </summary>
    public void ApplyZoneStatic(IReadOnlyList<(int zone, RgbColor color)> zones, byte level)
    {
        Ensure3Zone();
        foreach (var args in DchuZoneProtocol.BuildZoneStaticSequence(zones, level))
        {
            _transport.SetData(SetDchuLedCommand, args);
        }
    }

    private void Ensure3Zone()
    {
        if (!Has3ZoneKeyboard)
        {
            throw new NotSupportedException(
                "未检测到三区 RGB 键盘（GET_BIOS_FEATURES_1 未置 0x00400000 位）；" +
                "分区命令已拒绝下发。单分区路径不受影响，请继续使用 SetColor。");
        }
    }
}
