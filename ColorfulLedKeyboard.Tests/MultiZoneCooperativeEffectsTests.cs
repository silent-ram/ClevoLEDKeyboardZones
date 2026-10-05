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
    public void RelayFlow_ColorList_CyclesThroughStops()
    {
        var effect = new LightingEffectSettings
        {
            Type = EffectType.RelayFlow,
            Color = "#FF0000",
            PeriodMs = 3000,
            Sequence = [new SequenceColor { Color = "#FF0000" }, new SequenceColor { Color = "#0000FF" }],
        };

        // 相位 0：左区采样到列表首色（红），叠加亮度波谷（0.35）→ (89,0,0)
        var frame = MultiZoneCooperativeEffects.ComputeFrame(effect, 0, includeLightbar: true);
        Assert.Equal(89, frame[0]!.Value.R);
        Assert.Equal(0, frame[0]!.Value.G);
        Assert.Equal(0, frame[0]!.Value.B);

        // 左→中相位推进 1/3：中区颜色是红蓝插值（既非纯红也非纯蓝）
        var mid = frame[1]!.Value;
        Assert.True(mid.R > 0 && mid.R < 255 && mid.B > 0 && mid.B < 255,
            $"middle should be an interpolated blend, got ({mid.R},{mid.G},{mid.B})");

        // 灯带相位偏移 1/2：不同于左区
        Assert.NotEqual(frame[0]!.Value, frame[3]!.Value);
    }

    [Fact]
    public void RelayFlow_SingleColor_FallsBackToHueSweep()
    {
        var effect = new LightingEffectSettings
        {
            Type = EffectType.RelayFlow,
            Color = "#FF0000",
            PeriodMs = 3000,
            Sequence = [new SequenceColor { Color = "#FF0000" }], // 单色：回退色相全周摆动
        };

        // 半周期处：中区相位 1/3+1/2=5/6 → 色相 -300°→60°（黄区），非红
        var frame = MultiZoneCooperativeEffects.ComputeFrame(effect, 1500, includeLightbar: false);
        var middle = frame[1]!.Value;
        Assert.True(middle.G > 100, $"hue sweep expected, got ({middle.R},{middle.G},{middle.B})");
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
