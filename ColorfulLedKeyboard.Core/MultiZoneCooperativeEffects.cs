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
                // 接力流动：同一条时间轴，左→中→右依次相位偏移 1/3 周期，灯带偏移 1/2。
                // 序列 ≥2 色：颜色列表随时间轴循环流过四区（SampleStops 循环插值）；
                // 空列表/单色：按基色色相做全周摆动（锚点色相 = 波形中心）。
                var period = Math.Clamp(effect.PeriodMs, 300, 30000);
                var stops = effect.Sequence.Count > 0
                    ? effect.Sequence.Select(item => RgbColor.FromHex(item.Color)).ToList()
                    : new List<RgbColor>();
                var phase = elapsedMs % period / (double)period; // 0..1
                for (var zone = 0; zone < 3; zone++)
                {
                    var zonePhase = (phase + zone / 3d) % 1d;
                    var wave = 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(zonePhase * Math.PI * 2)); // 亮度波：接力感
                    result[zone] = stops.Count >= 2
                        ? ScaleValue(SampleStops(stops, zonePhase), wave)
                        : ScaleValue(RgbColor.FromHsv(HueOf(baseColor) + (zonePhase - 0.5) * 360, 1, 1), wave);
                }

                if (includeLightbar)
                {
                    var barPhase = (phase + 0.5) % 1d;
                    var barWave = 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos(barPhase * Math.PI * 2));
                    result[3] = stops.Count >= 2
                        ? ScaleValue(SampleStops(stops, barPhase), barWave)
                        : ScaleValue(RgbColor.FromHsv(HueOf(baseColor) + 180, 1, 1), barWave);
                }

                break;
            }
            case EffectType.AmbientGradient:
            {
                // 氛围渐变：颜色列表映射到键盘横向——左=首色、右=末色、中间=中间停靠点
                // （3 色及以上取中间停靠点，2 色取插值）。空序列在 Normalize 时已被回填，
                // 此 default 分支仅为直达 API 的防御路径（基色/补色）；
                // 整体叠加缓慢呼吸（PeriodMs 为呼吸周期，下限与 Normalize 的 300ms 一致）；灯带取中间色。
                var period = Math.Clamp(effect.PeriodMs, 300, 30000);
                var stops = effect.Sequence.Count > 0
                    ? effect.Sequence.Select(item => RgbColor.FromHex(item.Color)).ToList()
                    : new List<RgbColor>();
                RgbColor left;
                RgbColor middle;
                RgbColor right;
                switch (stops.Count)
                {
                    case 1:
                        left = stops[0];
                        right = stops[0];
                        middle = stops[0];
                        break;
                    case 2:
                        left = stops[0];
                        right = stops[1];
                        middle = RgbColor.Lerp(left, right, 0.5);
                        break;
                    case >= 3:
                        left = stops[0];
                        middle = stops[stops.Count / 2];
                        right = stops[^1];
                        break;
                    default:
                        left = baseColor;
                        right = RgbColor.FromHsv(HueOf(baseColor) + 180, 1, 1);
                        middle = RgbColor.Lerp(left, right, 0.5);
                        break;
                }

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

    /// <summary>沿停靠点序列循环采样：t∈[0,1) 映射到首尾相接的相邻停靠点插值。</summary>
    private static RgbColor SampleStops(IReadOnlyList<RgbColor> stops, double t)
    {
        t -= Math.Floor(t);
        var scaled = t * stops.Count;
        var index = (int)Math.Floor(scaled);
        var fraction = scaled - index;
        var a = stops[index % stops.Count];
        var b = stops[(index + 1) % stops.Count];
        return RgbColor.Lerp(a, b, fraction);
    }

    /// <summary>RGB 颜色的色相（0..360）。</summary>
    public static double HueOf(RgbColor color)
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
