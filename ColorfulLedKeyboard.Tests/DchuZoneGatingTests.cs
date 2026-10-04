using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Tests;

/// <summary>
/// 三区门控测试 —— 本实验最重要的约束："单分区用户行为零变化"的自动化保证。
///
/// 注意判据选择：任务书原文写"断言不存在 command=0x67 且 args bit28..31=0xF 的条目"，
/// 但现有单区路径 SetColor 本身就是 Local7=0xF 三槽位写（0xF0/0xF1/0xF2，协议第三/九节同族编码），
/// 该字面判据与"现有单区路径的 0x67 命令照常存在"自相矛盾、无法同时成立。
/// 因此判据修正为"三区独占操作码"：
///   0xF3xxxxxx（灯带）、0xF4xxxxxx（0xF4|level 亮度）、0x10000000（CUSTOM 模式命令）——
/// 这些操作码在现有单区管线中从未出现，出现即代表分区命令泄漏。
/// </summary>
public sealed class DchuZoneGatingTests
{
    private static bool IsLightbarArgs(int args) => (uint)(args & 0xFF000000) == 0xF3000000u;
    private static bool IsZoneBrightnessArgs(int args) => (uint)(args & 0xFF000000) == 0xF4000000u;
    private static bool IsCustomModeArgs(int args) => args == DchuZoneProtocol.PackCustomModeArgs();

    private static bool IsZoneExclusive(int args) => IsLightbarArgs(args) || IsZoneBrightnessArgs(args) || IsCustomModeArgs(args);

    private static KeyboardSettings BreathingSettings() => new()
    {
        Brightness = 70,
        Effect = new LightingEffectSettings
        {
            Type = EffectType.Breathing,
            Color = "#FF0000",
            PeriodMs = 3000,
        }
    };

    /// <summary>复刻 Worker 单区效果管线（LightingFrameGenerator.Next → SetColor），帧数固定以便逐字节比对。</summary>
    private static void RunSingleZonePipeline(DchuKeyboardDevice device, int frames = 64)
    {
        var settings = BreathingSettings();
        var generator = new LightingFrameGenerator(settings);
        for (var frame = 0; frame < frames; frame++)
        {
            var color = generator.NextAtElapsed(settings.Brightness, frame * generator.IntervalMs);
            device.SetColor(color);
        }
    }

    [Fact]
    public void WorkerPipeline_Without3ZoneBit_NeverEmitsZoneExclusiveCommands()
    {
        // 安全默认：GET_BIOS_FEATURES_1 返回 0x80000002（不支持）→ 门控按非三区
        var fake = new FakeDchuTransport();
        var device = new DchuKeyboardDevice(fake);
        Assert.False(device.Has3ZoneKeyboard);

        RunSingleZonePipeline(device);

        Assert.NotEmpty(fake.Sent);
        Assert.All(fake.Sent, entry => Assert.Equal(DchuZoneProtocol.SetDchuLedCommand, entry.Command));
        Assert.DoesNotContain(fake.Sent, entry => IsZoneExclusive(entry.Args));
    }

    [Fact]
    public void WorkerPipeline_EmittedStream_IdenticalRegardlessOfCapabilityBit()
    {
        // 同一效果管线在"无三区位"与"有三区位"两种能力下，单区输出流逐字节一致
        // ——能力位只门控三区 API，绝不改变现有路径。
        var fakeNoZone = new FakeDchuTransport { Features1Result = FakeDchuTransport.DefaultUnsupportedResult };
        var fake3Zone = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };

        RunSingleZonePipeline(new DchuKeyboardDevice(fakeNoZone));
        RunSingleZonePipeline(new DchuKeyboardDevice(fake3Zone));

        var noZoneArgs = fakeNoZone.Sent.Select(entry => (entry.Command, entry.Args)).ToList();
        var zone3Args = fake3Zone.Sent.Select(entry => (entry.Command, entry.Args)).ToList();
        Assert.Equal(noZoneArgs, zone3Args);
        Assert.All(zone3Args, entry => Assert.False(IsZoneExclusive(entry.Args)));
    }

    [Fact]
    public void WorkerPipeline_3ZoneCapability_RainbowFrames_StillNoZoneExclusiveCommands()
    {
        // 有三区位的机器上跑现有 Worker 管线（ rainbow），同样不得泄漏分区命令
        var fake = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };
        var device = new DchuKeyboardDevice(fake);
        var settings = new KeyboardSettings
        {
            Brightness = 100,
            Effect = new LightingEffectSettings { Type = EffectType.Rainbow, Step = 5 }
        };
        var generator = new LightingFrameGenerator(settings);
        for (var frame = 0; frame < 32; frame++)
        {
            device.SetColor(generator.NextAtElapsed(settings.Brightness, frame * generator.IntervalMs));
        }

        Assert.DoesNotContain(fake.Sent, entry => IsZoneExclusive(entry.Args));
        // 三槽位写照常存在（现有行为）
        Assert.Contains(fake.Sent, entry => (uint)(entry.Args & 0xFF000000) == 0xF0000000u);
        Assert.Contains(fake.Sent, entry => (uint)(entry.Args & 0xFF000000) == 0xF1000000u);
        Assert.Contains(fake.Sent, entry => (uint)(entry.Args & 0xFF000000) == 0xF2000000u);
    }

    [Fact]
    public void Has3ZoneKeyboard_FeaturesBitSet_True()
    {
        var fake = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };
        var device = new DchuKeyboardDevice(fake);

        Assert.True(device.Has3ZoneKeyboard);
        Assert.Equal(1, fake.GetIntegerCallCount); // 结果缓存
    }

    [Fact]
    public void Has3ZoneKeyboard_ResultCached_SingleProbe()
    {
        var fake = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };
        var device = new DchuKeyboardDevice(fake);

        _ = device.Has3ZoneKeyboard;
        _ = device.Has3ZoneKeyboard;
        _ = device.Has3ZoneKeyboard;

        Assert.Equal(1, fake.GetIntegerCallCount);
    }

    [Fact]
    public void Has3ZoneKeyboard_UnsupportedResult_False()
    {
        // 0x80000002 = 命令不支持（安全默认：按非三区）
        var fake = new FakeDchuTransport(); // 默认 Features1Result = 0x80000002
        var device = new DchuKeyboardDevice(fake);

        Assert.False(device.Has3ZoneKeyboard);
    }

    [Fact]
    public void Has3ZoneKeyboard_WhiteMonoBitOnly_False()
    {
        // 0x40000000 = 白色单色键盘，不是三区位
        var fake = new FakeDchuTransport { Features1Result = unchecked((int)0x40000000u) };
        var device = new DchuKeyboardDevice(fake);

        Assert.False(device.Has3ZoneKeyboard);
    }

    [Fact]
    public void Has3ZoneKeyboard_ProbeThrows_False_AndCached()
    {
        var fake = new FakeDchuTransport { GetIntegerException = new InvalidOperationException("驱动缺失") };
        var device = new DchuKeyboardDevice(fake);

        Assert.False(device.Has3ZoneKeyboard);
        Assert.False(device.Has3ZoneKeyboard);
        Assert.Equal(1, fake.GetIntegerCallCount); // 异常结果同样缓存，不重复探测
    }

    [Fact]
    public void SetZoneColor_Without3Zone_ThrowsNotSupportedException()
    {
        var fake = new FakeDchuTransport(); // 无三区位
        var device = new DchuKeyboardDevice(fake);

        Assert.Throws<NotSupportedException>(() => device.SetZoneColor(0, new RgbColor(255, 0, 0)));
        Assert.Empty(fake.Sent); // 绝不下发
    }

    [Fact]
    public void SetZoneBrightness_Without3Zone_ThrowsNotSupportedException()
    {
        var fake = new FakeDchuTransport();
        var device = new DchuKeyboardDevice(fake);

        Assert.Throws<NotSupportedException>(() => device.SetZoneBrightness(63));
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public void ApplyZoneStatic_Without3Zone_ThrowsNotSupportedException()
    {
        var fake = new FakeDchuTransport();
        var device = new DchuKeyboardDevice(fake);

        Assert.Throws<NotSupportedException>(
            () => device.ApplyZoneStatic([(0, new RgbColor(255, 0, 0))], 63));
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public void ApplyZoneStatic_With3Zone_EmitsModeThenZonesThenBrightness()
    {
        var fake = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };
        var device = new DchuKeyboardDevice(fake);

        device.ApplyZoneStatic(
        [
            (0, new RgbColor(255, 0, 0)),
            (1, new RgbColor(0, 255, 0)),
            (2, new RgbColor(0, 0, 255)),
        ], 63);

        Assert.Equal(
        [
            0x10000000u,        // 9.6 时序第 1 步
            0xF000FF00u,        // zone0 红
            0xF10000FFu,        // zone1 绿
            0xF2FF0000u,        // zone2 蓝
            0xF400003Fu,        // 第 3 步亮度
        ], fake.Sent.Select(entry => (uint)entry.Args));
        Assert.All(fake.Sent, entry => Assert.Equal(DchuZoneProtocol.SetDchuLedCommand, entry.Command));
    }

    [Fact]
    public void ApplyZoneStatic_LightbarModel_IncludesZone3_NonLightbar_DoesNot()
    {
        // 机型表命中（clevo-xsm-wmi DMI 表）→ 序列含 0xF3；未命中 → 不含
        var lightbarProduct = "P870DM";
        var singleZoneProduct = "P955ET1";

        var fakeLightbar = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };
        var deviceLightbar = new DchuKeyboardDevice(fakeLightbar);
        deviceLightbar.ApplyZoneStatic(BuildZones(productName: lightbarProduct, color: new RgbColor(255, 128, 0)), 126);
        Assert.Contains(fakeLightbar.Sent, entry => IsLightbarArgs(entry.Args));

        var fakeSingle = new FakeDchuTransport { Features1Result = unchecked((int)0x00400000u) };
        var deviceSingle = new DchuKeyboardDevice(fakeSingle);
        deviceSingle.ApplyZoneStatic(BuildZones(productName: singleZoneProduct, color: new RgbColor(255, 128, 0)), 126);
        Assert.DoesNotContain(fakeSingle.Sent, entry => IsLightbarArgs(entry.Args));
    }

    private static List<(int zone, RgbColor color)> BuildZones(string productName, RgbColor color)
    {
        var zones = new List<(int zone, RgbColor color)>
        {
            (0, color),
            (1, color),
            (2, color),
        };
        if (LightbarDetector.MatchesLightbar(productName))
        {
            zones.Add((3, color)); // 灯带是否入列由调用方依据 LightbarDetector 决定
        }

        return zones;
    }
}
