using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Tests;

/// <summary>
/// 多分区协同效果（RelayFlow 接力流动 / AmbientGradient 氛围渐变）纯函数测试：
/// 四区关系（相位推进/渐变插值/灯带语义）与时间连续性。
/// </summary>
public sealed class MultiZoneCooperativeEffectsTests
{
    private static readonly LightingEffectSettings Relay = new()
    {
        Type = EffectType.RelayFlow,
        Color = "#FF0000", // 色相锚点 0°
        PeriodMs = 3000,
    };

    private static readonly LightingEffectSettings Gradient = new()
    {
        Type = EffectType.AmbientGradient,
        Color = "#FF0000",
        PeriodMs = 6000,
        Sequence = [new SequenceColor { Color = "#FF0000" }, new SequenceColor { Color = "#0000FF" }], // 左红右蓝
    };

    [Fact]
    public void RelayFlow_FourZonesAdvanceInPhase()
    {
        var frame = MultiZoneCooperativeEffects.ComputeFrame(Relay, elapsedMs: 0, includeLightbar: true);

        Assert.All(frame.Take(3), zone => Assert.NotNull(zone));
        Assert.NotNull(frame[3]); // includeLightbar=true → 灯带有值
    }

    [Fact]
    public void RelayFlow_LightbarExcluded_WhenNotIncluded()
    {
        var frame = MultiZoneCooperativeEffects.ComputeFrame(Relay, 100, includeLightbar: false);
        Assert.Null(frame[3]);
    }

    [Fact]
    public void RelayFlow_ZonesDiffer_AtSameInstant()
    {
        // 同一时刻三区颜色互不相同（相位推进的可见性保证）
        var frame = MultiZoneCooperativeEffects.ComputeFrame(Relay, 1200, includeLightbar: true);
        Assert.NotEqual(frame[0], frame[1]);
        Assert.NotEqual(frame[1], frame[2]);
        Assert.NotEqual(frame[0], frame[2]);
    }

    [Fact]
    public void RelayFlow_IsContinuous_AcrossFrames()
    {
        // 相邻帧（40ms）颜色变化应平滑（每通道跳动有界，接力流动非硬切）
        var a = MultiZoneCooperativeEffects.ComputeFrame(Relay, 1000, includeLightbar: false);
        var b = MultiZoneCooperativeEffects.ComputeFrame(Relay, 1040, includeLightbar: false);
        for (var zone = 0; zone < 3; zone++)
        {
            var ca = a[zone]!.Value;
            var cb = b[zone]!.Value;
            Assert.True(Math.Abs(ca.R - cb.R) <= 40 && Math.Abs(ca.G - cb.G) <= 40 && Math.Abs(ca.B - cb.B) <= 40,
                $"zone{zone} jumped: ({ca.R},{ca.G},{ca.B}) -> ({cb.R},{cb.G},{cb.B})");
        }
    }

    [Fact]
    public void AmbientGradient_LeftIsFirstStop_RightIsLastStop()
    {
        // 呼吸中点（period/2 处 factor=1）时：左=序列首色、右=序列末色
        var frame = MultiZoneCooperativeEffects.ComputeFrame(Gradient, elapsedMs: 3000, includeLightbar: false);
        Assert.Equal(new RgbColor(255, 0, 0), frame[0]!.Value);
        Assert.Equal(new RgbColor(0, 0, 255), frame[2]!.Value);
    }

    [Fact]
    public void AmbientGradient_ThreeStops_MiddleUsesMiddleStop()
    {
        var effect = new LightingEffectSettings
        {
            Type = EffectType.AmbientGradient,
            Color = "#FF0000",
            PeriodMs = 6000,
            Sequence =
            [
                new SequenceColor { Color = "#FF0000" },
                new SequenceColor { Color = "#00FF00" },
                new SequenceColor { Color = "#0000FF" },
            ],
        };
        var frame = MultiZoneCooperativeEffects.ComputeFrame(effect, 3000, includeLightbar: false);
        Assert.Equal(new RgbColor(255, 0, 0), frame[0]!.Value);
        Assert.Equal(new RgbColor(0, 255, 0), frame[1]!.Value); // 中区取中间停靠点（非插值）
        Assert.Equal(new RgbColor(0, 0, 255), frame[2]!.Value);
    }

    [Fact]
    public void AmbientGradient_SingleStop_AppliesEverywhere()
    {
        var effect = new LightingEffectSettings
        {
            Type = EffectType.AmbientGradient,
            Color = "#FF0000",
            PeriodMs = 6000,
            Sequence = [new SequenceColor { Color = "#00FF00" }],
        };
        var frame = MultiZoneCooperativeEffects.ComputeFrame(effect, 3000, includeLightbar: false);
        Assert.Equal(new RgbColor(0, 255, 0), frame[0]!.Value);
        Assert.Equal(new RgbColor(0, 255, 0), frame[2]!.Value);
    }

    [Fact]
    public void AmbientGradient_MiddleIsLerpOfEnds()
    {
        var frame = MultiZoneCooperativeEffects.ComputeFrame(Gradient, 3000, includeLightbar: false);
        var left = frame[0]!.Value;
        var right = frame[2]!.Value;
        var middle = frame[1]!.Value;
        var expected = RgbColor.Lerp(left, right, 0.5);
        Assert.Equal(expected, middle);
    }

    [Fact]
    public void NonCooperativeType_ReturnsNulls()
    {
        var effect = new LightingEffectSettings { Type = EffectType.Breathing, Color = "#FF0000" };
        var frame = MultiZoneCooperativeEffects.ComputeFrame(effect, 100, includeLightbar: true);
        Assert.All(frame, zone => Assert.Null(zone));
    }
}
