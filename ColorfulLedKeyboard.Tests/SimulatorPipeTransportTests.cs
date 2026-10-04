using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Tests;

/// <summary>
/// SimulatorPipeTransport（外接模式"虚拟驱动"）测试。
///
/// 刻意只测线路协议纯函数与无 I/O 的降级语义，不建真实管道回环：
/// 本仓库开发环境（代理派生进程树）会拦截"同源兄弟进程"间的命名管道数据流
/// （连接成功但内核 WriteFile 永不返回，与既有 ServiceIpcTests 只测纯解析的理由相同）。
/// 活体端到端由 functional-test.ps1 的外接控制场景覆盖：模拟器经 WMI 独立派生后，
/// 用真客户端 harness 发命令并断言渲染（见 docs/simulator/external-control.md）。
/// </summary>
public sealed class SimulatorPipeTransportTests : IDisposable
{
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SimulatorPipeTransport.EnableEnvironmentVariable, null);
    }

    // ---- 线路协议格式化 ----

    [Theory]
    [InlineData(0x67, 0xF2FFFFFF, "W 67 F2FFFFFF")]
    [InlineData(0x67, 0x10000000, "W 67 10000000")]
    [InlineData(0x67, 0xF000FF00, "W 67 F000FF00")]
    [InlineData(0x52, 0, "Q 52")]
    public void FormatLines_ProduceCanonicalWireFormat(int command, long args, string expected)
    {
        if (command == 0x52)
        {
            Assert.Equal(expected, SimulatorPipeTransport.FormatQueryLine(command));
        }
        else
        {
            Assert.Equal(expected, SimulatorPipeTransport.FormatWriteLine(command, unchecked((int)args)));
        }
    }

    [Fact]
    public void FormatReplies_ProduceCanonicalWireFormat()
    {
        Assert.Equal("V 00400000", SimulatorPipeTransport.FormatValueReply(unchecked((int)0x00400000u)));
        Assert.Equal("V 80000002", SimulatorPipeTransport.FormatValueReply(unchecked((int)0x80000002u)));
        Assert.Equal("OK", SimulatorPipeTransport.FormatOkReply());
        Assert.Equal("ERR bad request", SimulatorPipeTransport.FormatErrorReply("bad request"));
    }

    // ---- 线路协议解析 ----

    [Theory]
    [InlineData("W 67 F2FFFFFF", true, 0x67, unchecked((int)0xF2FFFFFFu))]
    [InlineData("W 67 10000000", true, 0x67, unchecked((int)0x10000000u))]
    [InlineData("W 103 f4ff", true, 0x103, unchecked((int)0xF4FFu))] // 命令/参数不定长十六进制
    [InlineData("Q 52", false, 0x52, 0)]
    [InlineData("Q 0", false, 0, 0)]
    public void TryParseRequestLine_AcceptsCanonicalLines(string line, bool expectedWrite, int expectedCommand, int expectedArgs)
    {
        var ok = SimulatorPipeTransport.TryParseRequestLine(line, out var isWrite, out var command, out var args);

        Assert.True(ok);
        Assert.Equal(expectedWrite, isWrite);
        Assert.Equal(expectedCommand, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("X 67 10000000")]
    [InlineData("W 67")]
    [InlineData("W 67 10000000 extra")]
    [InlineData("W zz 10000000")]
    [InlineData("W 67 zz")]
    [InlineData("Q zz")]
    [InlineData("OK")]
    public void TryParseRequestLine_RejectsMalformedLines(string? line)
    {
        Assert.False(SimulatorPipeTransport.TryParseRequestLine(line, out _, out _, out _));
    }

    [Theory]
    [InlineData("V 00400000", unchecked((int)0x00400000u))]
    [InlineData("V 80000002", unchecked((int)0x80000002u))]
    [InlineData("V 0", 0)]
    public void TryParseValueReply_AcceptsValueLines(string line, int expected)
    {
        Assert.True(SimulatorPipeTransport.TryParseValueReply(line, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("OK")]
    [InlineData("ERR bad request")]
    [InlineData("V")]
    [InlineData("V zz")]
    [InlineData("V 00400000 ")] // 尾随空格：严格拒绝，暴露协议错位
    public void TryParseValueReply_RejectsNonValueLines(string? line)
    {
        Assert.False(SimulatorPipeTransport.TryParseValueReply(line, out _));
    }

    // ---- 环境开关与工厂选择 ----

    [Fact]
    public void Enabled_ReflectsEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(SimulatorPipeTransport.EnableEnvironmentVariable, "1");
        Assert.True(SimulatorPipeTransport.Enabled);

        Environment.SetEnvironmentVariable(SimulatorPipeTransport.EnableEnvironmentVariable, "TRUE");
        Assert.True(SimulatorPipeTransport.Enabled);

        Environment.SetEnvironmentVariable(SimulatorPipeTransport.EnableEnvironmentVariable, "0");
        Assert.False(SimulatorPipeTransport.Enabled);

        Environment.SetEnvironmentVariable(SimulatorPipeTransport.EnableEnvironmentVariable, null);
        Assert.False(SimulatorPipeTransport.Enabled);
    }

    [Fact]
    public void CreateDefault_UsesSimulatorPipe_WhenEnvEnabled_OtherwisePInvoke()
    {
        Environment.SetEnvironmentVariable(SimulatorPipeTransport.EnableEnvironmentVariable, "true");
        var simulated = DchuKeyboardDevice.CreateDefault();
        Assert.True(simulated.UsesSimulatorPipe);

        Environment.SetEnvironmentVariable(SimulatorPipeTransport.EnableEnvironmentVariable, null);
        var production = DchuKeyboardDevice.CreateDefault();
        Assert.False(production.UsesSimulatorPipe);
    }

    // ---- 无服务端时的降级语义（连接立即失败，不涉及数据流，任意环境安全）----

    [Fact]
    public void WithoutServer_WritesDropSilently_AndReadsReturnSafeNon3Zone()
    {
        using var transport = new SimulatorPipeTransport(port: 1); // 端口 1 必然拒绝连接：快速失败路径

        transport.SetData(0x67, 0x10000000); // 不抛出即通过：效果循环节奏不受模拟器缺席影响
        Assert.Equal(unchecked((int)0x80000002u), transport.GetInteger(0x52));
        Assert.Equal(1, transport.DroppedWrites);
    }
}
