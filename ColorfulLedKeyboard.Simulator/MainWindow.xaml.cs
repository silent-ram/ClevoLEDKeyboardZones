using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Simulator;

/// <summary>
/// 虚拟键盘模拟器主窗口：
/// - 单区视图：复刻 Worker 效果管线（LightingFrameGenerator.Next → SetColor），渲染与真实键盘一致；
/// - 三区视图：按色相偏移独立驱动左/中/右分区（0xF0/0xF1/0xF2），灯带 0xF3 可选；
/// - 全部命令经 FakeDchuTransport 记录并实时回推渲染，零真实 EC 写入。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] EffectNames =
    [
        "固定颜色", "RGB 循环", "单色呼吸", "循环呼吸", "脉冲", "心跳", "关闭"
    ];

    private const int ZoneExclusiveLightbar = unchecked((int)0xF3000000u);

    private FakeDchuTransport _transport = new();
    private DchuKeyboardDevice _device;
    private KeyboardSettings _settings = BuildSettings("单色呼吸");
    private LightingFrameGenerator _generator;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private double _elapsedMs;
    private bool _threeZoneView;

    public MainWindow(bool startInThreeZone = false, bool autostart = false, bool forceLightbar = false)
    {
        InitializeComponent();
        EffectBox.ItemsSource = EffectNames;
        _threeZoneView = startInThreeZone;
        EffectBox.SelectedIndex = 2; // 单色呼吸
        RebuildDevice();
        UpdateViewButton();
        ForceLightbarCheck.IsEnabled = _threeZoneView;
        if (forceLightbar && _threeZoneView)
        {
            ForceLightbarCheck.IsChecked = true;
        }
        _timer.Tick += (_, _) => Tick();
        if (autostart)
        {
            _generator = new LightingFrameGenerator(BuildSettings((string)EffectBox.SelectedItem));
            _timer.Start();
            StartButton.Content = "⏸ 暂停";
        }

        Closed += (_, _) => _timer.Stop();
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
        SetStatus($"已重建虚拟设备（{(_threeZoneView ? "三区能力位" : "无三区能力位")}）");
    }

    private void Tick()
    {
        _elapsedMs += _generator.IntervalMs * (FastForwardCheck.IsChecked == true ? 8 : 1);
        if (_threeZoneView)
        {
            TickThreeZone();
        }
        else
        {
            // 复刻 Worker 管线：LightingFrameGenerator.Next → SetColor
            var color = _generator.NextAtElapsed(ClampBrightness(), _elapsedMs);
            _device.SetColor(color);
        }
    }

    private void TickThreeZone()
    {
        // 三区演示：基色相随时间推进，左/中/右各偏移 40°，灯带取补色 —— 独立着色肉眼立辨
        var baseHue = (_elapsedMs / 25.0) % 360;
        var zones = new List<(int zone, RgbColor color)>();
        for (var zone = 0; zone < 3; zone++)
        {
            var zoneColor = RgbColor.FromHsv((baseHue + zone * 40) % 360, 1, 1);
            zones.Add((zone, MaybeSwapBr(zoneColor)));
        }

        if (ForceLightbarCheck.IsChecked == true)
        {
            var lightbar = RgbColor.FromHsv((baseHue + 180) % 360, 1, 1);
            zones.Add((3, MaybeSwapBr(lightbar)));
        }

        _device.ApplyZoneStatic(zones, 255);
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
                SetZoneColumn(local4, Color.FromRgb((byte)((args >> 8) & 0xFF), (byte)(args & 0xFF), (byte)((args >> 16) & 0xFF)));
                ModeText.Text = _threeZoneView ? "静态色（三区分区写入）" : "静态色（单区三槽位写）";
                break;
            case 0xF when local4 == 3: // 灯带
                LightbarBorder.Background = new SolidColorBrush(Color.FromRgb(
                    (byte)((args >> 8) & 0xFF), (byte)(args & 0xFF), (byte)((args >> 16) & 0xFF)));
                LightbarHintText.Text = "灯带：已点亮（0xF3）";
                break;
            case 0xF when local4 == 4: // 亮度（原始字节直传）
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
            _ => Zone2Border,
        }).Background = brush;
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
        if (!_threeZoneView)
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
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText is null)
        {
            return; // XAML 初始化期间
        }

        UpdateBrightnessBar((int)(e.NewValue / 100d * 255), $"亮度 {(int)e.NewValue}%（单区管线软件缩放）");
    }
}
