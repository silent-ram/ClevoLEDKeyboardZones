using ColorfulLedKeyboard.Core;
using ColorfulLedKeyboard.Service;

namespace ColorfulLedKeyboard.Tests;

/// <summary>
/// 多分区模式（实验）的命令下发测试：门控（能力位未命中即拒绝，零下发）与
/// 分区命令黄金向量（BRG 编码，0xF0/0xF1/0xF2[/0xF3] 顺序下发、绝无单区 SetColor）。
/// 灯带是否入列由调用方按 IncludeLightbar 组装帧——本测试覆盖两种帧形态。
/// </summary>
public sealed class WorkerMultiZoneTests
{
    private static readonly RgbColor Red = new(255, 0, 0);
    private static readonly RgbColor Green = new(0, 255, 0);
    private static readonly RgbColor Blue = new(0, 0, 255);
    private static readonly RgbColor Cyan = new(0, 255, 255);

    [Fact]
    public void RenderMultiZoneFrame_WithCapability_SendsZoneColorsInOrder()
    {
        var fake = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };

        Worker.RenderMultiZoneFrame(new DchuKeyboardDevice(fake),
        [
            (0, Red),
            (1, Green),
            (2, Blue),
        ]);

        Assert.Equal(
        [
            (DchuZoneProtocol.SetDchuLedCommand, DchuZoneProtocol.PackZoneColorArgs(0, Red)),
            (DchuZoneProtocol.SetDchuLedCommand, DchuZoneProtocol.PackZoneColorArgs(1, Green)),
            (DchuZoneProtocol.SetDchuLedCommand, DchuZoneProtocol.PackZoneColorArgs(2, Blue)),
        ], fake.Sent);
    }

    [Fact]
    public void RenderMultiZoneFrame_WithLightbar_IncludesZone3()
    {
        var fake = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };

        Worker.RenderMultiZoneFrame(new DchuKeyboardDevice(fake),
        [
            (0, Red),
            (1, Green),
            (2, Blue),
            (3, Cyan),
        ]);

        Assert.Equal(
        [
            (DchuZoneProtocol.SetDchuLedCommand, DchuZoneProtocol.PackZoneColorArgs(3, Cyan)),
        ], fake.Sent.Skip(3).ToList());
        Assert.Equal(4, fake.Sent.Count);
    }

    [Fact]
    public void RenderMultiZoneFrame_WithoutCapability_ThrowsAndSendsNothing()
    {
        // 门控语义：能力位未命中即拒绝（NotSupportedException），绝不静默下发——
        // Worker 的多分区循环在进入渲染前先检查 Has3ZoneKeyboard，这里是最后一道防线
        var fake = new FakeDchuTransport { Features1Result = FakeDchuTransport.DefaultUnsupportedResult };

        Assert.Throws<NotSupportedException>(() =>
            Worker.RenderMultiZoneFrame(new DchuKeyboardDevice(fake), [(0, Red), (1, Green), (2, Blue)]));
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public void PerZoneGenerators_RenderIndependentEffects_OnSharedTimeline()
    {
        // 左：固定红；中：固定绿；右：关闭 —— 同一时刻三区互不相同，关闭区为黑
        var generators = new[]
        {
            new LightingFrameGenerator(new LightingEffectSettings { Type = EffectType.Static, Color = "#FF0000" }),
            new LightingFrameGenerator(new LightingEffectSettings { Type = EffectType.Static, Color = "#00FF00" }),
            new LightingFrameGenerator(new LightingEffectSettings { Type = EffectType.Off }),
        };
        var fake = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };

        var frame = new List<(int Zone, RgbColor Color)>();
        for (var zone = 0; zone < 3; zone++)
        {
            var color = generators[zone].NextAtElapsed(70, elapsedMs: 1234);
            frame.Add((zone, color));
        }
        Worker.RenderMultiZoneFrame(new DchuKeyboardDevice(fake), frame);

        var sent = fake.Sent.Select(entry => entry.Args).ToList();
        Assert.Equal(DchuZoneProtocol.PackZoneColorArgs(0, new RgbColor(178, 0, 0)), sent[0]); // 70% 缩放
        Assert.Equal(DchuZoneProtocol.PackZoneColorArgs(1, new RgbColor(0, 178, 0)), sent[1]);
        Assert.Equal(unchecked((int)0xF2000000u), sent[2]); // 关闭区 = 黑
    }
}
