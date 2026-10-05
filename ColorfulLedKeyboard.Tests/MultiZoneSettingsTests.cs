using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Tests;

/// <summary>
/// 多分区（实验）设置测试：归一化补齐/类型收敛/深克隆，以及与 KeyboardSettings 的接线。
/// 命令下发侧的黄金向量与门控见 WorkerMultiZoneTests。
/// </summary>
public sealed class MultiZoneSettingsTests
{
    [Fact]
    public void Normalize_PadsToFourZones_WithDistinctDefaultColors()
    {
        var multi = new MultiZoneSettings().Normalize();

        Assert.Equal(4, multi.Zones.Count);
        Assert.Equal(EffectType.Static, multi.Zones[0].Type);
        Assert.Equal("#FF0000", multi.Zones[0].Color);
        Assert.Equal("#00FF00", multi.Zones[1].Color);
        Assert.Equal("#0080FF", multi.Zones[2].Color);
        Assert.Equal("#00FFFF", multi.Zones[3].Color);
        Assert.False(multi.IncludeLightbar);
    }

    [Fact]
    public void Normalize_KeepsLoopingTypes_AndCollapsesLegacyValue()
    {
        var multi = new MultiZoneSettings
        {
            Zones =
            [
                new LightingEffectSettings { Type = EffectType.Sequence },
                new LightingEffectSettings { Type = EffectType.Pulse },
                new LightingEffectSettings { Type = EffectType.Heartbeat },
                new LightingEffectSettings { Type = (EffectType)5 }, // 已废弃值
            ]
        }.Normalize();

        Assert.Equal(EffectType.Sequence, multi.Zones[0].Type);
        Assert.Equal(EffectType.Pulse, multi.Zones[1].Type);
        Assert.Equal(EffectType.Heartbeat, multi.Zones[2].Type);
        Assert.Equal(EffectType.Static, multi.Zones[3].Type); // 废弃值收敛回固定颜色
    }

    [Fact]
    public void Normalize_KeepsSupportedTypes_AndColors()
    {
        var multi = new MultiZoneSettings
        {
            Zones =
            [
                new LightingEffectSettings { Type = EffectType.Breathing, Color = "#FF00FF" },
                new LightingEffectSettings { Type = EffectType.Rainbow },
                new LightingEffectSettings { Type = EffectType.Off },
                new LightingEffectSettings { Type = EffectType.Static, Color = "#123456" },
            ]
        }.Normalize();

        Assert.Equal(EffectType.Breathing, multi.Zones[0].Type);
        Assert.Equal("#FF00FF", multi.Zones[0].Color);
        Assert.Equal(EffectType.Rainbow, multi.Zones[1].Type);
        Assert.Equal(EffectType.Off, multi.Zones[2].Type);
        Assert.Equal("#123456", multi.Zones[3].Color);
    }

    [Fact]
    public void KeyboardSettings_Normalize_PadsMissingMultiZone()
    {
        var settings = new KeyboardSettings();
        settings.MultiZone = new MultiZoneSettings { Zones = [] };
        settings.Normalize();

        Assert.Equal(4, settings.MultiZone.Zones.Count);
        Assert.Equal(OperatingMode.Lighting, settings.OperatingMode); // 默认模式不变（单分区用户零变化）
    }

    [Fact]
    public void KeyboardSettings_CloneForRuntime_DeepClonesZones()
    {
        var settings = new KeyboardSettings();
        settings.Normalize();

        var clone = settings.CloneForRuntime();
        clone.MultiZone.Zones[0].Color = "#123456";
        clone.MultiZone.IncludeLightbar = true;

        Assert.NotEqual(clone.MultiZone.Zones[0].Color, settings.MultiZone.Zones[0].Color);
        Assert.Equal("#FF0000", settings.MultiZone.Zones[0].Color);
        Assert.False(settings.MultiZone.IncludeLightbar);
    }

    [Fact]
    public void SettingsPathRedirect_SavesLocally_NeverThroughProductionIpc()
    {
        // 隔离泄漏回归测试：重定向激活时保存必须直写隔离文件（LOCALAPPDATA 可写），
        // 绝不回退生产 IPC 管道（否则 MultiZone 设置会被生产服务判损坏并回滚）
        var tempDir = Path.Combine(Path.GetTempPath(), "ClevoLEDSimTest-" + Guid.NewGuid().ToString("N"));
        var tempSettings = Path.Combine(tempDir, "settings.json");
        Environment.SetEnvironmentVariable(AppPaths.SettingsPathEnvironmentVariable, tempSettings);
        try
        {
            var store = new SettingsStore();
            store.Save(new KeyboardSettings { OperatingMode = OperatingMode.MultiZone });

            Assert.True(File.Exists(tempSettings), "redirected save should write the isolated file");
            var loaded = store.Load();
            Assert.Equal(OperatingMode.MultiZone, loaded.OperatingMode);
            Assert.Equal(Path.Combine(tempDir, "multizone-status.json"), AppPaths.MultiZoneStatusPath);
            Assert.Equal(Path.Combine(tempDir, "typing-pulse.json"), AppPaths.TypingPulseStatePath);
            Assert.Equal(Path.Combine(tempDir, "audio-applications.json"), AppPaths.AudioApplicationsStatePath);
            Assert.Equal(Path.Combine(tempDir, "automation-status.json"), AppPaths.AutomationStatusPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.SettingsPathEnvironmentVariable, null);
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Normalize_KeepsOnlyFirstCooperativeZone()
    {
        // 协同确定性：第二处协同（及灯带上的）收敛回固定颜色——Worker 取第一个协同区做基准，
        // 其余协同配置会被静默丢弃，Normalize 先行收敛保证"所见即所得"
        var multi = new MultiZoneSettings
        {
            Zones =
            [
                new LightingEffectSettings { Type = EffectType.RelayFlow, Color = "#FF0000" },
                new LightingEffectSettings { Type = EffectType.AmbientGradient, Color = "#00FF00" },
                new LightingEffectSettings { Type = EffectType.Static },
                new LightingEffectSettings { Type = EffectType.Static },
            ]
        }.Normalize();

        Assert.Equal(EffectType.RelayFlow, multi.Zones[0].Type);
        Assert.Equal(EffectType.Static, multi.Zones[1].Type); // 第二处协同收敛
    }

    [Fact]
    public void Layout_DefaultsToZones3_AndRoundTripsThroughClone()
    {
        var settings = new KeyboardSettings();
        settings.Normalize();
        Assert.Equal(MultiZoneLayout.Zones3, settings.MultiZone.Layout);

        settings.MultiZone.Layout = MultiZoneLayout.SingleMerged;
        var clone = settings.CloneForRuntime();
        Assert.Equal(MultiZoneLayout.SingleMerged, clone.MultiZone.Layout);
    }

    [Fact]
    public void MultiZoneModeValue_RoundTripsThroughSettingsJson()
    {
        // 旧服务（主仓库）读到 OperatingMode=2 会按未知值回退 Lighting（其 Normalize 的
        // Enum.IsDefined 分支），灯效参数保持不变——与新服务共享 settings.json 是安全的。
        var json = System.Text.Json.JsonSerializer.Serialize(new KeyboardSettings
        {
            OperatingMode = OperatingMode.MultiZone
        });
        var restored = System.Text.Json.JsonSerializer.Deserialize<KeyboardSettings>(json)!.Normalize();

        Assert.Equal(OperatingMode.MultiZone, restored.OperatingMode);
        Assert.Contains("OperatingMode", json);
        Assert.Contains("2", json);
    }
}
