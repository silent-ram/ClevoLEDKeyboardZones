namespace ColorfulLedKeyboard.Core;

/// <summary>
/// 多分区协同效果生成器（RelayFlow 接力流动 / AmbientGradient 氛围渐变）。
/// 与单区效果不同：一个协同效果同时决定四个区的颜色（共享时间轴、区间关系固定），
/// 因此不按区独立生成，而是整帧输出四区颜色。
/// 颜色基准取"配置该效果的分区"的 Effect.Color / 参数（配在哪个区效果一致）。
/// </summary>
public static class MultiZoneCooperativeEffects
{
    /// <summary>
    /// 计算协同效果在指定时刻的四区颜色。
    /// </summary>
    /// <param name="effect">协同效果配置（Type 须为 RelayFlow/AmbientGradient）。</param>
    /// <param name="elapsedMs">共享时间轴（毫秒）。</param>
    /// <param name="includeLightbar">是否计算灯带（false 时 zone3 返回 null）。</param>
    public static RgbColor?[] ComputeFrame(LightingEffectSettings effect, double elapsedMs, bool includeLightbar)
    {
        var result = new RgbColor?[4];
        var baseColor = RgbColor.FromHex(effect.Color);
        switch (effect.Type)
        {
            case EffectType.RelayFlow:
            {
                // 接力流动：同一条色相时间轴，左→中→右依次相位偏移 1/3 周期；
                // 基色作为色相锚点做全彩循环（锚点色相 = 波形中心），灯带取补色。
                var period = Math.Clamp(effect.PeriodMs, 300, 30000);
                var baseHue = ToHue(baseColor);
                var phase = elapsedMs % period / (double)period; // 0..1
                for (var zone = 0; zone < 3; zone++)
                {
                    var zonePhase = (phase + zone / 3d) % 1d;
                    var hue = baseHue + (zonePhase - 0.5) * 360; // 锚点居中摆动
                    var wave = 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(zonePhase * Math.PI * 2)); // 亮度波：接力感
                    result[zone] = ScaleValue(RgbColor.FromHsv(hue, 1, 1), wave);
                }

                if (includeLightbar)
                {
                    var barPhase = (phase + 0.5) % 1d; // 灯带与中区互补相位
                    var barWave = 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(barPhase * Math.PI * 2));
                    result[3] = ScaleValue(RgbColor.FromHsv(baseHue + 180, 1, 1), barWave);
                }

                break;
            }
            case EffectType.AmbientGradient:
            {
                // 氛围渐变：左=基色（暖端），右=辅助色（冷端，取 Sequence[0]，缺省基色补 180°），
                // 中=两端中点插值；整体叠加缓慢呼吸（PeriodMs 为呼吸周期）；灯带取中间色。
                var period = Math.Clamp(effect.PeriodMs, 1000, 30000);
                var left = baseColor;
                var right = effect.Sequence.Count > 0 ? RgbColor.FromHex(effect.Sequence[0].Color) : RgbColor.FromHsv(ToHue(baseColor) + 180, 1, 1);
                var middle = RgbColor.Lerp(left, right, 0.5);
                var breathe = 0.55 + 0.45 * (0.5 - 0.5 * Math.Cos(elapsedMs % period / (double)period * Math.PI * 2));
                result[0] = ScaleValue(left, breathe);
                result[1] = ScaleValue(middle, breathe);
                result[2] = ScaleValue(right, breathe);
                if (includeLightbar)
                {
                    result[3] = ScaleValue(middle, breathe * 0.9);
                }

                break;
            }
            default:
                // 非协同类型交由调用方按区独立生成；这里防御性返回 null
                break;
        }

        return result;
    }

    public static bool IsCooperative(EffectType type) =>
        type is EffectType.RelayFlow or EffectType.AmbientGradient;

    private static double ToHue(RgbColor color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta == 0)
        {
            return 0;
        }

        double hue;
        if (max == r)
        {
            hue = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            hue = 60 * ((b - r) / delta + 2);
        }
        else
        {
            hue = 60 * ((r - g) / delta + 4);
        }

        return hue < 0 ? hue + 360 : hue;
    }

    private static RgbColor ScaleValue(RgbColor color, double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new RgbColor(
            (byte)Math.Round(color.R * factor),
            (byte)Math.Round(color.G * factor),
            (byte)Math.Round(color.B * factor));
    }
}
