using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Tray.Wpf.Pages;

/// <summary>
/// 多分区（实验）页：左/中/右/灯带四区各自的灯效类型与颜色、灯带下发开关、服务端状态。
/// 保存经宿主窗口统一 ApplyTo(settings.MultiZone)；分区命令是否真的下发由服务端能力位门控，
/// 未命中自动回退普通灯效（状态行显示）。
/// 已知限制（v1）：每区仅支持 固定颜色/单色呼吸/RGB 循环/关闭；呼吸周期用各类型的默认值；
/// 能力位只是必要条件（P955ET1 置位但物理单分区）——单分区硬件上三区写入同址互相覆盖。
/// </summary>
public sealed class MultiZonePage : UserControl
{
    private static readonly string[] TypeLabels = ["固定颜色", "单色呼吸", "RGB 循环", "关闭"];
    private static readonly string[] SwatchColors =
    [
        "#FF0000", "#FF8000", "#FFFF00", "#00FF00", "#00FFFF", "#0080FF",
        "#0000FF", "#FF00FF", "#FFFFFF", "#B0B0B0", "#404040", "#000000"
    ];

    private readonly TextBlock _statusText = MakeHint();
    private readonly System.Windows.Controls.CheckBox _lightbarCheck = MakeCheckBox("向灯带下发命令（0xF3）");
    private readonly TextBlock _lightbarHint = MakeHint();
    private readonly System.Windows.Controls.ComboBox[] _typeCombos = new ComboBox[MultiZoneSettings.ZoneCount];
    private readonly Border[] _colorChips = new Border[MultiZoneSettings.ZoneCount];
    private readonly StackPanel[] _swatchRows = new StackPanel[MultiZoneSettings.ZoneCount];
    private readonly TextBlock[] _zoneHints = new TextBlock[MultiZoneSettings.ZoneCount];
    private readonly Border[] _zoneCards = new Border[MultiZoneSettings.ZoneCount];

    private bool _loading;

    public event EventHandler? Changed;

    public MultiZonePage()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 24, 8) };

        stack.Children.Add(MakeHintParagraph(
            "实验功能：左/中/右三区与灯带各自独立渲染所选灯效。服务端按三区能力位门控，" +
            "未命中时自动回退普通灯效；能力位命中但物理键盘为单分区时（部分机型如此），三区写入会互相覆盖。" +
            "灯带不做机型检测，请在确认机型具备灯带后再开启。"));

        _statusText.Text = "服务端状态：读取中…";
        stack.Children.Add(MakeCard("服务端状态", _statusText));

        _lightbarHint.Text = "灯带（zone 3）不参与能力位判定：不检测机型，开启即下发 0xF3；无灯带机型上该命令行为未知。";
        stack.Children.Add(MakeCard("灯带", _lightbarCheck, _lightbarHint));

        for (var zone = 0; zone < MultiZoneSettings.ZoneCount; zone++)
        {
            var zoneIndex = zone;
            var combo = MakeCombo(TypeLabels);
            combo.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                UpdateZoneControls(zoneIndex);
                MarkDirty();
            };
            _typeCombos[zone] = combo;

            var chip = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            _colorChips[zone] = chip;

            var swatches = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var color in SwatchColors)
            {
                var swatchColor = color;
                var button = new Button
                {
                    Width = 22,
                    Height = 22,
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(swatchColor)),
                    BorderThickness = new Thickness(1),
                    Focusable = false,
                };
                button.SetResourceReference(Control.BorderBrushProperty, "Brush.Border");
                button.Click += (_, _) =>
                {
                    if (_loading) return;
                    ApplySwatch(zoneIndex, swatchColor);
                };
                swatches.Children.Add(button);
            }
            _swatchRows[zone] = swatches;

            var hint = MakeHint();
            _zoneHints[zone] = hint;
            var card = MakeCard(MultiZoneSettings.ZoneName(zone),
                Row("效果", combo), Row("颜色", chip, swatches), hint);
            _zoneCards[zone] = card;
            stack.Children.Add(card);
        }

        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.SetResourceReference(StyleProperty, "DarkScrollViewer");
        Content = scroll;
    }

    public void LoadFromStore(KeyboardSettings settings)
    {
        _loading = true;
        try
        {
            var multi = settings.MultiZone;
            for (var zone = 0; zone < MultiZoneSettings.ZoneCount; zone++)
            {
                _typeCombos[zone].SelectedIndex = multi.Zones[zone].Type switch
                {
                    EffectType.Breathing => 1,
                    EffectType.Rainbow => 2,
                    EffectType.Off => 3,
                    _ => 0,
                };
                UpdateZoneControls(zone);
            }
            _lightbarCheck.IsChecked = multi.IncludeLightbar;
        }
        finally
        {
            _loading = false;
        }

        RefreshStatus();
    }

    public void ApplyTo(KeyboardSettings settings)
    {
        var multi = settings.MultiZone;
        for (var zone = 0; zone < MultiZoneSettings.ZoneCount; zone++)
        {
            var effect = multi.Zones[zone];
            effect.Type = _typeCombos[zone].SelectedIndex switch
            {
                1 => EffectType.Breathing,
                2 => EffectType.Rainbow,
                3 => EffectType.Off,
                _ => EffectType.Static,
            };
            if (_colorChips[zone].Background is SolidColorBrush brush)
            {
                effect.Color = brush.Color.ToString();
            }
        }
        multi.IncludeLightbar = _lightbarCheck.IsChecked == true;
        settings.MultiZone = multi.Normalize();
    }

    public void ResetDirty() => _dirty = false;

    public bool IsDirty => _loading ? false : _dirty;
    private bool _dirty;

    /// <summary>状态行刷新：读服务端 multizone-status（优先 IPC，其次文件）。</summary>
    public void RefreshStatus()
    {
        var status = MultiZoneStatus.Load();
        _statusText.Text = status is null
            ? "尚无服务端状态（服务未运行，或服务版本早于多分区功能）。"
            : status.Active
                ? "多分区渲染运行中（能力位已命中）。"
                : status.CapabilityDetected
                    ? "能力位已命中，但当前未在多分区渲染（未选择多分区模式或已退出）。"
                    : "能力位未命中：多分区不可用，已自动回退普通灯效管线。";
    }

    private void ApplySwatch(int zone, string colorHex)
    {
        _colorChips[zone].Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        if (_typeCombos[zone].SelectedIndex == 3)
        {
            _typeCombos[zone].SelectedIndex = 0; // 选色隐含“从关闭切到固定颜色”
        }
        UpdateZoneControls(zone);
        MarkDirty();
    }

    private void UpdateZoneControls(int zone)
    {
        var isOff = _typeCombos[zone].SelectedIndex == 3;
        var isRainbow = _typeCombos[zone].SelectedIndex == 2;
        _swatchRows[zone].IsEnabled = !isOff && !isRainbow;
        _colorChips[zone].IsEnabled = !isOff && !isRainbow;
        _colorChips[zone].Opacity = isRainbow ? 0.35 : 1;
        _zoneHints[zone].Text = _typeCombos[zone].SelectedIndex switch
        {
            1 => "以本区颜色呼吸（周期为默认值 3000 ms）。",
            2 => "全彩循环，与基色无关。",
            3 => "本区关闭（黑）。",
            _ => "本区常亮所选颜色。",
        };
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- 控件工厂（对齐 MusicPage 的风格）----

    private static Border MakeCard(string title, params UIElement[] children)
    {
        var stack = new StackPanel();
        var heading = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        stack.Children.Add(heading);
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        var card = new Border { Child = stack, Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(StyleProperty, "UiCard");
        return card;
    }

    private static StackPanel Row(string label, params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        var labelBlock = new TextBlock
        {
            Text = label,
            Width = 64,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.85,
        };
        labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        row.Children.Add(labelBlock);
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private static ComboBox MakeCombo(string[] items) => new()
    {
        ItemsSource = items,
        Width = 150,
        Height = 28,
        SelectedIndex = 0,
    };

    private static CheckBox MakeCheckBox(string text) => new()
    {
        Content = text,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 2),
    };

    private static TextBlock MakeHint() => new()
    {
        FontSize = 11,
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static TextBlock MakeHintParagraph(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
    };
}
