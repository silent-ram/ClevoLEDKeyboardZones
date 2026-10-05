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
    private const int GetBiosFeatures1Command = DchuZoneProtocol.GetBiosFeatures1Command; // GET_BIOS_FEATURES_1（第九节 9.9，读类，零 EC 写）
    private const int KeyboardFeatures3ZoneRgb = 0x00400000;

    /// <summary>SCMD 写命令（0x67），常量权威定义在 <see cref="DchuZoneProtocol.SetDchuLedCommand"/>。</summary>
    internal const int SetDchuLedCommand = DchuZoneProtocol.SetDchuLedCommand;

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

    /// <summary>
    /// 默认构造入口（服务使用）：环境变量 <see cref="SimulatorPipeTransport.EnableEnvironmentVariable"/>
    /// 启用时走模拟器命名管道（外接控制模式，零真实 EC 写，命令转发给虚拟键盘渲染）；
    /// 否则 P/Invoke 直通，与既有生产行为完全一致。
    /// </summary>
    public static DchuKeyboardDevice CreateDefault() =>
        SimulatorPipeTransport.Enabled
            ? new DchuKeyboardDevice(new SimulatorPipeTransport())
            : new DchuKeyboardDevice();

    /// <summary>诊断/测试：当前设备是否走模拟器管道（外接模式）。</summary>
    internal bool UsesSimulatorPipe => _transport is SimulatorPipeTransport;

    // ---- 单区路径（现有行为，逐字节保持不变）----

    /// <summary>把整块键盘面板设为指定颜色（三槽位写：Local4=0/1/2，最后一次触发 EC mode 5 应用）。</summary>
    public void SetColor(RgbColor color)
    {
        WriteSequenceSlot(color, slot: 0);
        WriteSequenceSlot(color, slot: 1);
        WriteSequenceSlot(color, slot: 2);
    }

    /// <summary>
    /// 向 SCMD 0x67 Local7=0xF 路径的指定槽位写入颜色（slot 0..2）。
    /// 编码与三区协议 PackZoneColorArgs 同一 BRG 字节序（zone0..2 等价于左/中/右分区写），
    /// 统一委托，避免两份编码漂移。
    /// </summary>
    internal static int BuildSequenceSlotArgs(RgbColor color, int slot) =>
        DchuZoneProtocol.PackZoneColorArgs(slot, color);

    private void WriteSequenceSlot(RgbColor color, int slot)
    {
        _transport.SetData(SetDchuLedCommand, BuildSequenceSlotArgs(color, slot));
    }

    /// <summary>
    /// 裸写一条 0x67 ARGS（仅供外接模拟器模式的分区渲染使用）。环境变量即门控，
    /// 不做能力位探测——模拟器按当前视图如实应答 0x52，单区视图会答"不支持"，
    /// 探测门会把外接演示整体挡死。生产路径不得调用。
    /// </summary>
    internal void WriteRawLedArgs(int args)
    {
        _transport.SetData(SetDchuLedCommand, args);
    }

    // ---- 能力探测（读类，零 EC 写）----

    /// <summary>GET_BIOS_FEATURES_1（0x52）原始返回值；调用方一般用 <see cref="Has3ZoneKeyboard"/>。</summary>
    public int GetKeyboardFeatures() => _transport.GetInteger(GetBiosFeatures1Command);

    /// <summary>
    /// 三区 RGB 键盘能力位（0x00400000）探测，结果缓存。
    /// 探测异常或命令不支持（如 0x80000002）一律按 false 处理——安全默认，回退单区路径。
    /// <para>**实测警示（P955ET1，2026-10）**：本机返回 0x04690025，该位置位但物理键盘为单分区——
    /// 能力位只是必要条件而非充分条件。因此分区 API 的运行时前提还包含：
    /// 服务效果管线不调用分区 API（仅模拟器三区视图显式调用）；详见文档第九节 9.9。</para>
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
    /// 切 CUSTOM/静态模式（0x10000000，文档 9.6 时序第 1 步）。真实三区机型上分区颜色
    /// 需在 CUSTOM 模式下才显示（clevo-xsm-wmi CUSTOM 分支同序）；门控与分区命令一致。
    /// </summary>
    public void ApplyCustomMode()
    {
        Ensure3Zone();
        _transport.SetData(SetDchuLedCommand, DchuZoneProtocol.PackCustomModeArgs());
    }

    /// <summary>
    /// 应用静态三区状态（时序见第九节 9.6）：0x10000000 → 各区颜色（zone 升序）→ 亮度。
    /// 灯带（zone 3）是否入列由调用方传入的 zones 决定，本类不做机型检测。
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
