using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// Tuning panel for the liquid glass experiment, modelled on glasswork's playground: every parameter live, presets, light and dark
/// materials kept apart, reset and JSON copy / paste. Settings persist through <see cref="GlassSettings"/>.
/// </summary>
public sealed partial class GlassTuningPanel : UserControl
{
    private const double BodySize = 16;
    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBlock _cost = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _status = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private bool _editDark;
    private bool _building;
    private long _costShown;

    public event Action? CloseRequested;

    public GlassTuningPanel()
    {
        FontSize = BodySize;
        var close = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new FontIcon { Glyph = "", FontSize = 16 } };
        ToolTipService.SetToolTip(close, "关闭");
        AutomationProperties.SetName(close, "关闭液态玻璃设置");
        close.Click += (_, _) => CloseRequested?.Invoke();
        var header = new Grid { ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        header.Children.Add(new TextBlock { Text = "液态玻璃", FontSize = 26, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        var root = new Grid { RowSpacing = 12, RowDefinitions = { new() { Height = GridLength.Auto }, new() { Height = GridLength.Auto }, new() { Height = new GridLength(1, GridUnitType.Star) } } };
        root.Children.Add(header);
        Grid.SetRow(_cost, 1);
        root.Children.Add(_cost);
        var scroll = new ScrollViewer { Content = _body, Padding = new Thickness(0, 0, 14, 0) };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Content = new Border { Style = (Style)Application.Current.Resources["FloatingPanel"], Padding = new Thickness(22, 16, 10, 18), Child = root };

        // The static events would keep every panel, its glass pane and their render targets alive for the whole session.
        Loaded += (_, _) => { LiquidGlass.Measured += ShowCost; GlassSettings.Changed += OnSettingsChanged; ShowCost(); };
        Unloaded += (_, _) => { LiquidGlass.Measured -= ShowCost; GlassSettings.Changed -= OnSettingsChanged; };
        Build();
        ShowCost();
    }

    private void OnSettingsChanged(bool scaleChanged)
    {
        if (!_building) ShowCost();
    }

    private void ShowCost()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _costShown < Stopwatch.Frequency / 4) return;
        _costShown = now;
        _cost.Text = GlassSettings.Current.Enabled
            ? $"每帧玻璃 {LiquidGlass.FrameMs:0.00} ms（{LiquidGlass.DrawnPanes} 块）· 每块平均 {LiquidGlass.AverageMs:0.00} ms · 峰值 {LiquidGlass.PeakMs:0.00} ms"
            : "当前为亚克力（Windows 自带材质）";
    }

    /// <summary>Rebuilds every control from the current settings (after a preset, reset or paste).</summary>
    private void Build()
    {
        _building = true;
        _body.Children.Clear();
        var s = GlassSettings.Current;

        Section("总体");
        Toggle("液态玻璃（关闭时用亚克力对比）", () => s.Enabled, v => s.Enabled = v);
        Slider("界面缩放 UI scale", 0.75, 2, 0.05, () => s.UiScale, v => s.UiScale = v, "0.00×");
        string[] themes = ["system", "light", "dark"];
        Choice("窗口主题 theme", ["跟随系统 system", "浅色 light", "深色 dark"], Array.IndexOf(themes, s.Theme), 3, i => GlassSettings.Update(g => g.Theme = themes[i]));

        Section("预设 Presets");
        Choice("预设", GlassSettings.Presets, Array.IndexOf(GlassSettings.Presets, s.Preset), 3, i =>
        {
            GlassSettings.Update(g => g.ApplyPreset(GlassSettings.Presets[i]));
            Rebuild();
        });
        _body.Children.Add(Note("预设只改折射和材质主体，保留色调颜色、主题和其他设置（与 glasswork 一致）。"));

        Section("顶部渐隐 Top fade");
        Toggle("顶部渐隐模糊", () => s.FadeOn, v => s.FadeOn = v);
        Slider("高度 height (px)", 40, 260, 1, () => s.FadeHeight, v => s.FadeHeight = v, "0");
        Slider("最大模糊 blur (px)", 0, 40, 0.5, () => s.FadeBlur, v => s.FadeBlur = v, "0.0");
        Slider("融入底色 wash", 0, 1, 0.01, () => s.FadeWash, v => s.FadeWash = v);
        Slider("强度 strength", 0, 1, 0.01, () => s.FadeOpacity, v => s.FadeOpacity = v);

        Section("边缘折射 Refraction");
        Toggle("折射 lens", () => s.Lens, v => s.Lens = v);
        Slider("边缘宽度 rim (px)", 4, 96, 1, () => s.Rim, v => s.Rim = v, "0");
        Slider("边缘偏移 edge (×rim)", 0, 2.5, 0.01, () => s.Edge, v => s.Edge = v);
        Slider("衰减 power", 1, 6, 0.1, () => s.Power, v => s.Power = v, "0.0");
        Slider("色散 dispersion", 0, 0.4, 0.005, () => s.Dispersion, v => s.Dispersion = v, "0.000");

        Section("形状 Shape");
        Slider("面板圆角 radius (px)", 0, 60, 1, () => s.Radius, v => s.Radius = v, "0");
        Slider("圆角平滑 smoothing", 0.5, 3, 0.05, () => s.Smoothing, v => s.Smoothing = v);
        _body.Children.Add(Note("工具栏和浮动工具条始终是胶囊形；圆角只作用于面板，面板内边距会随圆角自动加大，避免内容被圆角裁到。"));
        Slider("面板额外模糊 panel blur (px)", 0, 30, 0.5, () => s.PanelBlur, v => s.PanelBlur = v, "0.0");
        Slider("面板最低色调 panel tint", 0, 0.9, 0.01, () => s.PanelTint, v => s.PanelTint = v);

        Section("自适应 Adaptive");
        Choice("文字颜色 ink", ["跟随主题 theme", "自动 auto", "深色文字 dark", "浅色文字 light"], Array.IndexOf(GlassSettings.InkModes, s.Ink), 2,
            i => GlassSettings.Update(g => g.Ink = GlassSettings.InkModes[i]));
        Slider("自动切换阈值 threshold", 0.2, 0.8, 0.01, () => s.Threshold, v => s.Threshold = v);
        Toggle("色调浓度随背景 auto density", () => s.AutoDensity, v => s.AutoDensity = v);
        Slider("浓度 density（手动）", 0, 1, 0.01, () => s.Density, v => s.Density = v);
        Toggle("保证文字对比度 ≥ 4.5:1 legible", () => s.Legible, v => s.Legible = v);

        Section("材质 Material");
        Choice("编辑哪种材质", ["浅色玻璃 light", "深色玻璃 dark"], _editDark ? 1 : 0, 2, i =>
        {
            _editDark = i == 1;
            Rebuild();
        });
        var m = _editDark ? s.Dark : s.Light;
        Tint(m);
        Slider("色调强度 opacity", 0, 1.5, 0.01, () => m.Opacity, v => m.Opacity = v);
        Slider("最淡 thin", 0, 0.8, 0.01, () => m.Thin, v => m.Thin = v);
        Slider("最浓 dense", 0, 0.95, 0.01, () => m.Dense, v => m.Dense = v);
        Slider("模糊 blur (px)", 0, 40, 0.5, () => m.Blur, v => m.Blur = v, "0.0");
        Slider("饱和度 saturation", 0, 3, 0.01, () => m.Saturation, v => m.Saturation = v);
        Slider("亮度 brightness", 0.4, 1.6, 0.01, () => m.Brightness, v => m.Brightness = v);
        Slider("对比度 contrast", 0.4, 1.6, 0.01, () => m.Contrast, v => m.Contrast = v);
        Slider("边缘高光 rim light", 0, 2, 0.01, () => m.RimLight, v => m.RimLight = v);
        Slider("内斜面 bevel", 0, 2.5, 0.01, () => m.Bevel, v => m.Bevel = v);
        Slider("阴影 shadow", 0, 3, 0.01, () => m.Shadow, v => m.Shadow = v);

        Section("设置");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Action("恢复默认", () => { GlassSettings.Replace(new GlassSettings()); Rebuild(); _status.Text = "已恢复默认值。"; }));
        buttons.Children.Add(Action("复制 JSON", () =>
        {
            var package = new DataPackage();
            package.SetText(GlassSettings.Current.ToJson());
            Clipboard.SetContent(package);
            _status.Text = "已复制到剪贴板。";
        }));
        buttons.Children.Add(Action("粘贴 JSON", PasteAsync));
        _body.Children.Add(buttons);
        _body.Children.Add(_status);
        _body.Children.Add(Note("快捷键 Ctrl+Shift+L 打开或关闭本面板。设置保存在数据目录的 liquid-glass.json。材质移植自 kubai087/glasswork（MIT）。"));
        _building = false;
    }

    private void Rebuild() => DispatcherQueue.TryEnqueue(Build);

    private async void PasteAsync()
    {
        try
        {
            var text = await Clipboard.GetContent().GetTextAsync();
            if (GlassSettings.Parse(text) is not { } settings) throw new JsonException();
            GlassSettings.Replace(settings);
            Rebuild();
            _status.Text = "已应用剪贴板里的设置。";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _status.Text = "剪贴板里不是有效的液态玻璃设置 JSON。";
        }
    }

    // ---------- Control builders: large type, each change applied live ----------

    private static TextBlock Label(string text) => new() { Text = text, FontSize = BodySize };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 14, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    private void Section(string title) =>
        _body.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });

    /// <summary>A row of radio buttons (all options visible, large targets).</summary>
    private void Choice(string label, string[] options, int selected, int columns, Action<int> select)
    {
        // Plain radio buttons in a grid rather than RadioButtons, whose generated items UI Automation could not select.
        var grid = new Grid { ColumnSpacing = 8 };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var group = $"{label}-{Guid.NewGuid():N}";
        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var item = new RadioButton { Content = options[i], GroupName = group, FontSize = BodySize, IsChecked = i == Math.Max(0, selected), MinWidth = 0 };
            AutomationProperties.SetName(item, options[i]);
            item.Checked += (_, _) => { if (!_building) select(index); };
            if (i / columns >= grid.RowDefinitions.Count) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetRow(item, i / columns);
            Grid.SetColumn(item, i % columns);
            grid.Children.Add(item);
        }
        AutomationProperties.SetName(grid, label);
        _body.Children.Add(new StackPanel { Spacing = 4, Children = { Label(label), grid } });
    }

    private void Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var toggle = new ToggleSwitch { Header = Label(label), IsOn = get(), FontSize = BodySize };
        AutomationProperties.SetName(toggle, label);
        toggle.Toggled += (_, _) => { if (!_building) GlassSettings.Update(_ => set(toggle.IsOn)); };
        _body.Children.Add(toggle);
    }

    private void Slider(string label, double min, double max, double step, Func<double> get, Action<double> set, string format = "0.00")
    {
        var value = new TextBlock { FontSize = BodySize, FontWeight = FontWeights.SemiBold, Text = get().ToString(format) };
        var head = new Grid { ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        head.Children.Add(Label(label));
        Grid.SetColumn(value, 1);
        head.Children.Add(value);
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, SmallChange = step, LargeChange = step * 10, Value = get(), IsThumbToolTipEnabled = false, MinHeight = 36 };
        AutomationProperties.SetName(slider, label);
        slider.ValueChanged += (_, e) =>
        {
            value.Text = e.NewValue.ToString(format);
            if (!_building) GlassSettings.Update(_ => set(e.NewValue));
        };
        _body.Children.Add(new StackPanel { Spacing = 2, Children = { head, slider } });
    }

    private static Button Action(string label, Action click)
    {
        var button = new Button { Content = label, Style = (Style)Application.Current.Resources["ActionButton"], FontSize = BodySize, MinHeight = 40 };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The material's tint: glasswork's swatches plus a hex box.</summary>
    private void Tint(GlassMaterial material)
    {
        var swatches = _editDark
            ? new[] { ("Slate", "#1E1E22"), ("Night", "#0F1A2E"), ("Plum", "#2A1731"), ("Forest", "#10261C"), ("Ocean", "#0A2637"), ("Wine", "#2F1218"), ("Cocoa", "#2A1D14"), ("Black", "#000000") }
            : new[] { ("Clear", "#FFFFFF"), ("Warm", "#FFF3E2"), ("Sky", "#DCEBFF"), ("Rose", "#FFE0EA"), ("Mint", "#D9F6E8"), ("Lilac", "#EBE2FF"), ("Sand", "#FFE3AD"), ("Lime", "#EAF8C4") };
        var hex = new TextBox { Text = material.Tint, FontSize = BodySize, Header = Label("色调 tint"), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(hex, "色调颜色");
        hex.TextChanged += (_, _) =>
        {
            if (_building || LiquidGlass.ParseColor(hex.Text, default).A == 0) return;
            GlassSettings.Update(_ => material.Tint = "#" + hex.Text.TrimStart('#').ToUpperInvariant());
        };
        var row = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        foreach (var (name, color) in swatches)
        {
            var swatch = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeBrush"],
                Background = new SolidColorBrush(LiquidGlass.ParseColor(color, default)),
                Tag = color,
            };
            ToolTipService.SetToolTip(swatch, name);
            AutomationProperties.SetName(swatch, name);
            row.Items.Add(swatch);
        }
        row.ItemClick += (_, e) => { if (e.ClickedItem is Border { Tag: string color }) hex.Text = color; };
        _body.Children.Add(hex);
        _body.Children.Add(row);
    }
}
