using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Simulator;

/// <summary>
/// 模拟器本地的假传输：记录全部命令并以事件实时回推渲染。
/// 与测试工程的 FakeDchuTransport 同构，但独立维护（模拟器不引用测试程序集）。
/// </summary>
public sealed class FakeDchuTransport : IDchuTransport
{
    public List<(int Command, int Args)> Sent { get; } = [];

    /// <summary>GET_BIOS_FEATURES_1(0x52) 模拟返回值；视图切换时由界面改写。</summary>
    public int Features1Result { get; set; } = unchecked((int)0x80000002u);

    public event Action<int, int>? CommandSent;

    public void SetData(int command, int args)
    {
        Sent.Add((command, args));
        CommandSent?.Invoke(command, args);
    }

    public int GetInteger(int command) => Features1Result;
}
