using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Tests;

/// <summary>
/// 进程内假传输：记录全部 SetData 调用，GetInteger 可配置返回值/异常。
/// 卫生约束：任何测试不得走真实 P/Invoke 或 EC 写入，一律经本假件。
/// </summary>
public sealed class FakeDchuTransport : IDchuTransport
{
    /// <summary>GetInteger 的默认返回：0x80000002（命令不支持）——与真实驱动对未知读命令的行为一致的安全默认。</summary>
    public const int DefaultUnsupportedResult = unchecked((int)0x80000002u);

    public List<(int Command, int Args)> Sent { get; } = [];

    /// <summary>GET_BIOS_FEATURES_1(0x52) 的模拟返回值。</summary>
    public int Features1Result { get; set; } = DefaultUnsupportedResult;

    /// <summary>设置后 GetInteger 直接抛该异常（模拟驱动缺失等）。</summary>
    public Exception? GetIntegerException { get; set; }

    public int GetIntegerCallCount { get; private set; }

    /// <summary>模拟器/测试可订阅以实时渲染。</summary>
    public event Action<int, int>? CommandSent;

    public void SetData(int command, int args)
    {
        Sent.Add((command, args));
        CommandSent?.Invoke(command, args);
    }

    public int GetInteger(int command)
    {
        GetIntegerCallCount++;
        if (GetIntegerException is not null)
        {
            throw GetIntegerException;
        }

        return Features1Result;
    }
}
