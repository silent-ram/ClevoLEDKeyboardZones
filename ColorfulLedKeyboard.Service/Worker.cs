namespace ColorfulLedKeyboard.Service;

using ColorfulLedKeyboard.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

public class Worker : BackgroundService
{
    private readonly SettingsStore _settingsStore = new();
    private readonly DchuKeyboardDevice _device = DchuKeyboardDevice.CreateDefault();

    // 多分区循环的跨重入状态：配置变更（文件监视触发）会让循环退出重进，
    // 这些字段让"没变的区"不发任何命令——单区硬件上三槽位同址，逐区重写会闪色，
    // CUSTOM 模式命令重复发会让 EC 重置灯效状态（换色瞬间闪烁的根因）。
    private readonly LightingFrameGenerator?[] _multiZoneGenerators = new LightingFrameGenerator?[4];
    private readonly string?[] _multiZoneGeneratorSignatures = new string?[4];
    private readonly RgbColor?[] _multiZoneLastColors = new RgbColor?[4];
    private bool _multiZoneModeApplied;
    private byte _multiZoneBrightnessLevel;
    private bool _multiZoneActive;
    private readonly AudioSourceProvider _audioSource;
    private readonly SystemAudioLevelMeter _audioLevelMeter;
    private readonly AudioBandLevelMeter _audioBandLevelMeter;
    private readonly AudioApplicationMonitor _audioApplicationMonitor = new();
    private readonly ILogger<Worker> _logger;
    private readonly ServiceIpcServer _ipcServer;
    private FileSystemWatcher? _watcher;
    private volatile bool _settingsChanged = true;
    private List<RgbColor> _lastRenderedMusicColors = [];
    private DateTimeOffset _lastAutomationStatusWrite = DateTimeOffset.MinValue;
    private string _lastAutomationStatusSignature = "";
    private AudioApplicationsState? _lastAudioApplicationsState;
    private DateTimeOffset _lastAudioApplicationsRead = DateTimeOffset.MinValue;

    public Worker(ILogger<Worker> logger, ILoggerFactory loggerFactory, ServiceIpcServer ipcServer)
    {
        _logger = logger;
        _ipcServer = ipcServer;
        _audioSource = new AudioSourceProvider(loggerFactory.CreateLogger<AudioSourceProvider>());
        _audioLevelMeter = new SystemAudioLevelMeter(_audioSource);
        _audioBandLevelMeter = new AudioBandLevelMeter(_audioSource);
        _audioSource.SourceChanged += OnAudioSourceChanged;

        // 订阅完事件后立刻刷一次状态：让初始（启动那一刻）的设备状态也走 OnAudioSourceChanged
        // 写到文件里，否则 Tray 在用户首次切设备前都看不到任何状态，UI 显示"检测中…"。
        _audioSource.RefreshNow();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 外接模式（实验，环境变量 CLEVO_LED_SIMULATOR_PIPE=1）：DCHU 命令转发给虚拟键盘模拟器，
        // 零真实 EC 写。真实模式：P/Invoke InsydeDCHU.dll 直发真实 EC。两者都把 IPC 托管在
        // 分支专用 TCP 通道（标准管道被已安装主服务占用），本仓库托盘的保存/读取经此到达本服务。
        if (SimulatorPipeTransport.Enabled)
        {
            _logger.LogInformation(
                "Simulator pipe mode enabled via {EnvVar}: forwarding DCHU commands to the virtual keyboard; IPC hosted on TCP 127.0.0.1:{ForkIpcPort}",
                SimulatorPipeTransport.EnableEnvironmentVariable, ServiceIpc.ForkIpcPort);
        }
        else
        {
            _logger.LogInformation("Real keyboard mode (InsydeDCHU.dll P/Invoke); IPC hosted on TCP 127.0.0.1:{ForkIpcPort}",
                ServiceIpc.ForkIpcPort);
        }

        _ipcServer.Start();

        EnsureConfigWatcher();
        var audioStatusReconcile = ReconcileAudioStatusAsync(stoppingToken);
        await FlashStartupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = BuildRuntimeSettings(_settingsStore.Load());
            _settingsChanged = false;

            if (_multiZoneActive && (!settings.Enabled || settings.OperatingMode != OperatingMode.MultiZone))
            {
                LeaveMultiZoneState();
            }

            if (!settings.Enabled)
            {
                TryTurnOffKeyboard();
                await WaitForSettingsChangeAsync(1000, stoppingToken);
                continue;
            }

            try
            {
                await RunEffectAsync(settings, stoppingToken);
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogError(ex, "InsydeDCHU.dll was not found. Copy it next to the service executable.");
                await Task.Delay(5000, stoppingToken);
            }
            catch (EntryPointNotFoundException ex)
            {
                _logger.LogError(ex, "InsydeDCHU.dll does not expose SetDCHU_Data.");
                await Task.Delay(5000, stoppingToken);
            }
            catch (SEHException ex)
            {
                _logger.LogError(ex, "The keyboard LED driver rejected the operation.");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        _ipcServer.Dispose();
        _audioSource.SourceChanged -= OnAudioSourceChanged;

        // NAudio 在某些路径下 StopRecording / Dispose 可能阻塞（COM 回调链路死锁）
        // 加 5 秒超时保护，超时则放弃 dispose 让进程自然终结
        var disposeTask = Task.Run(() =>
        {
            try { _audioBandLevelMeter.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "AudioBandLevelMeter.Dispose threw"); }
            try { _audioLevelMeter.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "SystemAudioLevelMeter.Dispose threw"); }
            try { _audioSource.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "AudioSourceProvider.Dispose threw"); }
            try { _audioApplicationMonitor.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "AudioApplicationMonitor.Dispose threw"); }
        });

        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        if (completed != disposeTask)
        {
            _logger.LogWarning("Audio dispose timed out after 5s; proceeding with shutdown anyway");
        }

        await base.StopAsync(cancellationToken);
    }

    private void TryTurnOffKeyboard()
    {
        try
        {
            RenderFrame(_device, RgbColor.Black, SimulatorPipeTransport.Enabled);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            _logger.LogWarning(ex, "Keyboard LEDs could not be turned off.");
        }
    }

    /// <summary>
    /// 渲染一帧。生产路径 = SetColor 三槽位写（既有行为，逐字节不变）；
    /// 外接模拟器模式（环境变量开启）= 分区路径直写：三区 + 灯带同色，让模拟器的
    /// 分区列与灯带条参与显示。环境变量即门控，生产永远不会进入该分支；
    /// 真实三区机型的效果管线属后续工作（文档 9.9：能力位仅必要条件，不得作为运行时门控）。
    /// </summary>
    internal static void RenderFrame(DchuKeyboardDevice device, RgbColor color, bool simulatorMode)
    {
        if (!simulatorMode)
        {
            device.SetColor(color);
            return;
        }

        device.WriteRawLedArgs(DchuZoneProtocol.PackZoneColorArgs(0, color));
        device.WriteRawLedArgs(DchuZoneProtocol.PackZoneColorArgs(1, color));
        device.WriteRawLedArgs(DchuZoneProtocol.PackZoneColorArgs(2, color));
        device.WriteRawLedArgs(DchuZoneProtocol.PackZoneColorArgs(3, color));
    }

    private async Task RunEffectAsync(KeyboardSettings settings, CancellationToken stoppingToken)
    {
        if (settings.OperatingMode == OperatingMode.MultiZone)
        {
            await RunMultiZoneAsync(settings, stoppingToken);
            return;
        }

        await RunLightingAsync(settings, stoppingToken);
    }

    /// <summary>
    /// 多分区模式（实验）：左/中/右[/灯带]各自按 MultiZone.Zones 的灯效独立渲染。
    /// 门控：能力位（GET_BIOS_FEATURES_1 0x00400000）未命中 → 写状态文件并回退普通灯效管线，
    /// 绝不静默下发分区命令（单分区用户零变化的保证）。命中即按区下发 0xF0/0xF1/0xF2[/0xF3]，
    /// 颜色软件缩放（与单区管线一致），每区变化才写，时基为进程内 Stopwatch 共享时间轴。
    /// 注意：能力位只是必要条件（P955ET1 实测置位但物理单分区，文档 9.9）——命中门控的
    /// 单分区硬件上三槽位退化同址，表现为各区颜色互相覆盖，UI 已提示。
    /// </summary>
    private async Task RunMultiZoneAsync(KeyboardSettings settings, CancellationToken stoppingToken)
    {
        var multi = settings.MultiZone;
        if (multi.Layout == MultiZoneLayout.SingleMerged)
        {
            // 单区合并不需要三区能力位：走与灯效模式相同的单区路径（SetColor），零门控依赖
            await RunMultiZoneSingleMergedAsync(settings, multi, stoppingToken);
            return;
        }

        if (!_device.Has3ZoneKeyboard)
        {
            new MultiZoneStatus { CapabilityDetected = false, Active = false }.Save();
            _logger.LogWarning(
                "Multi-zone mode selected but the 3-zone capability bit is clear; falling back to the single-zone pipeline");
            var fallback = settings.CloneForRuntime();
            fallback.OperatingMode = OperatingMode.Lighting;
            await RunLightingAsync(fallback, stoppingToken);
            return;
        }

        _multiZoneActive = true;
        new MultiZoneStatus { CapabilityDetected = true, Active = true }.Save();
        try
        {
            var zoneGenerator0 = ZoneGenerator(0, multi.Zones[0]);
            var zoneGenerator1 = ZoneGenerator(1, multi.Zones[1]);
            var zoneGenerator2 = ZoneGenerator(2, multi.Zones[2]);
            var lightbarGenerator = multi.IncludeLightbar ? ZoneGenerator(3, multi.Zones[3]) : null;
            var interval = Math.Clamp(
                Math.Min(Math.Min(zoneGenerator0.IntervalMs, zoneGenerator1.IntervalMs),
                    Math.Min(zoneGenerator2.IntervalMs, lightbarGenerator?.IntervalMs ?? int.MaxValue)),
                20, 100);

            // 真实三区机型需先切 CUSTOM/静态模式，分区颜色才会显示（文档 9.6 时序）。
            // 亮度完全走 0xF4（EC 硬件亮度，协议设计如此），分区颜色满档渲染：
            // 亮度变化 = 单条 0xF4、分区零重写——单区硬件上多区同写会闪色（三槽位同址，
            // 亮度滑块拖动时的闪烁即源于此）。若个别单区固件忽略 0xF4，多分区模式下
            // 亮度滑块无可见效果（灯效模式的软件缩放不受影响）。
            if (!_multiZoneModeApplied)
            {
                _device.ApplyCustomMode();
                _multiZoneModeApplied = true;
            }

            var level = (byte)Math.Clamp(settings.Brightness * 255 / 100, 0, 255);
            if (level != _multiZoneBrightnessLevel)
            {
                _device.SetZoneBrightness(level);
                _multiZoneBrightnessLevel = level;
            }

            var clock = Stopwatch.StartNew();
            // 管道模式：模拟器晚启动/断连重连会让早期帧丢失；每秒整帧重发兜底（真实路径纯去重）
            var resendPeriod = SimulatorPipeTransport.Enabled ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan;
            var nextResend = DateTimeOffset.UtcNow + resendPeriod;

            while (!stoppingToken.IsCancellationRequested && !_settingsChanged)
            {
                var elapsed = clock.Elapsed.TotalMilliseconds;
                // 满档渲染（亮度由 0xF4 承担，见入口注释）
                var z0 = zoneGenerator0.NextAtElapsed(100, elapsed);
                var z1 = zoneGenerator1.NextAtElapsed(100, elapsed);
                var z2 = zoneGenerator2.NextAtElapsed(100, elapsed);
                RgbColor? z3 = null;
                if (lightbarGenerator is not null)
                {
                    z3 = lightbarGenerator.NextAtElapsed(100, elapsed);
                }

                var resendDue = DateTimeOffset.UtcNow >= nextResend;
                var frame = new List<(int Zone, RgbColor Color)>(4);
                var changed = false;
                if (z0 != _multiZoneLastColors[0]) { frame.Add((0, z0)); _multiZoneLastColors[0] = z0; changed = true; }
                if (z1 != _multiZoneLastColors[1]) { frame.Add((1, z1)); _multiZoneLastColors[1] = z1; changed = true; }
                if (z2 != _multiZoneLastColors[2]) { frame.Add((2, z2)); _multiZoneLastColors[2] = z2; changed = true; }
                if (z3 is not null && z3 != _multiZoneLastColors[3]) { frame.Add((3, z3.Value)); _multiZoneLastColors[3] = z3; changed = true; }
                if (!changed && resendDue)
                {
                    frame.Add((0, z0));
                    frame.Add((1, z1));
                    frame.Add((2, z2));
                    if (z3 is not null) frame.Add((3, z3.Value));
                    _multiZoneLastColors[0] = z0;
                    _multiZoneLastColors[1] = z1;
                    _multiZoneLastColors[2] = z2;
                    _multiZoneLastColors[3] = z3;
                }

                if (frame.Count > 0)
                {
                    RenderMultiZoneFrame(_device, frame);
                }

                if (resendDue)
                {
                    nextResend = DateTimeOffset.UtcNow + resendPeriod;
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        finally
        {
            new MultiZoneStatus { CapabilityDetected = true, Active = false }.Save();
        }
    }

    /// <summary>
    /// 单区合并渲染：整块键盘一块灯（单分区机型），仅 左分区 配置生效，走与灯效模式完全相同的
    /// 单区路径（SetColor 三槽位同色写——单分区用户长期验证过，同色连写无闪色），亮度为软件
    /// 缩放（该路径已知可用）。不发 CUSTOM/0xF4/0xF3——零新增操作码。
    /// </summary>
    private async Task RunMultiZoneSingleMergedAsync(KeyboardSettings settings, MultiZoneSettings multi, CancellationToken stoppingToken)
    {
        _multiZoneActive = true;
        new MultiZoneStatus { CapabilityDetected = _device.Has3ZoneKeyboard, Active = true }.Save();
        try
        {
            var generator = ZoneGenerator(0, multi.Zones[0]);
            var clock = Stopwatch.StartNew();
            RgbColor? last = null;
            while (!stoppingToken.IsCancellationRequested && !_settingsChanged)
            {
                var color = generator.NextAtElapsed(settings.Brightness, clock.Elapsed.TotalMilliseconds);
                if (color != last)
                {
                    _device.SetColor(color);
                    last = color;
                }

                await Task.Delay(generator.IntervalMs, stoppingToken);
            }
        }
        finally
        {
            new MultiZoneStatus { CapabilityDetected = _device.Has3ZoneKeyboard, Active = false }.Save();
        }
    }

    /// <summary>
    /// 按区取生成器：相位签名（类型/周期/最低亮度等，不含颜色）未变时复用并热更新颜色
    /// （换色不重置呼吸相位——重建会让首帧写到接近黑色，肉眼暗闪一下）；签名变了才重建。
    /// </summary>
    private LightingFrameGenerator ZoneGenerator(int zone, LightingEffectSettings effect)
    {
        var signature = PhaseSignature(effect);
        if (_multiZoneGenerators[zone] is null || _multiZoneGeneratorSignatures[zone] != signature)
        {
            _multiZoneGenerators[zone] = new LightingFrameGenerator(effect);
            _multiZoneGeneratorSignatures[zone] = signature;
            _multiZoneLastColors[zone] = null; // 相位参数变化：下一帧强制重写该区
        }
        else
        {
            _multiZoneGenerators[zone]!.UpdateEffect(effect);
        }

        return _multiZoneGenerators[zone]!;
    }

    private static string PhaseSignature(LightingEffectSettings effect) =>
        string.Join("|",
            effect.Type, effect.PeriodMs, effect.MinimumBrightness, effect.HardBlink,
            effect.Step, effect.IntervalMs, effect.CustomSequenceColorsEnabled,
            string.Join(";", effect.Sequence.Select(item =>
                $"{item.Color},{item.HoldMs},{item.TransitionMs},{item.Breathing}")));

    /// <summary>离开多分区（切模式/关闭）：恢复 EC 亮度满档（单区管线按"软件缩放 + EC 满亮度"假设工作），清空跨重入状态。</summary>
    private void LeaveMultiZoneState()
    {
        try { _device.SetZoneBrightness(255); } catch (NotSupportedException) { }
        _multiZoneActive = false;
        _multiZoneModeApplied = false;
        _multiZoneBrightnessLevel = 0;
        Array.Clear(_multiZoneGenerators);
        Array.Clear(_multiZoneGeneratorSignatures);
        Array.Clear(_multiZoneLastColors);
    }

    /// <summary>按 (zone, color) 顺序下发分区颜色（经 SetZoneColor 的能力门控；调用方须已确认能力位）。</summary>
    internal static void RenderMultiZoneFrame(DchuKeyboardDevice device, IReadOnlyList<(int Zone, RgbColor Color)> zones)
    {
        foreach (var entry in zones)
        {
            device.SetZoneColor(entry.Zone, entry.Color);
        }
    }

    private async Task RunLightingAsync(KeyboardSettings settings, CancellationToken stoppingToken)
    {
        if (settings.OperatingMode == OperatingMode.Music)
        {
            await RunMusicAsync(settings, stoppingToken);
            return;
        }

        var generator = new LightingFrameGenerator(settings);
        var nextRuntimeRefresh = DateTimeOffset.UtcNow.Add(RuntimePollInterval(settings));
        RgbColor? lastColor = null;
        var resendPeriod = SimulatorPipeTransport.Enabled ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan;
        var nextResend = DateTimeOffset.UtcNow + resendPeriod;

        while (!stoppingToken.IsCancellationRequested && !_settingsChanged)
        {
            if (DateTimeOffset.UtcNow >= nextRuntimeRefresh)
            {
                nextRuntimeRefresh = DateTimeOffset.UtcNow.Add(RuntimePollInterval(settings));
                if (ShouldRebuildRuntimeSettings(settings))
                {
                    _settingsChanged = true;
                    return;
                }
            }

            var brightness = ApplyTypingPulseBrightness(settings.Brightness, settings);
            var color = ClampOutputBrightness(
                ApplyNotificationFlash(generator.Next(brightness), settings),
                settings.OutputBrightnessLimit);
            // 管道模式：模拟器可能晚于服务启动，最初的帧会被静默丢弃；静态效果颜色不变
            // 不再重发会让虚拟键盘永远黑屏。每秒强制重发一次当前帧（生产路径保持纯去重）。
            var resendDue = SimulatorPipeTransport.Enabled && DateTimeOffset.UtcNow >= nextResend;
            if (color != lastColor || resendDue)
            {
                RenderFrame(_device, color, SimulatorPipeTransport.Enabled);
                lastColor = color;
                if (resendDue) nextResend = DateTimeOffset.UtcNow + resendPeriod;
            }

            if (settings.Effect.Type is EffectType.Static or EffectType.Off)
            {
                if ((settings.Effect.Type == EffectType.Static && settings.TypingPulse.Enabled) ||
                    settings.NotificationFlash.Enabled)
                {
                    await Task.Delay(40, stoppingToken);
                    continue;
                }

                if (NeedsRuntimePolling(settings))
                {
                    await Task.Delay(RuntimePollInterval(settings), stoppingToken);
                    _settingsChanged = true;
                    return;
                }

                await WaitForSettingsChangeAsync(1000, stoppingToken);
                _settingsChanged = true;
                return;
            }

            await Task.Delay(generator.IntervalMs, stoppingToken);
        }
    }

    private async Task RunMusicAsync(KeyboardSettings settings, CancellationToken stoppingToken)
    {
        var music = settings.Effect.Music.Normalize();
        var controller = new MusicPulseController();
        var targetMusicColors = music.Colors.Select(RgbColor.FromHex).ToList();
        var transitionFrom = _lastRenderedMusicColors.Count == 0
            ? targetMusicColors
            : Enumerable.Range(0, targetMusicColors.Count)
                .Select(index => _lastRenderedMusicColors[index % _lastRenderedMusicColors.Count]).ToList();
        var colorTransitionStarted = DateTimeOffset.UtcNow;
        var nextRuntimeRefresh = DateTimeOffset.UtcNow.Add(RuntimePollInterval(settings));
        RgbColor? lastColor = null;
        var resendPeriod = SimulatorPipeTransport.Enabled ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan;
        var nextResend = DateTimeOffset.UtcNow + resendPeriod;

        // 进入音乐模式立刻刷一次状态文件，避免 Tray 看到陈旧值
        _audioSource.RefreshNow();

        try
        {
            while (!stoppingToken.IsCancellationRequested && !_settingsChanged)
            {
                if (DateTimeOffset.UtcNow >= nextRuntimeRefresh)
                {
                    nextRuntimeRefresh = DateTimeOffset.UtcNow.Add(RuntimePollInterval(settings));
                    if (ShouldRebuildRuntimeSettings(settings))
                    {
                        _settingsChanged = true;
                        return;
                    }
                }

                // 绑定播放器时只读对应音频会话；未绑定时才允许系统 Loopback 做频段分析。
                // 静音时 level=0，灯效会自然降到 BaseBrightness 并保持当前颜色。
                var selectedPeak = GetSelectedApplicationPeak(
                    settings.SelectedAudioProcessName, settings.SelectedAudioExecutablePath, settings.SelectedAudioProcessIds);
                var hasSelectedApplication = !string.IsNullOrWhiteSpace(settings.SelectedAudioProcessName);
                var systemMixLevel = !hasSelectedApplication && music.EqEnabled && music.AllowSystemMixFallback
                    ? _audioBandLevelMeter.GetAdaptiveBeatLevel(music)
                    : 0f;
                var level = MusicAudioInputSelector.Select(
                    music, hasSelectedApplication, selectedPeak, systemMixLevel);
                var systemVolume = _audioLevelMeter.GetMasterVolumeScalar();
                var transition = Math.Clamp((DateTimeOffset.UtcNow - colorTransitionStarted).TotalMilliseconds / 800d, 0, 1);
                var musicColors = targetMusicColors.Select((color, index) =>
                    RgbColor.Lerp(transitionFrom[index], color, transition)).ToList();
                _lastRenderedMusicColors = musicColors;
                var frame = controller.Next(music, level, systemVolume, musicColors.Count);
                var envelope = frame.Envelope;
                // 单色封面缺少多色切换带来的视觉节奏，扩大明暗范围让鼓点清晰可见。
                var effectiveBaseBrightness = musicColors.Count == 1
                    ? Math.Min(music.BaseBrightness, 8)
                    : music.BaseBrightness;
                var musicBrightness = effectiveBaseBrightness +
                    (music.PeakBrightness - effectiveBaseBrightness) * Math.Pow(envelope, musicColors.Count == 1 ? 0.45 : 0.55);
                var brightness = Math.Min(
                    (int)Math.Clamp(Math.Round(musicBrightness), effectiveBaseBrightness, music.PeakBrightness),
                    settings.OutputBrightnessLimit);
                brightness = Math.Min(
                    ApplyTypingPulseBrightness(brightness, settings),
                    settings.OutputBrightnessLimit);
                var sourceColor = musicColors[frame.ColorIndex % musicColors.Count];
                var color = ClampOutputBrightness(
                    ApplyNotificationFlash(sourceColor.Scale(brightness), settings),
                    settings.OutputBrightnessLimit);

                var resendDue = SimulatorPipeTransport.Enabled && DateTimeOffset.UtcNow >= nextResend;
                if (color != lastColor || resendDue)
                {
                    RenderFrame(_device, color, SimulatorPipeTransport.Enabled);
                    lastColor = color;
                    if (resendDue) nextResend = DateTimeOffset.UtcNow + resendPeriod;
                }

                await Task.Delay(music.IntervalMs, stoppingToken);
            }
        }
        finally
        {
            _audioBandLevelMeter.PauseCapture();
            _audioLevelMeter.PauseDevice();
        }
    }

    private static RgbColor ApplyNotificationFlash(RgbColor color, KeyboardSettings settings)
    {
        var flash = settings.NotificationFlash.Normalize();
        if (!flash.Enabled)
        {
            return color;
        }

        var state = NotificationFlashState.Load();
        if (state is null)
        {
            return color;
        }

        var elapsedMs = (DateTimeOffset.UtcNow - state.TriggeredUtc).TotalMilliseconds;
        var cycleMs = flash.PulseMs * 2;
        var totalMs = cycleMs * flash.Pulses;
        if (elapsedMs < 0 || elapsedMs > totalMs)
        {
            return color;
        }

        var phase = elapsedMs % cycleMs;
        return phase < flash.PulseMs ? RgbColor.FromHex(flash.Color) : RgbColor.Black;
    }

    private static RgbColor ClampOutputBrightness(RgbColor color, int limit)
    {
        limit = Math.Clamp(limit, 0, 100);
        var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
        var allowed = limit * 255 / 100d;
        if (maximum == 0 || maximum <= allowed)
        {
            return color;
        }

        var scale = allowed / maximum;
        return new RgbColor(
            (byte)Math.Round(color.R * scale),
            (byte)Math.Round(color.G * scale),
            (byte)Math.Round(color.B * scale));
    }

    private static int ApplyTypingPulseBrightness(int currentBrightness, KeyboardSettings settings)
    {
        var pulse = settings.TypingPulse.Normalize();
        if (!pulse.Enabled)
        {
            return currentBrightness;
        }

        var state = TypingPulseState.Load();
        if (state is null)
        {
            return currentBrightness;
        }

        var elapsedMs = (DateTimeOffset.UtcNow - state.LastKeyUtc).TotalMilliseconds;
        if (elapsedMs < 0 || elapsedMs > pulse.HoldMs + pulse.FadeMs)
        {
            return currentBrightness;
        }

        var pulseBrightness = pulse.PeakBrightness;
        if (elapsedMs > pulse.HoldMs)
        {
            var progress = Math.Clamp((elapsedMs - pulse.HoldMs) / Math.Max(1, pulse.FadeMs), 0, 1);
            pulseBrightness = (int)Math.Round(pulse.PeakBrightness - (pulse.PeakBrightness - currentBrightness) * progress);
        }

        return Math.Max(currentBrightness, pulseBrightness);
    }

    private KeyboardSettings BuildRuntimeSettings(KeyboardSettings settings)
    {
        var runtime = settings.CloneForRuntime();
        var status = ApplyAutomation(runtime);
        status.IdleOverrideActive = ApplyIdleDim(runtime);
        ApplyBrightnessStatus(runtime, status);
        status.UpdatedUtc = DateTimeOffset.UtcNow;
        PublishAutomationStatus(status);
        return runtime.Normalize();
    }

    private void PublishAutomationStatus(AutomationStatus status)
    {
        var signature = string.Join("|", status.ActiveRuleId, status.ForegroundProcessName,
            status.ActiveMusicApplication, string.Join(",", status.ActiveProcessIds), status.TrackTitle,
            status.AlbumColor, status.IdleOverrideActive, status.FinalBrightnessLimit,
            status.BrightnessDisplay, status.BrightnessDescription, status.InvalidReason);
        if (signature == _lastAutomationStatusSignature &&
            DateTimeOffset.UtcNow - _lastAutomationStatusWrite < TimeSpan.FromSeconds(1)) return;
        _lastAutomationStatusSignature = signature;
        _lastAutomationStatusWrite = DateTimeOffset.UtcNow;
        status.Save();
    }

    internal static void ApplyBrightnessStatus(KeyboardSettings settings, AutomationStatus status)
    {
        var outputLimit = Math.Clamp(settings.OutputBrightnessLimit, 0, 100);
        if (!settings.Enabled || settings.Effect.Type == EffectType.Off || outputLimit == 0)
        {
            status.FinalBrightnessLimit = 0;
            status.BrightnessDisplay = "已关闭";
            status.BrightnessDescription = status.IdleOverrideActive ? "空闲关灯覆盖" : "当前模式关闭灯光";
            return;
        }

        if (settings.OperatingMode == OperatingMode.Music)
        {
            var low = Math.Min(settings.Effect.Music.BaseBrightness, outputLimit);
            var high = Math.Min(settings.Effect.Music.PeakBrightness, outputLimit);
            if (low > high) low = high;
            status.FinalBrightnessLimit = high;
            status.BrightnessDisplay = low == high ? $"{high}%" : $"{low}–{high}%";
            status.BrightnessDescription = status.IdleOverrideActive
                ? $"音乐动态范围 · 空闲上限 {outputLimit}%"
                : $"音乐动态范围 · 输出上限 {outputLimit}%";
            return;
        }

        var final = Math.Min(settings.Brightness, outputLimit);
        status.FinalBrightnessLimit = final;
        status.BrightnessDisplay = $"{final}%";
        status.BrightnessDescription = status.IdleOverrideActive
            ? $"灯效亮度 · 空闲上限 {outputLimit}%"
            : outputLimit < settings.Brightness
                ? $"灯效亮度 · 场景上限 {outputLimit}%"
                : "灯效实际亮度";
    }

    private bool ShouldRebuildRuntimeSettings(KeyboardSettings current)
    {
        var next = BuildRuntimeSettings(new SettingsStore().Load());
        return next.Enabled != current.Enabled ||
            next.OperatingMode != current.OperatingMode ||
            next.Brightness != current.Brightness ||
            next.OutputBrightnessLimit != current.OutputBrightnessLimit ||
            next.SelectedAudioProcessName != current.SelectedAudioProcessName ||
            next.SelectedAudioExecutablePath != current.SelectedAudioExecutablePath ||
            !next.SelectedAudioProcessIds.SequenceEqual(current.SelectedAudioProcessIds) ||
            !TypingPulseEquals(next.TypingPulse, current.TypingPulse) ||
            !NotificationFlashEquals(next.NotificationFlash, current.NotificationFlash) ||
            next.Effect.Type != current.Effect.Type ||
            next.Effect.Color != current.Effect.Color ||
            next.Effect.Step != current.Effect.Step ||
            next.Effect.IntervalMs != current.Effect.IntervalMs ||
            next.Effect.PeriodMs != current.Effect.PeriodMs ||
            next.Effect.MinimumBrightness != current.Effect.MinimumBrightness ||
            next.Effect.HardBlink != current.Effect.HardBlink ||
            next.Effect.CustomSequenceColorsEnabled != current.Effect.CustomSequenceColorsEnabled ||
            !MusicEquals(next.Effect.Music, current.Effect.Music) ||
            next.Effect.Sequence.Count != current.Effect.Sequence.Count ||
            next.Effect.Sequence.Zip(current.Effect.Sequence).Any(pair =>
                pair.First.Color != pair.Second.Color ||
                pair.First.HoldMs != pair.Second.HoldMs ||
                pair.First.TransitionMs != pair.Second.TransitionMs ||
                pair.First.Breathing != pair.Second.Breathing);
    }

    private static bool NeedsRuntimePolling(KeyboardSettings settings)
    {
        // 音乐页的播放器 PID 绑定依赖实时会话列表，自动化关闭时也必须持续刷新。
        return true;
    }

    private static bool NotificationFlashEquals(NotificationFlashSettings left, NotificationFlashSettings right)
    {
        return left.Enabled == right.Enabled &&
            left.Color == right.Color &&
            left.Pulses == right.Pulses &&
            left.PulseMs == right.PulseMs &&
            left.CooldownSeconds == right.CooldownSeconds;
    }

    private static bool MusicEquals(MusicSettings left, MusicSettings right)
    {
        return left.LevelColorEnabled == right.LevelColorEnabled &&
            left.PresetName == right.PresetName &&
            left.ResponseMode == right.ResponseMode &&
            left.LowColor == right.LowColor &&
            left.HighColor == right.HighColor &&
            left.Colors.Count == right.Colors.Count &&
            left.Colors.SequenceEqual(right.Colors, StringComparer.OrdinalIgnoreCase) &&
            Math.Abs(left.Sensitivity - right.Sensitivity) < 0.001 &&
            left.AttackMs == right.AttackMs &&
            left.ReleaseMs == right.ReleaseMs &&
            left.BaseBrightness == right.BaseBrightness &&
            left.PeakBrightness == right.PeakBrightness &&
            left.IntervalMs == right.IntervalMs &&
            Math.Abs(left.NoiseGate - right.NoiseGate) < 0.001 &&
            Math.Abs(left.BeatThreshold - right.BeatThreshold) < 0.001 &&
            left.PeakHoldMs == right.PeakHoldMs &&
            left.FollowSystemVolume == right.FollowSystemVolume &&
            left.EqEnabled == right.EqEnabled &&
            left.AllowSystemMixFallback == right.AllowSystemMixFallback &&
            left.EqLowHz == right.EqLowHz &&
            left.EqHighHz == right.EqHighHz &&
            PlayerBindingEquals(left.PlayerBinding, right.PlayerBinding) &&
            left.CustomPresets.Count == right.CustomPresets.Count &&
            left.CustomPresets.Zip(right.CustomPresets).All(pair => MusicPresetEquals(pair.First, pair.Second));
    }

    private static bool PlayerBindingEquals(MusicPlayerBinding left, MusicPlayerBinding right) =>
        left.Enabled == right.Enabled &&
        left.ProcessName == right.ProcessName &&
        left.ExecutablePath == right.ExecutablePath &&
        left.IncludeChildProcesses == right.IncludeChildProcesses &&
        left.MediaSessionId == right.MediaSessionId &&
        left.ColorSource == right.ColorSource;

    private static bool MusicPresetEquals(MusicPreset left, MusicPreset right)
    {
        return left.Name == right.Name &&
            left.ResponseMode == right.ResponseMode &&
            left.LowColor == right.LowColor &&
            left.HighColor == right.HighColor &&
            left.Colors.Count == right.Colors.Count &&
            left.Colors.SequenceEqual(right.Colors, StringComparer.OrdinalIgnoreCase) &&
            Math.Abs(left.Sensitivity - right.Sensitivity) < 0.001 &&
            left.AttackMs == right.AttackMs &&
            left.ReleaseMs == right.ReleaseMs &&
            left.BaseBrightness == right.BaseBrightness &&
            left.PeakBrightness == right.PeakBrightness &&
            left.IntervalMs == right.IntervalMs &&
            Math.Abs(left.NoiseGate - right.NoiseGate) < 0.001 &&
            Math.Abs(left.BeatThreshold - right.BeatThreshold) < 0.001 &&
            left.PeakHoldMs == right.PeakHoldMs &&
            left.FollowSystemVolume == right.FollowSystemVolume &&
            left.EqEnabled == right.EqEnabled &&
            left.EqLowHz == right.EqLowHz &&
            left.EqHighHz == right.EqHighHz;
    }

    private async Task FlashStartupAsync(CancellationToken stoppingToken)
    {
        try
        {
            for (var i = 0; i < 2; i++)
            {
                RenderFrame(_device, new RgbColor(255, 255, 255), SimulatorPipeTransport.Enabled);
                await Task.Delay(120, stoppingToken);
                RenderFrame(_device, RgbColor.Black, SimulatorPipeTransport.Enabled);
                await Task.Delay(120, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            _logger.LogWarning(ex, "Startup flash could not be sent to the keyboard.");
        }
    }

    private AutomationStatus ApplyAutomation(KeyboardSettings settings)
    {
        var manualMusicMode = settings.OperatingMode == OperatingMode.Music;
        var foreground = ForegroundAppState.Load();
        var foregroundAvailable = foreground is not null &&
            DateTimeOffset.UtcNow - foreground.UpdatedUtc <= TimeSpan.FromSeconds(10);
        var audioApplications = GetAudioApplications(foreground?.ProcessName ?? "");
        var audioStates = audioApplications.Select(app => new AudioApplicationState(
            app.ProcessName, app.ExecutablePath, app.ProcessIds, app.PeakLevel, app.IsPlaying, app.IsForeground)).ToList();
        AddProcessTreeStates(settings.Automation.MusicApplications, audioStates);
        AddPlayerBindingState(settings.Effect.Music.PlayerBinding, audioStates);
        var selection = AutomationResolver.Resolve(
            settings.Automation, DateTime.Now, foregroundAvailable ? foreground?.ProcessName ?? "" : "", audioStates,
            rule => ValidateMusicRule(settings, rule),
            action => ValidateSceneAction(settings, action));

        var status = new AutomationStatus
        {
            ForegroundProcessName = foreground?.ProcessName ?? "",
            ForegroundAvailable = foregroundAvailable,
            AudioApplications = audioApplications.ToList()
        };
        status.InvalidReason = selection.InvalidReason ?? "";

        if (selection.Music is not null && selection.Audio is not null)
        {
            var error = ValidateMusicRule(settings, selection.Music);
            if (error is null)
            {
                ApplyMusicRule(settings, selection.Music);
                settings.SelectedAudioProcessIds = selection.Audio.ProcessIds.ToList();
                var media = ApplyMediaColors(settings, selection.Music);
                status.ActiveRuleId = selection.Music.Id;
                status.ActiveRuleName = selection.Music.Name;
                status.ActiveMusicApplication = selection.Music.ProcessName;
                status.ActiveProcessIds = selection.Audio.ProcessIds.ToList();
                status.TargetDescription = DescribeMusicRule(settings, selection.Music);
                status.AudioCaptureMode = "程序音频会话电平（播放器隔离）";
                if (media is not null)
                {
                    status.TrackTitle = media.Title;
                    status.TrackArtist = media.Artist;
                    status.AlbumColor = media.DominantColor;
                }
            }
            else status.InvalidReason = $"{selection.Music.Name}：{error}";
        }
        else if (selection.Lighting is not null)
        {
            var error = ValidateSceneAction(settings, selection.Lighting.Action);
            if (error is null)
            {
                ApplySceneAction(settings, selection.Lighting.Action);
                status.ActiveRuleId = selection.Lighting.Id;
                status.ActiveRuleName = selection.Lighting.Name;
                status.TargetDescription = DescribeSceneAction(settings, selection.Lighting.Action);
            }
            else status.InvalidReason = $"{selection.Lighting.Name}：{error}";
        }
        else if (selection.Schedule is not null)
        {
            var error = ValidateSceneAction(settings, selection.Schedule.Action);
            if (error is null)
            {
                ApplySceneAction(settings, selection.Schedule.Action);
                status.ActiveRuleId = selection.Schedule.Id;
                status.ActiveRuleName = selection.Schedule.Name;
                status.TargetDescription = DescribeSceneAction(settings, selection.Schedule.Action);
            }
            else status.InvalidReason = $"{selection.Schedule.Name}：{error}";
        }
        else if (manualMusicMode && settings.Effect.Music.PlayerBinding.Enabled)
        {
            ApplyPlayerBinding(settings, audioStates, status);
        }

        if (selection.Music is not null)
        {
            ApplyBrightnessLimit(settings, selection.Music.BrightnessLimit);
            ApplyEventPolicy(settings, selection.Music.TypingPolicy, selection.Music.NotificationPolicy);
        }
        if (selection.Lighting is not null)
        {
            ApplyBrightnessLimit(settings, selection.Lighting.Action.BrightnessLimit);
            ApplyEventPolicy(settings, selection.Lighting.TypingPolicy, selection.Lighting.NotificationPolicy);
        }
        return status;
    }

    private static TimeSpan RuntimePollInterval(KeyboardSettings settings) =>
        (settings.Automation.Enabled && settings.Automation.MusicApplications.Count > 0) ||
        settings.Effect.Music.PlayerBinding.Enabled
            ? TimeSpan.FromMilliseconds(100)
            : TimeSpan.FromSeconds(1);

    private static bool TypingPulseEquals(TypingPulseSettings left, TypingPulseSettings right) =>
        left.Enabled == right.Enabled &&
        left.BaseBrightness == right.BaseBrightness &&
        left.PeakBrightness == right.PeakBrightness &&
        left.HoldMs == right.HoldMs &&
        left.FadeMs == right.FadeMs;

    private static string? ValidateMusicRule(KeyboardSettings settings, MusicApplicationRule rule) =>
        MusicSettings.BuiltInPresets.Any(item => item.Id == rule.MusicPresetId) ||
        settings.Effect.Music.CustomPresets.Any(item => item.Id == rule.MusicPresetId)
            ? null
            : "引用的音乐预设不存在";

    private static void ApplyMusicRule(KeyboardSettings settings, MusicApplicationRule rule)
    {
        settings.Enabled = true;
        settings.OperatingMode = OperatingMode.Music;
        var preset = MusicSettings.BuiltInPresets.Concat(settings.Effect.Music.CustomPresets)
            .First(item => item.Id == rule.MusicPresetId);
        settings.Effect.Music.ApplyPreset(preset);
        settings.SelectedAudioProcessName = rule.ProcessName;
        settings.SelectedAudioExecutablePath = rule.ExecutablePath;
        ApplyBrightnessLimit(settings, rule.BrightnessLimit);
    }

    private static string DescribeMusicRule(KeyboardSettings settings, MusicApplicationRule rule) =>
        "音乐：" + MusicSettings.BuiltInPresets.Concat(settings.Effect.Music.CustomPresets)
            .First(item => item.Id == rule.MusicPresetId).Name;

    private static MediaSessionState? ApplyMediaColors(KeyboardSettings settings, MusicApplicationRule rule)
    {
        if (rule.ColorSource == MusicColorSource.Preset) return null;
        var state = MediaPlaybackState.Load();
        if (state is null || DateTimeOffset.UtcNow - state.UpdatedUtc > TimeSpan.FromSeconds(10)) return null;
        var media = state.Find(rule);
        if (media is null) return null;
        IEnumerable<string> colors = rule.ColorSource == MusicColorSource.AlbumDominant
            ? new[] { media.DominantColor }
            : media.Palette;
        var normalized = colors.Where(color => !string.IsNullOrWhiteSpace(color)).ToList();
        if (normalized.Count == 0) return null;
        settings.Effect.Music.Colors = normalized;
        settings.Effect.Music.LowColor = normalized[0];
        settings.Effect.Music.HighColor = normalized[^1];
        return media;
    }

    private static MediaSessionState? ApplyMediaColors(KeyboardSettings settings, MusicPlayerBinding binding)
    {
        if (binding.ColorSource == MusicColorSource.Preset) return null;
        var state = MediaPlaybackState.Load();
        if (state is null || DateTimeOffset.UtcNow - state.UpdatedUtc > TimeSpan.FromSeconds(10)) return null;
        var media = state.Find(binding);
        if (media is null) return null;
        IEnumerable<string> colors = binding.ColorSource == MusicColorSource.AlbumDominant
            ? new[] { media.DominantColor }
            : media.Palette;
        var normalized = colors.Where(color => !string.IsNullOrWhiteSpace(color)).ToList();
        if (normalized.Count == 0) return null;
        settings.Effect.Music.Colors = normalized;
        settings.Effect.Music.LowColor = normalized[0];
        settings.Effect.Music.HighColor = normalized[^1];
        return media;
    }

    private static void ApplyPlayerBinding(
        KeyboardSettings settings,
        IReadOnlyList<AudioApplicationState> audioStates,
        AutomationStatus status)
    {
        var binding = settings.Effect.Music.PlayerBinding;
        var audio = audioStates.FirstOrDefault(state =>
            string.Equals(state.ProcessName, binding.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(binding.ExecutablePath) ||
             string.Equals(state.ExecutablePath, binding.ExecutablePath, StringComparison.OrdinalIgnoreCase)));
        settings.SelectedAudioProcessName = binding.ProcessName;
        settings.SelectedAudioExecutablePath = binding.ExecutablePath;
        settings.SelectedAudioProcessIds = audio?.ProcessIds.ToList() ?? [];
        var media = ApplyMediaColors(settings, binding);
        status.ActiveRuleName = "音乐页播放器绑定";
        status.ActiveMusicApplication = binding.ProcessName;
        status.ActiveProcessIds = settings.SelectedAudioProcessIds;
        status.TargetDescription = binding.ColorSource == MusicColorSource.Preset ? "音乐：预设颜色" : "音乐：歌曲封面颜色";
        status.AudioCaptureMode = audio is null
            ? "等待播放器音频会话"
            : "程序音频会话电平（播放器隔离）";
        if (audio is null) status.InvalidReason = "已绑定播放器未检测到音频会话";
        if (media is not null)
        {
            status.TrackTitle = media.Title;
            status.TrackArtist = media.Artist;
            status.AlbumColor = media.DominantColor;
        }
    }

    private static void ApplyBrightnessLimit(KeyboardSettings settings, int? limit)
    {
        if (!limit.HasValue) return;
        settings.OutputBrightnessLimit = Math.Min(settings.OutputBrightnessLimit, limit.Value);
        if (settings.OperatingMode == OperatingMode.Lighting)
            settings.Brightness = Math.Min(settings.Brightness, limit.Value);
    }

    private static void ApplyEventPolicy(KeyboardSettings settings, EventPolicy typing, EventPolicy notification)
    {
        if (typing != EventPolicy.Inherit) settings.TypingPulse.Enabled = typing == EventPolicy.Enabled;
        if (notification != EventPolicy.Inherit) settings.NotificationFlash.Enabled = notification == EventPolicy.Enabled;
    }

    private float GetSelectedApplicationPeak(string processName, string executablePath, IReadOnlyCollection<int> processIds)
    {
        if (string.IsNullOrWhiteSpace(processName)) return _audioLevelMeter.GetPeakLevel();
        var foreground = ForegroundAppState.Load()?.ProcessName ?? "";
        var matches = GetAudioApplications(foreground)
            .Where(item => (string.Equals(item.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(executablePath) || string.Equals(item.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)) ||
                item.ProcessIds.Any(processIds.Contains)))
            .ToList();
        return matches.Count == 0 ? 0f : matches.Max(item => item.PeakLevel);
    }

    private IReadOnlyList<AudioApplicationStatus> GetAudioApplications(string foregroundProcessName)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAudioApplicationsRead >= TimeSpan.FromMilliseconds(50))
        {
            _lastAudioApplicationsRead = now;
            _lastAudioApplicationsState = AudioApplicationsState.Load() ?? _lastAudioApplicationsState;
        }
        var state = _lastAudioApplicationsState;
        if (state is not null && DateTimeOffset.UtcNow - state.UpdatedUtc <= TimeSpan.FromSeconds(2))
            return state.Applications;

        // 仅作为非服务宿主/测试环境的兼容回退。Windows 服务 Session 0 看不到用户会话音频。
        return _audioApplicationMonitor.Poll(foregroundProcessName, DateTimeOffset.UtcNow);
    }

    private static void AddPlayerBindingState(MusicPlayerBinding binding, List<AudioApplicationState> states)
    {
        if (!binding.Enabled || !binding.IncludeChildProcesses) return;
        var roots = ProcessTree.FindRoots(binding.ProcessName, binding.ExecutablePath);
        var tree = ProcessTree.Expand(roots);
        var children = states.Where(state => state.ProcessIds.Any(tree.Contains)).ToList();
        if (children.Count == 0) return;
        states.RemoveAll(state => string.Equals(state.ProcessName, binding.ProcessName, StringComparison.OrdinalIgnoreCase));
        states.Add(new AudioApplicationState(
            binding.ProcessName,
            binding.ExecutablePath,
            children.SelectMany(item => item.ProcessIds).Distinct().ToList(),
            children.Max(item => item.PeakLevel),
            children.Any(item => item.IsPlaying),
            children.Any(item => item.IsForeground)));
    }

    private static void AddProcessTreeStates(
        IEnumerable<MusicApplicationRule> rules,
        List<AudioApplicationState> states)
    {
        foreach (var rule in rules.Where(item => item.Enabled && item.IncludeChildProcesses))
        {
            var roots = ProcessTree.FindRoots(rule.ProcessName, rule.ExecutablePath);
            var tree = ProcessTree.Expand(roots);
            var children = states.Where(state => state.ProcessIds.Any(tree.Contains)).ToList();
            if (children.Count == 0) continue;
            states.RemoveAll(state => string.Equals(state.ProcessName, rule.ProcessName, StringComparison.OrdinalIgnoreCase));
            states.Add(new AudioApplicationState(
                rule.ProcessName,
                rule.ExecutablePath,
                children.SelectMany(item => item.ProcessIds).Distinct().ToList(),
                children.Max(item => item.PeakLevel),
                children.Any(item => item.IsPlaying),
                children.Any(item => item.IsForeground)));
        }
    }

    private static string? ValidateSceneAction(KeyboardSettings settings, SceneAction action)
    {
        return action.Target switch
        {
            SceneTargetKind.Off => null,
            SceneTargetKind.LightingPreset when
                action.PresetId == EffectPresetSettings.BuiltInId(action.LightingEffectType) ||
                settings.EffectPresets.ForType(action.LightingEffectType).Any(item => item.Id == action.PresetId) => null,
            SceneTargetKind.MusicPreset when
                MusicSettings.BuiltInPresets.Any(item => item.Id == action.PresetId) ||
                settings.Effect.Music.CustomPresets.Any(item => item.Id == action.PresetId) => null,
            _ => "引用的预设不存在"
        };
    }

    private static void ApplySceneAction(KeyboardSettings settings, SceneAction action)
    {
        settings.Enabled = true;
        if (action.Target == SceneTargetKind.Off)
        {
            settings.OperatingMode = OperatingMode.Lighting;
            settings.Effect.Type = EffectType.Off;
        }
        else if (action.Target == SceneTargetKind.LightingPreset)
        {
            settings.OperatingMode = OperatingMode.Lighting;
            var preset = settings.EffectPresets.ForType(action.LightingEffectType)
                .FirstOrDefault(item => item.Id == action.PresetId);
            settings.Effect = preset is null
                ? EffectPresetSettings.CreateSoftwareDefault(action.LightingEffectType)
                : KeyboardSettings.CloneEffect(preset.Effect);
        }
        else
        {
            settings.OperatingMode = OperatingMode.Music;
            var preset = MusicSettings.BuiltInPresets
                .Concat(settings.Effect.Music.CustomPresets)
                .First(item => item.Id == action.PresetId);
            settings.Effect.Music.ApplyPreset(preset);
        }

        if (action.BrightnessLimit.HasValue)
        {
            settings.OutputBrightnessLimit = action.BrightnessLimit.Value;
            if (settings.OperatingMode == OperatingMode.Lighting)
            {
                settings.Brightness = action.BrightnessLimit.Value;
            }
        }
    }

    private static string DescribeSceneAction(KeyboardSettings settings, SceneAction action)
    {
        if (action.Target == SceneTargetKind.Off) return "关闭灯光";
        if (action.Target == SceneTargetKind.MusicPreset)
        {
            return "音乐：" + MusicSettings.BuiltInPresets
                .Concat(settings.Effect.Music.CustomPresets)
                .First(item => item.Id == action.PresetId).Name;
        }
        if (action.PresetId == EffectPresetSettings.BuiltInId(action.LightingEffectType))
            return $"灯效：{action.LightingEffectType} 软件默认";
        return "灯效：" + settings.EffectPresets.ForType(action.LightingEffectType)
            .First(item => item.Id == action.PresetId).Name;
    }

    private static bool ApplyIdleDim(KeyboardSettings settings)
    {
        if (!settings.IdleDim.Enabled)
        {
            return false;
        }

        if (WindowsIdleTime.GetIdleTime().TotalSeconds < settings.IdleDim.AfterSeconds)
        {
            return false;
        }

        if (settings.IdleDim.TurnOff)
        {
            settings.OperatingMode = OperatingMode.Lighting;
            settings.Effect.Type = EffectType.Off;
            settings.OutputBrightnessLimit = 0;
            return true;
        }

        settings.OutputBrightnessLimit = Math.Min(settings.OutputBrightnessLimit, settings.IdleDim.Brightness);
        settings.Brightness = Math.Min(settings.Brightness, settings.IdleDim.Brightness);
        return true;
    }

    private void EnsureConfigWatcher()
    {
        // 跟随实际设置文件（CLEVO_LED_SETTINGS_PATH 重定向后监视其所在目录）
        var settingsDirectory = Path.GetDirectoryName(AppPaths.SettingsPath);
        var settingsFileName = Path.GetFileName(AppPaths.SettingsPath);
        Directory.CreateDirectory(settingsDirectory!);
        _watcher = new FileSystemWatcher(settingsDirectory!)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += (_, args) => MarkSettingsChanged(args.Name);
        _watcher.Created += (_, args) => MarkSettingsChanged(args.Name);
        _watcher.Deleted += (_, args) => MarkSettingsChanged(args.Name);
        _watcher.Renamed += (_, args) => MarkSettingsChanged(args.Name);
    }

    private void MarkSettingsChanged(string? fileName)
    {
        if (string.Equals(fileName, Path.GetFileName(AppPaths.SettingsPath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, AppPaths.NotificationFlashStateFileName, StringComparison.OrdinalIgnoreCase))
        {
            _settingsChanged = true;
        }
    }

    private async Task WaitForSettingsChangeAsync(int pollIntervalMs, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && !_settingsChanged)
        {
            await Task.Delay(pollIntervalMs, stoppingToken);
        }
    }

    /// <summary>
    /// 音频源状态文件对账兜底（每 5 秒）。旧模型只在 SourceChanged 事件时写文件——
    /// 漏掉一次事件（切设备瞬态解析失败、写入竞争、历史脏数据），文件就永远停在旧值，
    /// 托盘会一直显示"检测中…"，只能靠重启服务恢复（77648d6、5282a54 两次同类修复
    /// 都是在给"纯事件驱动写入"打补丁）。对账只在文件与提供方真实状态不一致时才重写，
    /// 平时零写入、零事件，不增加稳态开销。
    /// </summary>
    private async Task ReconcileAudioStatusAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var truth = new AudioSourceStatusInfo
                {
                    Status = _audioSource.Status,
                    DeviceFriendlyName = _audioSource.DeviceFriendlyName,
                    DeviceId = _audioSource.DeviceId,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                var onDisk = AudioSourceStatusFile.ReadFrom(AppPaths.AudioSourceStatusPath);
                if (onDisk is null ||
                    onDisk.Status != truth.Status ||
                    onDisk.DeviceFriendlyName != truth.DeviceFriendlyName ||
                    onDisk.DeviceId != truth.DeviceId)
                {
                    AudioSourceStatusFile.Write(truth);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audio status reconcile failed");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnAudioSourceChanged(object? sender, AudioSourceChangedEventArgs e)
    {
        // 这个回调可能在 NAudio COM 回调线程里触发；文件 IO 必须脱离它
        var snapshot = new AudioSourceStatusInfo
        {
            Status = e.Status,
            DeviceFriendlyName = e.DeviceFriendlyName,
            DeviceId = e.DeviceId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                AudioSourceStatusFile.Write(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write audio source status file");
            }
        });
    }
}

internal static class MusicAudioInputSelector
{
    public static float Select(
        MusicSettings settings,
        bool hasSelectedApplication,
        float selectedPeak,
        float systemMixLevel)
    {
        selectedPeak = Math.Clamp(selectedPeak, 0f, 1f);
        if (hasSelectedApplication || !settings.EqEnabled || !settings.AllowSystemMixFallback)
            return selectedPeak;

        return Math.Max(Math.Clamp(systemMixLevel, 0f, 1f), selectedPeak * 0.12f);
    }
}
