namespace ColorfulLedKeyboard.Core;

/// <summary>
/// DCHU 传输抽象：隔离 InsydeDCHU.dll 的 P/Invoke，使能力探测与三区/灯带命令
/// 可被 <see cref="FakeDchuTransport"/>（测试/模拟器）在进程内验证——任何测试与
/// 模拟器会话都不触发真实 EC 写入。
/// </summary>
public interface IDchuTransport
{
    /// <summary>下发 4 字节写命令：args 以 int32 小端写入 buffer 后调用 SetDCHU_Data(command, buffer, 4)。</summary>
    void SetData(int command, int args);

    /// <summary>读整数命令（如 GET_BIOS_FEATURES_1 = 0x52）。驱动缺失/命令不支持时抛异常或返回 0x80000002。</summary>
    int GetInteger(int command);
}
