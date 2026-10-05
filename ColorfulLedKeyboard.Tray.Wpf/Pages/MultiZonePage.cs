using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ColorfulLedKeyboard.Core;
using ColorfulLedKeyboard.Tray.Wpf.Controls;
using ColorfulLedKeyboard.Tray.Wpf.Dialogs;

namespace ColorfulLedKeyboard.Tray.Wpf.Pages;

/// <summary>
/// 多分区（实验）页：四区各自的灯效类型/颜色/参数、布局选择、协同效果、整套配置预设、灯带开关、
/// 服务端状态。保存经宿主窗口统一 ApplyTo(settings.MultiZone)。
/// 差异化功能：① 每区自定义拾色（拾色对话框+色板）② 每区参数（周期/最低亮度）③ 分区协同效果
/// （接力流动/氛围渐变——四区统一驱动）④ 整套配置命名预设。
/// </summary>
public sealed class MultiZonePage : UserControl
{
    private static readonly string[] TypeLabels =
        ["固定颜色", "单色呼吸", "RGB 循环", "循环呼吸", "脉冲", "心跳", "接力流动（协同）", "氛围渐变（协同）", "关闭"];

    private static readonly string[] SwatchColors =
    [
        "#FF0000", "#FF8000", "#FFFF00", "#00FF00", "#00FFFF", "#0080FF",
        "#0000FF", "#FF00FF", "#FFFFFF", "#B0B0B0", "#404040", "#000000"
    ];

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly TextBlock _statusText = MakeHint();
    private readonly TextBlock _modeHintText = MakeHint();
    private bool _statusRefreshInFlight;
    private readonly System.Windows.Controls.RadioButton _layoutZones = MakeRadio("三区 + 灯带（分区机型）");
    private readonly System.Windows.Controls.RadioButton _layoutSingle = MakeRadio("单区合并（单分区机型）");
    private readonly TextBlock _layoutHint = MakeHint();
    private readonly Button _switchModeButton;
    private Border? _lightbarSection;
    private readonly System.Windows.Controls.CheckBox _lightbarCheck = MakeCheckBox("向灯带下发命令（0xF3）");
    private readonly TextBlock _lightbarHint = MakeHint();
    private readonly ComboBox[] _typeCombos = new ComboBox[MultiZoneSettings.ZoneCount];
    private readonly Border[] _colorChips = new Border[MultiZoneSettings.ZoneCount];
    private readonly StackPanel[] _swatchRows = new StackPanel[MultiZoneSettings.ZoneCount];
    private readonly TextBlock[] _zoneHints = new TextBlock[MultiZoneSettings.ZoneCount];
    private readonly Border[] _zoneCards = new Border[MultiZoneSettings.ZoneCount];
    private readonly UiSliderRow[] _periodSliders = new UiSliderRow[MultiZoneSettings.ZoneCount];
    private readonly UiSliderRow[] _minimumSliders = new UiSliderRow[MultiZoneSettings.ZoneCount];

    // 预设栏
    private readonly ComboBox _presetCombo = new() { Width = 200, Height = 28 };
    private readonly System.Windows.Controls.TextBox _presetNameBox = new() { Width = 180, Height = 28 };
    private readonly Button _presetSaveButton = MakeButton("保存为预设", 96);
    private readonly Button _presetApplyButton = MakeButton("应用预设", 96);
    private readonly Button _presetDeleteButton = MakeButton("删除预设", 96);
    private bool _loading;

    // 分区颜色的唯一数据源（6 位 #RRGGBB 字符串）。WPF Color.ToString() 输出 8 位
    // #AARRGGBB，经 NormalizeHex 会静默回退成红色——画刷只做显示，绝不作为保存来源。
    private readonly string?[] _zoneColors = new string?[MultiZoneSettings.ZoneCount];

    // 预设编辑工作副本（宿主保存时 ApplyTo 会重新写入 settings）
    private MultiZoneSettings _working = new();

    public event EventHandler? Changed;

    /// <summary>用户点击"切换到多分区模式"：宿主窗口应切模式并保存。</summary>
    public event EventHandler? ModeSwitchRequested;

    public MultiZonePage()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 24, 8) };

        stack.Children.Add(MakeHintParagraph(
            "实验功能。渲染布局二选一：三区 + 灯带（面向真三区机型，按区独立渲染）；单区合并（面向单分区机型，" +
            "以『左分区』配置渲染，走与灯效模式相同的单区路径）。『接力流动』『氛围渐变』为分区协同效果——" +
            "四区统一驱动，配在任意区效果一致（接力流动在单分区硬件上表现为色相摆动）。灯带不做机型检测，确认机型具备后再开启。"));

        _switchModeButton = MakeButton("切换到多分区模式（并保存）", 200);
        _switchModeButton.Visibility = Visibility.Collapsed;
        _switchModeButton.Click += (_, _) => ModeSwitchRequested?.Invoke(this, EventArgs.Empty);

        _statusText.Text = "服务端状态：读取中…";
        stack.Children.Add(MakeCard("服务端状态", _statusText, _modeHintText, _switchModeButton));

        // ---- 预设栏 ----
        _presetSaveButton.Click += (_, _) => SavePreset();
        _presetApplyButton.Click += (_, _) => ApplySelectedPreset();
        _presetDeleteButton.Click += (_, _) => DeleteSelectedPreset();
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        presetRow.Children.Add(_presetCombo);
        _presetNameBox.Margin = new Thickness(8, 0, 0, 0);
        presetRow.Children.Add(_presetNameBox);
        _presetSaveButton.Margin = new Thickness(8, 0, 0, 0);
        presetRow.Children.Add(_presetSaveButton);
        _presetApplyButton.Margin = new Thickness(8, 0, 0, 0);
        presetRow.Children.Add(_presetApplyButton);
        _presetDeleteButton.Margin = new Thickness(8, 0, 0, 0);
        presetRow.Children.Add(_presetDeleteButton);
        stack.Children.Add(MakeCard("整套配置预设", presetRow,
            MakeHintParagraph("『保存为预设』把当前四区配置+布局+灯带存为命名预设（配置文件内，最多 8 个，同名覆盖）；选中后『应用预设』回填页面。")));

        // ---- 布局 ----
        var layoutRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        layoutRow.Children.Add(_layoutZones);
        _layoutSingle.Margin = new Thickness(20, 0, 0, 0);
        layoutRow.Children.Add(_layoutSingle);
        _layoutZones.Checked += (_, _) => OnLayoutChanged();
        _layoutSingle.Checked += (_, _) => OnLayoutChanged();
        stack.Children.Add(MakeCard("渲染布局", layoutRow, _layoutHint));

        // ---- 灯带 ----
        _lightbarSection = MakeCard("灯带", _lightbarCheck, _lightbarHint);
        stack.Children.Add(_lightbarSection);

        // ---- 四区卡片 ----
        // 灯带（zone3）不参与协同效果——其下拉不含协同项，避免"选了被 Normalize 静默打回"的界面不一致
        var lightbarTypeLabels = new[] { "固定颜色", "单色呼吸", "RGB 循环", "关闭" };
        for (var zone = 0; zone < MultiZoneSettings.ZoneCount; zone++)
        {
            var zoneIndex = zone;
            var combo = new ComboBox
            {
                ItemsSource = zone == 3 ? lightbarTypeLabels : TypeLabels,
                Width = 150,
                Height = 28,
                SelectedIndex = 0,
            };
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
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "点击打开拾色器",
            };
            chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            chip.MouseLeftButtonUp += (_, _) => OpenColorPicker(zoneIndex);
            _colorChips[zone] = chip;

            var pickButton = MakeButton("自定义…", 72);
            pickButton.Height = 24;
            pickButton.Click += (_, _) => OpenColorPicker(zoneIndex);

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

            _periodSliders[zone] = new UiSliderRow("周期", 300, 30000, " ms");
            _minimumSliders[zone] = new UiSliderRow("最低亮度", 0, 100, "%");
            _periodSliders[zone].ValueChanged += (_, _) => MarkDirty();
            _minimumSliders[zone].ValueChanged += (_, _) => MarkDirty();

            var hint = MakeHint();
            _zoneHints[zone] = hint;
            var card = MakeCard(MultiZoneSettings.ZoneName(zone),
                Row("效果", combo), Row("颜色", chip, pickButton, swatches),
                _periodSliders[zone], _minimumSliders[zone], hint);
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
            _working = multi; // 预设编辑工作副本（ApplyTo 时整体写回）
            for (var zone = 0; zone < MultiZoneSettings.ZoneCount; zone++)
            {
                _typeCombos[zone].SelectedIndex = zone == 3 && MultiZoneCooperativeEffects.IsCooperative(multi.Zones[zone].Type)
                    ? 0
                    : ZoneTypeToIndex(multi.Zones[zone].Type);
                _zoneColors[zone] = multi.Zones[zone].Color;
                _colorChips[zone].Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(multi.Zones[zone].Color));
                _periodSliders[zone].Value = multi.Zones[zone].PeriodMs;
                _minimumSliders[zone].Value = multi.Zones[zone].MinimumBrightness;
                UpdateZoneControls(zone);
            }

            _lightbarCheck.IsChecked = multi.IncludeLightbar;
            _layoutZones.IsChecked = multi.Layout != MultiZoneLayout.SingleMerged;
            _layoutSingle.IsChecked = multi.Layout == MultiZoneLayout.SingleMerged;
            RefreshPresetCombo(multi);
        }
        finally
        {
            _loading = false;
        }

        UpdateLayoutVisibility();

        var mismatch = settings.OperatingMode != OperatingMode.MultiZone;
        _modeHintText.Text = mismatch
            ? "当前模式不是多分区（配置不会生效）。点下方按钮一键切换并保存。"
            : "";
        _switchModeButton.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;

        RefreshStatus();
    }

    public void ApplyTo(KeyboardSettings settings)
    {
        var multi = CaptureCurrent();
        settings.MultiZone = multi;
        RefreshPresetCombo(multi);
    }

    public void ResetDirty() => _dirty = false;

    public bool IsDirty => _loading ? false : _dirty;
    private bool _dirty;

    private static int ZoneTypeToIndex(EffectType type) => type switch
    {
        EffectType.Breathing => 1,
        EffectType.Rainbow => 2,
        EffectType.Sequence => 3,
        EffectType.Pulse => 4,
        EffectType.Heartbeat => 5,
        EffectType.RelayFlow => 6,
        EffectType.AmbientGradient => 7,
        EffectType.Off => 8,
        _ => 0,
    };

    private static EffectType ZoneIndexToType(int index) => index switch
    {
        1 => EffectType.Breathing,
        2 => EffectType.Rainbow,
        3 => EffectType.Sequence,
        4 => EffectType.Pulse,
        5 => EffectType.Heartbeat,
        6 => EffectType.RelayFlow,
        7 => EffectType.AmbientGradient,
        8 => EffectType.Off,
        _ => EffectType.Static,
    };

    // ---- 预设 ----

    private void RefreshPresetCombo(MultiZoneSettings multi)
    {
        var names = multi.Presets.Select(preset => preset.Name).ToList();
        _presetCombo.ItemsSource = names;
        _presetCombo.SelectedIndex = names.Count == 0 ? -1 : 0;
    }

    private void SavePreset()
    {
        if (_loading) return;
        var name = _presetNameBox.Text.Trim();
        if (name.Length == 0)
        {
            System.Windows.MessageBox.Show("请先在文本框输入预设名称。", "多分区预设",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var multi = CaptureCurrent();
        var presets = multi.Presets.ToList();
        presets.RemoveAll(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)); // 同名覆盖
        while (presets.Count >= MultiZoneSettings.MaxPresets)
        {
            presets.RemoveAt(0); // 满额挤掉最旧，新预设可见（此前 Take(8) 会静默丢掉最新）
        }

        presets.Add(multi.CapturePreset(name));
        multi.Presets = presets;
        _working = multi.Normalize();
        _presetNameBox.Text = "";
        RefreshPresetCombo(_working);
        MarkDirty();
    }

    private void ApplySelectedPreset()
    {
        if (_loading) return;
        if (_presetCombo.SelectedItem is not string name)
        {
            System.Windows.MessageBox.Show("请先选择一个预设。", "多分区预设",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var multi = CaptureCurrent();
        var preset = multi.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return;

        multi.ApplyPreset(preset);
        _working = multi;
        _loading = true;
        try
        {
            for (var zone = 0; zone < MultiZoneSettings.ZoneCount; zone++)
            {
                _typeCombos[zone].SelectedIndex = zone == 3 && MultiZoneCooperativeEffects.IsCooperative(multi.Zones[zone].Type)
                    ? 0
                    : ZoneTypeToIndex(multi.Zones[zone].Type);
                _zoneColors[zone] = multi.Zones[zone].Color;
                _colorChips[zone].Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(multi.Zones[zone].Color));
                _periodSliders[zone].Value = multi.Zones[zone].PeriodMs;
                _minimumSliders[zone].Value = multi.Zones[zone].MinimumBrightness;
                UpdateZoneControls(zone);
            }

            _lightbarCheck.IsChecked = multi.IncludeLightbar;
            _layoutZones.IsChecked = multi.Layout != MultiZoneLayout.SingleMerged;
            _layoutSingle.IsChecked = multi.Layout == MultiZoneLayout.SingleMerged;
        }
        finally
        {
            _loading = false;
        }

        UpdateLayoutVisibility();
        MarkDirty();
    }

    private void DeleteSelectedPreset()
    {
        if (_loading || _presetCombo.SelectedItem is not string name) return;
        var multi = CaptureCurrent();
        var preset = multi.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return;

        multi.Presets.Remove(preset);
        _working = multi.Normalize();
        RefreshPresetCombo(_working);
        MarkDirty();
    }

    /// <summary>当前页面状态打包（含预设列表）。</summary>
    private MultiZoneSettings CaptureCurrent()
    {
        var multi = _working;
        multi.Layout = _layoutSingle.IsChecked == true ? MultiZoneLayout.SingleMerged : MultiZoneLayout.Zones3;
        multi.IncludeLightbar = _lightbarCheck.IsChecked == true;
        for (var zone = 0; zone < MultiZoneSettings.ZoneCount; zone++)
        {
            var effect = multi.Zones[zone];
            effect.Type = ZoneIndexToType(_typeCombos[zone].SelectedIndex);
            if (_zoneColors[zone] is { Length: > 0 } color)
            {
                effect.Color = color;
            }

            effect.PeriodMs = (int)_periodSliders[zone].Value;
            effect.MinimumBrightness = (int)_minimumSliders[zone].Value;
        }

        return multi.Normalize();
    }

    // ---- 状态与交互 ----

    /// <summary>状态行刷新：后台线程读取，Dispatcher 回 UI。</summary>
    public void RefreshStatus()
    {
        if (_statusRefreshInFlight)
        {
            return;
        }

        _statusRefreshInFlight = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            MultiZoneStatus? status = null;
            try { status = MultiZoneStatus.Load(); }
            catch { }
            _dispatcher.BeginInvoke(() =>
            {
                _statusRefreshInFlight = false;
                ApplyStatus(status);
            });
        });
    }

    private void ApplyStatus(MultiZoneStatus? status)
    {
        _statusText.Text = status is null
            ? "尚无服务端状态（服务未运行，或服务版本早于多分区功能）。"
            : status.Active
                ? "多分区渲染运行中。"
                : status.CapabilityDetected
                    ? "能力位已命中，但当前未在多分区渲染（未选择多分区模式或已退出）。"
                    : "能力位未命中：三区布局不可用并已回退普通灯效管线（单区合并布局不受影响）。";
    }

    private void OpenColorPicker(int zone)
    {
        if (_loading) return;
        var current = _zoneColors[zone] ?? "#FF0000";
        var dialog = new ColorSelectionDialog([current], singleSelection: true)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() == true && dialog.SelectedColors is { Count: > 0 } picked)
        {
            ApplySwatch(zone, picked[0]);
        }
    }

    private void ApplySwatch(int zone, string colorHex)
    {
        _zoneColors[zone] = colorHex;
        _colorChips[zone].Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        if (_typeCombos[zone].SelectedIndex == 8)
        {
            _typeCombos[zone].SelectedIndex = 0; // 选色隐含“从关闭切到固定颜色”
        }

        UpdateZoneControls(zone);
        MarkDirty();
    }

    private void OnLayoutChanged()
    {
        if (_loading) return;
        UpdateLayoutVisibility();
        MarkDirty();
    }

    private void UpdateLayoutVisibility()
    {
        var single = _layoutSingle.IsChecked == true;
        _zoneCards[1].Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        _zoneCards[2].Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        _zoneCards[3].Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        _lightbarSection.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        _layoutHint.Text = single
            ? "整块键盘按『左分区』的配置渲染（与灯效模式相同的单区路径，适合单分区机型）。其余分区配置保留，切回三区布局后恢复生效。"
            : "左/中/右/灯带按区独立渲染（面向真三区机型；亮度经 0xF4 硬件亮度）。";
    }

    private void UpdateZoneControls(int zone)
    {
        var index = _typeCombos[zone].SelectedIndex;
        var isOff = index == 8;
        var usesSequence = index is 2 or 3 or 4 or 5; // 循环类：颜色由序列决定
        _swatchRows[zone].IsEnabled = !isOff && !usesSequence;
        _colorChips[zone].IsEnabled = !isOff && !usesSequence;
        _colorChips[zone].Opacity = usesSequence ? 0.35 : 1;
        var periodVisible = index is 1 or 6 or 7; // 呼吸/接力/渐变有周期语义
        _periodSliders[zone].Visibility = periodVisible && !isOff ? Visibility.Visible : Visibility.Collapsed;
        _minimumSliders[zone].Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed; // 仅单色呼吸
        _zoneHints[zone].Text = index switch
        {
            1 => "以本区颜色呼吸；周期与最低亮度可调。",
            2 => "全彩循环（默认六色序列），与基色无关。",
            3 => "循环呼吸（默认六色序列，带呼吸过渡）。",
            4 => "脉冲（默认六色序列）。",
            5 => "心跳（默认六色序列）。",
            6 => "接力流动：四区共享色相时间轴，左→中→右依次推进，灯带补色（单分区硬件上表现为色相摆动）。基色=色相锚点。",
            7 => "氛围渐变：左=基色、右=辅助色（序列首色，缺省取补色）、中间插值，缓慢呼吸；灯带取中间色。",
            8 => "本区关闭（黑）。",
            _ => "本区常亮所选颜色。",
        };
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- 控件工厂 ----

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

    private static System.Windows.Controls.RadioButton MakeRadio(string text) => new()
    {
        Content = text,
        GroupName = "MultiZoneLayout",
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static CheckBox MakeCheckBox(string text) => new()
    {
        Content = text,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 2, 0, 2),
    };

    private static Button MakeButton(string text, double minWidth = 112) => new()
    {
        Content = text,
        MinWidth = minWidth,
        Height = 30,
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
