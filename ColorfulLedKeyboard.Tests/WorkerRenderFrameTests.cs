using ColorfulLedKeyboard.Core;
using ColorfulLedKeyboard.Service;

namespace ColorfulLedKeyboard.Tests;

/// <summary>
/// Worker.RenderFrame 分支测试：
/// - 生产路径 = SetColor 三槽位写（与既有行为逐字节一致，绝无 0xF3/0x10000000）；
/// - 外接模拟器路径 = 三区 + 灯带直写（0xF3 参与），且不做能力位探测——
///   模拟器按当前视图如实应答 0x52，单区视图答"不支持"，探测门会把演示挡死；
///   环境变量即门控，生产永远不进该分支（文档 9.9：能力位仅必要条件）。
/// </summary>
public sealed class WorkerRenderFrameTests
{
    private static readonly RgbColor Color = new(0x12, 0x34, 0x56);

    [Fact]
    public void Production_Path_MatchesSetColorBytes_AndNeverTouchesLightbar()
    {
        var fake = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };
        var reference = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };

        Worker.RenderFrame(new DchuKeyboardDevice(fake), Color, simulatorMode: false);
        new DchuKeyboardDevice(reference).SetColor(Color);

        Assert.Equal(reference.Sent, fake.Sent);
        Assert.DoesNotContain(fake.Sent, entry => (uint)(entry.Args & 0xFF000000) == 0xF3000000u);
    }

    [Fact]
    public void Simulator_Path_WritesThreeZonesAndLightbar_WithoutCapabilityGate()
    {
        // 能力位故意给"不支持"（0x80000002）：证明外接路径不做探测门
        var fake = new FakeDchuTransport { Features1Result = FakeDchuTransport.DefaultUnsupportedResult };

        Worker.RenderFrame(new DchuKeyboardDevice(fake), Color, simulatorMode: true);

        Assert.Equal(
        [
            (DchuZoneProtocol.SetDchuLedCommand, DchuZoneProtocol.PackZoneColorArgs(0, Color)),
            (DchuZoneProtocol.SetDchuLedCommand, DchuZoneProtocol.PackZoneColorArgs(1, Color)),
            (DchuZoneProtocol.SetDchuLedCommand, DchuZoneProtocol.PackZoneColorArgs(2, Color)),
            (DchuZoneProtocol.SetDchuLedCommand, DchuZoneProtocol.PackZoneColorArgs(3, Color)),
        ], fake.Sent);
    }

    [Fact]
    public void Simulator_Path_OffFrame_SendsBlackToAllZonesIncludingLightbar()
    {
        var fake = new FakeDchuTransport { Features1Result = FakeDchuTransport.DefaultUnsupportedResult };

        Worker.RenderFrame(new DchuKeyboardDevice(fake), RgbColor.Black, simulatorMode: true);

        Assert.Equal(
        [
            unchecked((int)0xF0000000u),
            unchecked((int)0xF1000000u),
            unchecked((int)0xF2000000u),
            unchecked((int)0xF3000000u),
        ], fake.Sent.Select(entry => entry.Args));
    }
}
