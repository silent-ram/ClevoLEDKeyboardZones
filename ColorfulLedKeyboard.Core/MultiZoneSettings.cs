namespace ColorfulLedKeyboard.Core;

/// <summary>多分区渲染布局。</summary>
public enum MultiZoneLayout
{
    /// <summary>三区 + 灯带按区独立渲染（面向真三区机型；服务端按能力位门控，亮度走 0xF4）。</summary>
    Zones3 = 0,

    /// <summary>单区合并：整块键盘一块灯，仅左分区配置生效，走与灯效模式完全相同的单区路径
    /// （SetColor 三槽位同色写，单分区用户长期验证过）——无闪色、亮度为软件缩放、零新增操作码，
    /// 单分区机型友好。</summary>
    SingleMerged = 1
}

/// <summary>
/// 多分区（实验）配置：左/中/右/灯带四个分区各自的灯效 + 灯带下发开关。
///
/// <para>分区命令是否真的下发由 Worker 的能力位门控决定（GET_BIOS_FEATURES_1 的 0x00400000 位，
/// 未命中回退普通灯效管线）；灯带（zone 3）不做机型检测，是否下发完全由
/// <see cref="IncludeLightbar"/> 手动开关（文档 9.4 设计决策：无灯带机型上该命令行为未知）。</para>
///
/// <para>每区灯效复用 <see cref="LightingEffectSettings"/>（UI v1 仅暴露 固定颜色/单色呼吸/
/// RGB 循环/关闭 四类，归一化时把其他类型收敛回固定颜色），为将来放开完整灯效编辑留好存储。</para>
/// </summary>
public sealed class MultiZoneSettings
{
    public const int ZoneCount = 4;

    /// <summary>分区灯效，索引即 zone：0 左 / 1 中 / 2 右 / 3 灯带。</summary>
    public List<LightingEffectSettings> Zones { get; set; } = [];

    /// <summary>是否向 zone 3（灯带）下发命令。不做机型检测，由用户自行确认机型支持。</summary>
    public bool IncludeLightbar { get; set; }

    /// <summary>渲染布局：三区独立（真三区机型）或单区合并（单分区机型）。</summary>
    public MultiZoneLayout Layout { get; set; } = MultiZoneLayout.Zones3;

    public static string ZoneName(int zone) => zone switch
    {
        0 => "左分区",
        1 => "中分区",
        2 => "右分区",
        3 => "灯带",
        _ => $"分区 {zone}"
    };

    public MultiZoneSettings Normalize()
    {
        Zones ??= [];
        while (Zones.Count < ZoneCount)
        {
            Zones.Add(CreateDefaultZone(Zones.Count));
        }

        if (Zones.Count > ZoneCount)
        {
            Zones.RemoveRange(ZoneCount, Zones.Count - ZoneCount);
        }

        if (!Enum.IsDefined(Layout))
        {
            Layout = MultiZoneLayout.Zones3;
        }

        for (var zone = 0; zone < ZoneCount; zone++)
        {
            var effect = Zones[zone] ?? CreateDefaultZone(zone);
            if (!Enum.IsDefined(effect.Type) ||
                effect.Type is not (EffectType.Off or EffectType.Static or EffectType.Breathing or EffectType.Rainbow))
            {
                // UI 之外的类型（循环呼吸/脉冲/心跳等）v1 不支持按区配置，收敛回固定颜色
                effect.Type = EffectType.Static;
            }

            effect.Normalize();
            Zones[zone] = effect;
        }

        return this;
    }

    /// <summary>出厂默认：三区基色（红/绿/蓝）+ 青色灯带，一开机就能看出分区差异。</summary>
    public static LightingEffectSettings CreateDefaultZone(int zone)
    {
        var effect = EffectPresetSettings.CreateSoftwareDefault(EffectType.Static);
        effect.Color = zone switch
        {
            0 => "#FF0000",
            1 => "#00FF00",
            2 => "#0080FF",
            _ => "#00FFFF"
        };
        return effect.Normalize();
    }
}
