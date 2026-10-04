using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Simulator;

/// <summary>
/// 虚拟键盘模拟器主窗口：
/// - 单区视图：复刻 Worker 效果管线（LightingFrameGenerator.Next → SetColor），渲染与真实键盘一致；
/// - 三区视图（多分区）：按当前所选灯效的帧颜色做分区化渲染——三区各偏移色相 40°、
///   灯带取补色、亮度经 0xF4 直传（实验预览，非既有生产行为）；音乐模式同样接入；
/// - 显示模型 = 命令颜色 × 亮度档（模拟 EC 亮度对分区颜色的作用）；
/// - 全部命令经 FakeDchuTransport 记录并实时回推渲染，零真实 EC 写入。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] EffectNames =
    [
        "固定颜色", "RGB 循环", "单色呼吸", "循环呼吸", "脉冲", "心跳", "音乐模式", "关闭"
    ];

    private const string MusicEffectName = "音乐模式";

    private const int ZoneExclusiveLightbar = unchecked((int)0xF3000000u);

    private FakeDchuTransport _transport = new();
    private DchuKeyboardDevice _device;
    private KeyboardSettings _settings = BuildSettings("单色呼吸");
    private LightingFrameGenerator _generator;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private double _elapsedMs;
    private bool _threeZoneView;
    private ExternalControlServer? _externalServer;
    private bool _internalRunBeforeExternal;

    // 分区显示模型：最近一次命令的各区颜色 + 亮度档（渲染 = 颜色 × 亮度/255）
    private readonly RgbColor?[] _lastZoneColors = new RgbColor?[4];
    private byte _lastLevel = 255;

    // 音乐模式（绑定程序）：复刻 Worker.RunMusicAsync 的"已绑定播放器"路径——
    // 电平 = 绑定进程的会话峰值，节拍包络/换色由 Core 的 MusicPulseController 驱动
    private readonly AudioProgramProbe _audioProbe = new();
    private readonly MusicSettings _musicSettings = new MusicSettings().Normalize();
    private MusicPulseController? _musicController;
    private int _boundPid;
    private bool _suppressProgramSelection;

    public MainWindow(bool startInThreeZone = false, bool autostart = false, bool forceLightbar = false)
    {
        InitializeComponent();
        EffectBox.ItemsSource = EffectNames;
        _threeZoneView = startInThreeZone;
        EffectBox.SelectedIndex = 2; // 单色呼吸
        RebuildDevice();
        UpdateViewButton();
        ForceLightbarCheck.IsEnabled = _threeZoneView;
        if (_threeZoneView)
        {
            ForceLightbarCheck.IsChecked = true; // 多分区默认点亮灯带（--force-lightbar 兼容保留）
        }

        // 外接控制（默认开启）：XAML 里不挂事件避免初始化期触发，这里统一接线
        ExternalControlCheck.Checked += (_, _) => StartExternalServer();
        ExternalControlCheck.Unchecked += (_, _) => StopExternalServer();
        if (ExternalControlCheck.IsChecked == true)
        {
            StartExternalServer();
        }

        _timer.Tick += (_, _) => Tick();
        if (autostart)
        {
            _generator = new LightingFrameGenerator(BuildSettings((string)EffectBox.SelectedItem));
            _timer.Start();
            StartButton.Content = "⏸ 暂停";
        }

        Closed += (_, _) =>
        {
            _timer.Stop();
            _externalServer?.Dispose();
            _audioProbe.Dispose();
        };
    }

    private static KeyboardSettings BuildSettings(string effectName) => new()
    {
        Brightness = 70,
        Effect = BuildEffect(effectName),
    };

    private static LightingEffectSettings BuildEffect(string effectName)
    {
        var effect = new LightingEffectSettings();
        switch (effectName)
        {
            case "固定颜色":
                effect.Type = EffectType.Static;
                effect.Color = "#0080FF";
                break;
            case "RGB 循环":
                effect.Type = EffectType.Rainbow;
                effect.Step = 5;
                break;
            case "单色呼吸":
                effect.Type = EffectType.Breathing;
                effect.Color = "#0080FF";
                effect.PeriodMs = 3000;
                effect.MinimumBrightness = 10;
                break;
            case "循环呼吸":
                effect.Type = EffectType.Sequence;
                effect.Sequence = RainbowSequence(breathing: true);
                effect.PeriodMs = 3000;
                break;
            case "脉冲":
                effect.Type = EffectType.Pulse;
                effect.Sequence = RainbowSequence(breathing: false);
                effect.PeriodMs = 1200;
                break;
            case "心跳":
                effect.Type = EffectType.Heartbeat;
                effect.Sequence = RainbowSequence(breathing: false);
                effect.PeriodMs = 1600;
                break;
            case "关闭":
                effect.Type = EffectType.Off;
                break;
        }

        return effect;
    }

    private static List<SequenceColor> RainbowSequence(bool breathing) =>
    [
        new() { Color = "#FF0000", HoldMs = 400, TransitionMs = 300, Breathing = breathing },
        new() { Color = "#FF8000", HoldMs = 400, TransitionMs = 300, Breathing = breathing },
        new() { Color = "#FFFF00", HoldMs = 400, TransitionMs = 300, Breathing = breathing },
        new() { Color = "#00FF00", HoldMs = 400, TransitionMs = 300, Breathing = breathing },
        new() { Color = "#00FFFF", HoldMs = 400, TransitionMs = 300, Breathing = breathing },
        new() { Color = "#0080FF", HoldMs = 400, TransitionMs = 300, Breathing = breathing },
        new() { Color = "#FF00FF", HoldMs = 400, TransitionMs = 300, Breathing = breathing },
    ];

    private void RebuildDevice()
    {
        // 旧传输实例整体废弃（其事件订阅随之失效），无需逐个退订
        _transport = new FakeDchuTransport
        {
            Features1Result = _threeZoneView ? unchecked((int)0x00400000u) : unchecked((int)0x80000002u),
        };
        _transport.CommandSent += OnCommandSent;
        _device = new DchuKeyboardDevice(_transport);
        ResetZoneDisplayModel();
        SetStatus($"已重建虚拟设备（{(_threeZoneView ? "三区能力位，分区化渲染" : "无三区能力位，单区管线")}）");
    }

    private void Tick()
    {
        _elapsedMs += _generator.IntervalMs * (FastForwardCheck.IsChecked == true ? 8 : 1);
        if (_threeZoneView)
        {
            TickZones();
            return;
        }

        if (IsMusicMode)
        {
            TickMusic();
            return;
        }

        // 复刻 Worker 管线：LightingFrameGenerator.Next → SetColor
        var color = _generator.NextAtElapsed(ClampBrightness(), _elapsedMs);
        _device.SetColor(color);
    }

    private bool IsMusicMode => string.Equals(EffectBox.SelectedItem as string, MusicEffectName, StringComparison.Ordinal);

    /// <summary>
    /// 多分区视图：按当前所选灯效的帧颜色做分区化渲染——
    /// 三区各偏移色相 40°、灯带取补色（可选）、亮度经 0xF4 直传（不经软件缩放，
    /// 更接近真实分区管线的做法）。效果选择决定基色与明暗，音乐模式由节拍包络驱动。
    /// </summary>
    private void TickZones()
    {
        RgbColor frameColor;
        int brightnessPercent;
        if (IsMusicMode)
        {
            (frameColor, brightnessPercent) = NextMusicFrame();
        }
        else
        {
            frameColor = _generator.NextAtElapsed(100, _elapsedMs);
            brightnessPercent = ClampBrightness();
        }

        RenderZonesFromFrame(frameColor, brightnessPercent);
    }

    private void RenderZonesFromFrame(RgbColor frameColor, int brightnessPercent)
    {
        var (hue, sat, val) = ToHsv(frameColor);
        var zones = new List<(int zone, RgbColor color)>();
        for (var zone = 0; zone < 3; zone++)
        {
            zones.Add((zone, MaybeSwapBr(RgbColor.FromHsv((hue + zone * 40) % 360, sat, val))));
        }

        if (ForceLightbarCheck.IsChecked == true)
        {
            zones.Add((3, MaybeSwapBr(RgbColor.FromHsv((hue + 180) % 360, sat, val))));
        }

        var level = (byte)Math.Clamp(brightnessPercent * 255 / 100, 0, 255);
        _device.ApplyZoneStatic(zones, level);
    }

    private static (double Hue, double Sat, double Val) ToHsv(RgbColor color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue;
        if (delta == 0)
        {
            hue = 0;
        }
        else if (max == r)
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

        if (hue < 0)
        {
            hue += 360;
        }

        return (hue, max == 0 ? 0 : delta / max, max);
    }

    /// <summary>推进一帧音乐模式，返回（源颜色 0..255 未缩放, 亮度百分比）。</summary>
    private (RgbColor Color, int Brightness) NextMusicFrame()
    {
        var level = 0d;
        if (_boundPid != 0)
        {
            var peak = _audioProbe.ReadPeak(_boundPid);
            if (peak < 0)
            {
                ClearBinding("绑定已失效（进程或音频会话退出），请刷新后重新选择");
            }
            else
            {
                level = peak;
            }
        }

        _musicController ??= new MusicPulseController();
        var frame = _musicController.Next(
            _musicSettings,
            level,
            _musicSettings.FollowSystemVolume ? _audioProbe.GetMasterVolumeScalar() : 1,
            _musicSettings.Colors.Count);

        // 复刻 Worker.RunMusicAsync 的包络→亮度映射（多色指数 0.55）
        var colorCount = Math.Max(1, _musicSettings.Colors.Count);
        var sourceColor = RgbColor.FromHex(_musicSettings.Colors[frame.ColorIndex % colorCount]);
        var baseBrightness = _musicSettings.BaseBrightness;
        var peakBrightness = _musicSettings.PeakBrightness;
        var brightness = (int)Math.Clamp(
            Math.Round(baseBrightness + (peakBrightness - baseBrightness) * Math.Pow(frame.Envelope, 0.55)),
            baseBrightness, peakBrightness);
        MusicLevelBar.Width = Math.Clamp(level, 0, 1) * 220;
        return (sourceColor, brightness);
    }

    private void TickMusic()
    {
        var (color, brightness) = NextMusicFrame();
        _device.SetColor(color.Scale(brightness));
    }

    private void RefreshPrograms()
    {
        _audioProbe.Refresh();
        _suppressProgramSelection = true;
        try
        {
            ProgramBox.ItemsSource = null;
            ProgramBox.Items.Refresh();
            ProgramBox.ItemsSource = _audioProbe.Entries;
            ProgramBox.SelectedIndex = -1;
        }
        finally
        {
            _suppressProgramSelection = false;
        }

        if (_boundPid != 0)
        {
            if (_audioProbe.Entries.Any(entry => entry.ProcessId == _boundPid))
            {
                // 刷新后绑定仍在：恢复选中项
                for (var index = 0; index < _audioProbe.Entries.Count; index++)
                {
                    if (_audioProbe.Entries[index].ProcessId == _boundPid)
                    {
                        ProgramBox.SelectedIndex = index;
                        break;
                    }
                }
            }
            else
            {
                ClearBinding("原绑定进程已不在音频会话列表中，请重新选择");
            }
        }
        else
        {
            ProgramHintText.Text = "未绑定：电平恒为 0，键盘保持底亮度常亮。选择进程即绑定，随其声音起伏。";
        }
    }

    private void ClearBinding(string reason)
    {
        _boundPid = 0;
        if (!_suppressProgramSelection)
        {
            _suppressProgramSelection = true;
            ProgramBox.SelectedIndex = -1;
            _suppressProgramSelection = false;
        }

        MusicLevelBar.Width = 0;
        ProgramHintText.Text = reason;
    }

    private void OnRefreshPrograms(object sender, RoutedEventArgs e) => RefreshPrograms();

    private void OnProgramSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProgramSelection || ProgramBox.SelectedItem is not AudioProgramProbe.Entry entry)
        {
            return;
        }

        _boundPid = entry.ProcessId;
        ProgramHintText.Text = $"已绑定 {entry.ProcessName} (pid {entry.ProcessId})——键盘随其峰值电平起伏；进程退出后绑定自动失效。";
    }

    private RgbColor MaybeSwapBr(RgbColor color) =>
        SwapBrCheck.IsChecked == true
            ? new RgbColor(color.B, color.G, color.R) // 互换 B/R 字节：红↔蓝肉眼立辨
            : color;

    private int ClampBrightness() => (int)Math.Clamp(BrightnessSlider.Value, 0, 100);

    // ---- 命令回推渲染 ----

    private void OnCommandSent(int command, int args)
    {
        if (command != DchuZoneProtocol.SetDchuLedCommand)
        {
            return; // 0x67 以外不渲染
        }

        LastCommandText.Text = $"最近命令：0x{DchuZoneProtocol.SetDchuLedCommand:X2} 0x{(uint)args:X8}";

        // 整字模式表（9.7）优先 —— 这些值在位字段解析下会与单区语义混淆
        var fullWordMode = args switch
        {
            0x10000000 => "CUSTOM（静态）",
            unchecked((int)0x1002A000u) => "BREATHE",
            unchecked((int)0x33010000u) => "CYCLE",
            unchecked((int)0x80000000u) => "DANCE",
            unchecked((int)0xA0000000u) => "FLASH",
            unchecked((int)0x70000000u) => "RANDOM",
            unchecked((int)0x90000000u) => "TEMPO",
            unchecked((int)0xB0000000u) => "WAVE",
            _ => null,
        };
        if (fullWordMode is not null)
        {
            ModeText.Text = fullWordMode;
            return;
        }

        var local7 = (int)(((uint)args >> 28) & 0xF);
        var local4 = (int)(((uint)args >> 24) & 0xF);
        switch (local7)
        {
            case 0xF when local4 <= 2: // 分区/槽位颜色（单区三槽位与三区分区同编码）
                _lastZoneColors[local4] = new RgbColor(
                    (byte)((args >> 8) & 0xFF), (byte)(args & 0xFF), (byte)((args >> 16) & 0xFF));
                RepaintZone(local4);
                ModeText.Text = _threeZoneView ? "分区化渲染（CUSTOM 静态 + 0xF4）" : "静态色（单区三槽位写）";
                break;
            case 0xF when local4 == 3: // 灯带
                _lastZoneColors[3] = new RgbColor(
                    (byte)((args >> 8) & 0xFF), (byte)(args & 0xFF), (byte)((args >> 16) & 0xFF));
                RepaintZone(3);
                LightbarHintText.Text = "灯带：已点亮（0xF3）";
                break;
            case 0xF when local4 == 4: // 亮度（原始字节直传）
                _lastLevel = (byte)(args & 0xFF);
                RepaintAllZones();
                UpdateBrightnessBar(args & 0xFF, $"亮度 {args & 0xFF}/255（0x{args & 0xFF:X2}）");
                break;
            case 0x0: // 9-bit 紧致静态色（单区协议）
            {
                var local0 = args & 0x1FF;
                var r3 = local0 & 0x7;
                var g3 = (local0 >> 3) & 0x7;
                var b3 = (local0 >> 6) & 0x7;
                var brush = new SolidColorBrush(Color.FromRgb(
                    (byte)((r3 << 5) | (r3 >> 2)),
                    (byte)((g3 << 5) | (g3 >> 2)),
                    (byte)((b3 << 5) | (b3 >> 2))));
                SetAllZoneColumns(brush);
                ModeText.Text = "9-bit 静态色（单区协议）";
                break;
            }
            case 0x1:
                ModeText.Text = "硬件模式 3（0xC4/0x03）";
                break;
            case 0x2:
                ModeText.Text = "硬件模式 4（0xC4/0x04）";
                break;
            case 0x3:
                ModeText.Text = $"硬件模式 6（0xC4/0x06，参数 {local4}）";
                break;
            case 0x4:
                ModeText.Text = ((args >> 16) & 0xFF) == 0x0D ? "键盘总开关：开" : "键盘总开关：关";
                break;
            case >= 0x7 and <= 0xB:
                ModeText.Text = $"硬件模式 {local7}（0xC4/0x{local7:X2}）";
                break;
            case 0xD: // 硬件亮度档 0..9（Local2）
                var level = (args >> 12) & 0xF;
                UpdateBrightnessBar((level + 1) * 255 / 10, $"硬件亮度档 {level}/9");
                break;
            case 0xE:
                ModeText.Text = "灯效定时/选项（mode 12）";
                break;
        }
    }

    private void SetZoneColumn(int zone, Color color)
    {
        var brush = new SolidColorBrush(color);
        (zone switch
        {
            0 => Zone0Border,
            1 => Zone1Border,
            2 => Zone2Border,
            _ => LightbarBorder,
        }).Background = brush;
    }

    // 显示模型：EC 实际呈现 = 命令颜色 × 亮度档/255（呼吸/脉冲的明暗由此在多分区视图可见）
    private void RepaintZone(int zone)
    {
        if (_lastZoneColors[zone] is not { } color)
        {
            return;
        }

        var scaled = Color.FromRgb(
            (byte)(color.R * _lastLevel / 255),
            (byte)(color.G * _lastLevel / 255),
            (byte)(color.B * _lastLevel / 255));
        SetZoneColumn(zone, scaled);
    }

    private void RepaintAllZones()
    {
        for (var zone = 0; zone < 4; zone++)
        {
            RepaintZone(zone);
        }
    }

    private void ResetZoneDisplayModel()
    {
        Array.Clear(_lastZoneColors);
        _lastLevel = 255;
    }

    private void SetAllZoneColumns(Brush brush)
    {
        Zone0Border.Background = brush;
        Zone1Border.Background = brush;
        Zone2Border.Background = brush;
    }

    private void UpdateBrightnessBar(int level255, string text)
    {
        BrightnessBar.Width = Math.Clamp(level255 / 255d, 0, 1) * 220;
        BrightnessText.Text = text;
    }

    private void SetStatus(string text) => SubtitleText.Text = text;

    // ---- 交互 ----

    private void OnToggleView(object sender, RoutedEventArgs e)
    {
        _threeZoneView = !_threeZoneView;
        RebuildDevice();
        UpdateViewButton();
        ForceLightbarCheck.IsEnabled = _threeZoneView;
        if (_threeZoneView)
        {
            ForceLightbarCheck.IsChecked = true; // 多分区默认点亮灯带（补色），可手动关闭
        }
        else
        {
            LightbarBorder.Background = (Brush)FindResource("Brush.Field");
            LightbarHintText.Text = "灯带（仅收到 0xF3 时点亮）";
        }
    }

    private void UpdateViewButton()
    {
        ViewButton.Content = _threeZoneView ? "当前：三区视图（点此切回单分区）" : "当前：单分区视图（点此切三区）";
    }

    private void OnToggleRun(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled)
        {
            _timer.Stop();
            StartButton.Content = "▶ 开始";
            return;
        }

        _generator = new LightingFrameGenerator(BuildSettings((string)EffectBox.SelectedItem));
        _timer.Start();
        StartButton.Content = "⏸ 暂停";
    }

    private void OnStepOnce(object sender, RoutedEventArgs e)
    {
        if (_generator is null)
        {
            _generator = new LightingFrameGenerator(BuildSettings((string)EffectBox.SelectedItem));
        }

        Tick(); // 无加速单帧步进
    }

    private void OnEffectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EffectBox.SelectedItem is not string name)
        {
            return;
        }

        _settings = BuildSettings(name);
        _generator = new LightingFrameGenerator(_settings);
        _elapsedMs = 0;

        if (MusicPanel is null)
        {
            return; // XAML 初始化期间
        }

        var isMusic = string.Equals(name, MusicEffectName, StringComparison.Ordinal);
        MusicPanel.Visibility = isMusic ? Visibility.Visible : Visibility.Collapsed;
        _musicController = isMusic ? new MusicPulseController() : null;
        if (isMusic)
        {
            RefreshPrograms();
        }
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText is null)
        {
            return; // XAML 初始化期间
        }

        UpdateBrightnessBar((int)(e.NewValue / 100d * 255), $"亮度 {(int)e.NewValue}%（单区管线软件缩放）");
    }

    // ---- 外接控制（主项目服务 → 命名管道 → 本键盘）----

    private void StartExternalServer()
    {
        if (_externalServer is not null)
        {
            return;
        }

        _externalServer = new ExternalControlServer(
            onCommand: OnExternalCommand,
            onQuery: OnExternalQuery,
            onStatus: UpdateExternalStatus);
        _externalServer.Start();
        UpdateExternalStatus(false, 0);
    }

    private void StopExternalServer()
    {
        _externalServer?.Dispose();
        _externalServer = null;
        ResumeInternalDemo();
        ExternalStatusText.Text = "外接：已停用";
    }

    private void OnExternalCommand(int command, int args) => OnCommandSent(command, args);

    private int OnExternalQuery(int command)
    {
        // 能力探测按当前视图如实回答：三区视图报 0x00400000，单区视图报"命令不支持"
        if (command == DchuZoneProtocol.GetBiosFeatures1Command && _threeZoneView)
        {
            return unchecked((int)0x00400000u);
        }

        return unchecked((int)0x80000002u);
    }

    private void UpdateExternalStatus(bool connected, long total)
    {
        if (connected)
        {
            // 服务接管渲染期间暂停内置演示并锁定播放控制，避免两路信号叠加闪烁
            if (_timer.IsEnabled)
            {
                _internalRunBeforeExternal = true;
                _timer.Stop();
                StartButton.Content = "▶ 开始";
            }

            StartButton.IsEnabled = false;
            StepButton.IsEnabled = false;
            ExternalStatusText.Text = $"外接：服务已连接，正在接管渲染（内置演示已暂停；累计命令 {total} 条）";
        }
        else
        {
            ResumeInternalDemo();
            ExternalStatusText.Text = $"外接：等待服务连接（管道 {ExternalControlServer.PipeName}；累计命令 {total} 条）";
        }
    }

    private void ResumeInternalDemo()
    {
        StartButton.IsEnabled = true;
        StepButton.IsEnabled = true;
        if (_internalRunBeforeExternal && !_timer.IsEnabled && _generator is not null)
        {
            _timer.Start();
            StartButton.Content = "⏸ 暂停";
        }

        _internalRunBeforeExternal = false;
    }
}
