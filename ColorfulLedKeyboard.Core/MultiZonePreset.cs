namespace ColorfulLedKeyboard.Core;

/// <summary>多分区整套配置的命名预设。</summary>
public sealed class MultiZonePreset
{
    public string Name { get; set; } = "";

    public MultiZoneLayout Layout { get; set; } = MultiZoneLayout.Zones3;

    public bool IncludeLightbar { get; set; }

    public List<LightingEffectSettings> Zones { get; set; } = [];

    public MultiZonePreset Normalize()
    {
        Name = Name.Trim();
        if (!Enum.IsDefined(Layout))
        {
            Layout = MultiZoneLayout.Zones3;
        }

        Zones ??= [];
        while (Zones.Count < MultiZoneSettings.ZoneCount)
        {
            Zones.Add(MultiZoneSettings.CreateDefaultZone(Zones.Count));
        }

        if (Zones.Count > MultiZoneSettings.ZoneCount)
        {
            Zones.RemoveRange(MultiZoneSettings.ZoneCount, Zones.Count - MultiZoneSettings.ZoneCount);
        }

        for (var zone = 0; zone < MultiZoneSettings.ZoneCount; zone++)
        {
            var effect = Zones[zone] ?? MultiZoneSettings.CreateDefaultZone(zone);
            if (!Enum.IsDefined(effect.Type))
            {
                effect.Type = EffectType.Static;
            }

            Zones[zone] = effect.Normalize();
        }

        return this;
    }
}
