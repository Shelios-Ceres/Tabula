using InfiniteCanvas.App.Pages;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace InfiniteCanvas.App.Board;

/// <summary>Generation panel under the selected node: prompt, model, per-family parameters and the generate button.</summary>
public sealed partial class PromptPanel : UserControl
{
    private static readonly string[] Voices = ["alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar", "fable", "onyx", "nova"];
    private readonly CanvasView _host;
    private CanvasNode? _node;
    private List<string> _models = [];
    private bool _loading;
    private Panel _params = null!;
    /// <summary>Set while rebuilding the open flyout, so the 高级 section the user is working in stays expanded.</summary>
    private bool _keepAdvanced;

    public PromptPanel(CanvasView host)
    {
        InitializeComponent();
        _host = host;
        // Keep panel interaction from starting canvas gestures underneath.
        Shell.PointerPressed += (_, e) => e.Handled = true;
        Shell.PointerReleased += (_, e) => e.Handled = true;
        Shell.PointerMoved += (_, e) => e.Handled = true;
        Shell.PointerWheelChanged += (_, e) => e.Handled = true;
        Shell.DoubleTapped += (_, e) => e.Handled = true;
        Shell.RightTapped += (_, e) => e.Handled = true;
    }

    public CanvasNode? Node => _node;

    /// <summary>Shadow goes on the rounded shell, not the control, so it follows the corners.</summary>
    public void CastShadowOn(UIElement receiver)
    {
        var shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
        shadow.Receivers.Add(receiver);
        Shell.Shadow = shadow;
        Shell.Translation = new System.Numerics.Vector3(0, 0, 32);
    }
    private static AppSettings Settings => SettingsStore.Current;
    private Capability Capability => _node is null ? Capability.Image : CanvasGenerator.CapabilityOf(_node);

    public void Bind(CanvasNode? node)
    {
        // Open flyouts belong to the previous node; close them when another node takes over (e.g. an agent changed the selection).
        if (node != _node)
        {
            ParamsFlyout.Hide();
            _library?.Hide();
        }
        _node = node;
        if (node is null) return;
        _loading = true;
        ModeBar.Visibility = node.Kind == NodeKind.Config ? Visibility.Visible : Visibility.Collapsed;
        ModeBar.SelectedItem = ModeBar.Items.FirstOrDefault(item => (string)item.Tag == node.Mode.ToString());
        PromptBox.Text = node.Prompt ?? "";
        PromptBox.PlaceholderText = Capability switch
        {
            Capability.Audio => "输入要朗读的文字，Ctrl+Enter 开始生成",
            Capability.Text => "输入要让模型完成的任务，Ctrl+Enter 开始生成",
            _ => node.HasContent && node.Kind == NodeKind.Image ? "描述要如何修改这张图片，结果会生成到右侧新节点" : "描述想要生成的内容，Ctrl+Enter 开始生成",
        };
        _loading = false;
        Refresh();
    }

    /// <summary>Re-read everything that depends on settings, connections or running state.</summary>
    public void Refresh()
    {
        if (_node is null) return;
        _loading = true;
        var capability = Capability;
        _node.Model = Settings.ModelFor(capability, _node.Model);
        _models = Settings.ModelsFor(capability).ToList();
        ModelBox.ItemsSource = _models.Select(Settings.Label).ToList();
        ModelBox.SelectedIndex = _models.IndexOf(_node.Model);
        // Same list and order the request numbers 图片N with, so the node's own image counts as 图片1.
        var inputs = CanvasGenerator.InputsOf(_host.Doc, _node);
        ShowInputs(inputs);
        HeaderRow.Visibility = ModeBar.Visibility == Visibility.Visible || inputs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowMarks();
        ShowHint();
        ParamsText.Text = ParamsSummary();
        ShowEstimate();
        var running = _host.Generator.IsRunning(_node.Id);
        GenerateText.Text = running ? "停止" : "生成";
        GenerateIcon.Glyph = running ? "" : "";
        _loading = false;
    }

    private void ShowInputs(List<GenerationResource> inputs)
    {
        InputThumbs.Children.Clear();
        var images = inputs.Where(r => r.Type == NodeKind.Image).ToList();
        foreach (var resource in images.Take(5))
        {
            var path = ProjectStore.AssetPath(_host.Doc.Id, resource.Node.Content!);
            var thumb = new Border { Width = 28, Height = 28, CornerRadius = new(4), Child = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill, Source = new BitmapImage { DecodePixelWidth = 56, UriSource = new Uri(path) } } };
            ToolTipService.SetToolTip(thumb, $"图片{images.IndexOf(resource) + 1}：{resource.Node.Title}");
            InputThumbs.Children.Add(thumb);
        }
        var others = inputs.Count - Math.Min(images.Count, 5);
        InputsText.Text = inputs.Count == 0 ? "" : others > 0 ? $"+{others} 个输入" : $"{inputs.Count} 个输入";
    }

    /// <summary>Local-edit entry for image nodes whose current model supports a mask or point / box editing, plus any text the model returned.</summary>
    private void ShowMarks()
    {
        var node = _node!;
        var (mask, points) = _host.MarkSupport(node);
        var show = node.Kind == NodeKind.Image && node.HasContent && (mask || points);
        MarksRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            var parts = new List<string>();
            if (mask && node.Option(NodeKeys.Mask).Length > 0) parts.Add("已涂抹重绘区域");
            if (points && NodeKeys.SelectionsOf(node).Count is > 0 and var count) parts.Add($"已标记 {count} 处位置");
            MarksText.Text = parts.Count > 0 ? string.Join(" · ", parts) : mask ? "涂抹要重绘的区域，其余保持不变" : "点选或框选要修改的位置";
            ClearMarksButton.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        var notes = new[] { (Label: "模型说明", Text: node.Option(NodeKeys.ModelText)), (Label: "参考图", Text: node.Option(NodeKeys.InputNote)) }.Where(n => n.Text.Length > 0).ToList();
        ModelText.Text = string.Join("\n", notes.Select(n => $"{n.Label}：{n.Text}"));
        ModelText.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A missing model or API key shows up front with a way to settings; the generator's dialogs still cover the menu and Ctrl+Enter.</summary>
    private void ShowHint()
    {
        var node = _node!;
        var running = _host.Generator.IsRunning(node.Id);
        var resolved = Settings.Resolve(node.Model);
        // ProtectedKey avoids a DPAPI decrypt on every refresh.
        var hint = resolved is null ? $"还没有可用的{Labels.Name(Capability)}模型"
            : string.IsNullOrEmpty(resolved.Value.Channel.ProtectedKey) ? $"服务商「{Settings.ChannelName(resolved.Value.Channel)}」还没有填写 API Key" : null;
        HintText.Text = hint ?? "";
        HintRow.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        GenerateButton.IsEnabled = running || hint is null;
    }

    /// <summary>Expected cost of the next image run from the model, parameters, prompt and connected pictures; image runs only.</summary>
    private void ShowEstimate()
    {
        var node = _node!;
        var resolved = Settings.Resolve(node.Model);
        var show = Capability == Capability.Image && resolved is not null;
        EstimateText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var references = CanvasGenerator.InputsOf(_host.Doc, node).Count(r => r.Type == NodeKind.Image);
        EstimateText.Text = Prices.EstimateImage(resolved!.Value.Channel, resolved.Value.Model.Name, node, references) is { } estimate
            ? $"预计花费 ≈ {estimate.Text}"
            : "预计花费：这个模型和设置还没有官方单价，生成一次后按实际用量估算";
    }

    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (await _host.ConfirmLeaveAsync()) App.Window.Frame.Navigate(typeof(SettingsPage), null, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
    }

    /// <summary>Count, size and ratio of an image on the parameters button, defaulted the way the flyout shows them.</summary>
    private string ParamsSummary()
    {
        var node = _node!;
        var resolved = Settings.Resolve(node.Model);
        var parts = new List<string>();
        if (Capability == Capability.Image)
        {
            if (node.Count > 1) parts.Add($"{node.Count} 张");
            if (resolved is { } ri && ModelFamilies.FindImage(ri.Channel.Vendor, ri.Model.Name) is { } image)
                foreach (var value in new[] { image.Tiers.Contains(node.Size) ? node.Size : image.Tiers.FirstOrDefault(), image.Ratios.Contains(node.Ratio) ? node.Ratio : image.Ratios.FirstOrDefault() })
                    if (value is { Length: > 0 } && value != "auto") parts.Add(value);
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "参数";
    }

    private async void EditMarks_Click(object sender, RoutedEventArgs e)
    {
        if (_node is null) return;
        await _host.EditMarksAsync(_node);
        Refresh();
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        if (_node is null) return;
        _host.ClearMarks(_node);
        Refresh();
    }

    private void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_loading || _node is null || sender.SelectedItem?.Tag is not string tag || tag == _node.Mode.ToString()) return;
        _host.History.Record();
        _node.Mode = Enum.Parse<Capability>(tag);
        Bind(_node);
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged arrives after Bind has cleared _loading, so only real edits are written.
        if (_node is null || PromptBox.Text == NodeView.BoxText(_node.Prompt)) return;
        _host.History.Record($"{_node.Id}/prompt");
        _node.Prompt = PromptBox.Text.Replace('\r', '\n');
        ShowEstimate();
    }

    private void PromptBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { e.Handled = true; _host.FocusCanvas(); return; }
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key != VirtualKey.Enter || !ctrl) return;
        e.Handled = true;
        // Holding the keys auto-repeats KeyDown; only the first press toggles generation.
        if (!e.KeyStatus.WasKeyDown) Generate_Click(sender, e);
    }

    public void FocusPrompt()
    {
        PromptBox.Focus(FocusState.Keyboard);
        PromptBox.SelectionStart = PromptBox.Text.Length;
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _node is null || ModelBox.SelectedIndex < 0 || _models[ModelBox.SelectedIndex] == _node.Model) return;
        _host.History.Record();
        _node.Model = _models[ModelBox.SelectedIndex];
        ShowMarks();
        ShowHint();
        ParamsText.Text = ParamsSummary();
        ShowEstimate();
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_node is null) return;
        if (_host.Generator.IsRunning(_node.Id)) _host.Generator.Cancel(_node.Id);
        else await _host.Generator.GenerateAsync(_node);
    }

    /// <summary>The flyout hangs off the whole panel's right edge so it never covers the prompt.</summary>
    private void Params_Click(object sender, RoutedEventArgs e) => FlyoutBase.ShowAttachedFlyout(Shell);

    private PromptLibraryFlyout? _library;

    private void PromptLibrary_Click(object sender, RoutedEventArgs e) => (_library ??= new PromptLibraryFlyout(() => PromptBox.Text, UsePrompt)).ShowAt(Shell);

    /// <summary>A library prompt replaces the selected text, or goes after what is already written; the edit is one undo step like typing.</summary>
    private void UsePrompt(string text)
    {
        if (_node is null) return;
        text = NodeView.BoxText(text);
        if (PromptBox.SelectionLength > 0) PromptBox.SelectedText = text;
        else PromptBox.Text = PromptBox.Text.Trim().Length == 0 ? text : PromptBox.Text.TrimEnd('\r') + "\r" + text;
        FocusPrompt();
    }

    private void ParamsFlyout_Closed(object sender, object e)
    {
        if (_node is not null) ParamsText.Text = ParamsSummary();
    }

    private void ParamsFlyout_Opening(object sender, object e)
    {
        _params = ParamsPanel;
        ParamsPanel.Children.Clear();
        if (_node is null) return;
        var node = _node;
        var resolved = Settings.Resolve(node.Model);
        var vendor = resolved?.Channel.Vendor;
        var name = resolved?.Model.Name ?? "";
        switch (Capability)
        {
            case Capability.Image:
                Combo("张数", Enumerable.Range(1, 15).Select(i => (i.ToString(), $"{i} 张")), Math.Clamp(node.Count, 1, 15).ToString(), value => node.Count = int.Parse(value));
                var image = vendor is { } v ? ModelFamilies.FindImage(v, name) : null;
                if (image is null) { Note("当前模型没有已知的能力配置，将使用接口默认参数。"); break; }
                if (image.Tiers.Length > 0) Combo("分辨率", image.Tiers.Select(t => (t, t == "auto" ? "自动" : t)), node.Size ?? image.Tiers[0], value => node.Size = value);
                if (image.Ratios.Length > 0) Combo("比例", image.Ratios.Select(r => (r, r == "auto" ? "自动" : r)), node.Ratio ?? image.Ratios[0], value => node.Ratio = value);
                if (image.Qualities.Length > 0) Combo("质量", image.Qualities.Select(q => (q, q == "auto" ? "自动" : q)), node.Quality ?? "auto", value => node.Quality = value);
                if (image.OutputFormats.Length > 1) Combo("输出格式", image.OutputFormats.Select(f => (f, f.ToUpperInvariant())), node.Option("outputFormat", image.OutputFormats[0]), value => node.SetOption("outputFormat", value));
                if (image.Background) Combo("背景", [("auto", "自动"), ("transparent", "透明"), ("opaque", "不透明")], node.Option("background", "auto"), value => node.SetOption("background", value));
                if (vendor == Vendor.OpenAI)
                {
                    Combo("审核强度", [("auto", "标准"), ("low", "宽松")], node.Option("moderation", "auto"), value => node.SetOption("moderation", value));
                    var advanced = new StackPanel { Spacing = 4 };
                    _params.Children.Add(new Expander { Header = "高级", Content = advanced, IsExpanded = _keepAdvanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                    _keepAdvanced = false;
                    _params = advanced;
                    Combo("调用方式", [("images", "Image API"), ("responses", "Responses 图像工具")], node.Option("openaiApi", "images"), value => node.SetOption("openaiApi", value));
                    Note("Responses 以后台任务提交后轮询结果，4K、最高画质等长时间生成不怕代理断线，关闭客户端后重开画布也会接着取结果。");
                    var texts = resolved!.Value.Channel.Models.Where(m => m.Capability == Capability.Text).Select(m => (m.Name, AppSettings.DisplayName(resolved.Value.Channel, m))).ToList();
                    if (texts.Count > 0)
                        Combo("Responses 主模型", texts, node.Option("mainModel", texts[0].Item1), value =>
                        {
                            // Each model has its own effort levels: start the new one at its lowest and refresh the list.
                            node.SetOption("mainModel", value);
                            node.SetOption("mainReasoning", null);
                            _keepAdvanced = true;
                            DispatcherQueue.TryEnqueue(() => ParamsFlyout_Opening(ParamsPanel, EventArgs.Empty));
                        });
                    else Note("Responses 需要同一服务商下的文本模型作为主模型：请先在设置里给 OpenAI 添加一个文本模型（如 gpt-6-luna）。");
                    var mainModel = CanvasGenerator.MainModelOf(node, resolved!.Value.Channel);
                    if (ModelFamilies.ReasoningEfforts(mainModel) is { Length: > 0 } efforts)
                        Combo("主模型思考强度", efforts.Select(e => (e, EffortLabels[e])), ModelFamilies.SnapReasoning(mainModel, node.Option("mainReasoning"))!, value => node.SetOption("mainReasoning", value));
                    Toggle("流式预览（Image API）", node.Option("stream") == "true", value => node.SetOption("stream", value ? "true" : null));
                    Combo("中间预览张数（额外计费）", [("0", "0"), ("1", "1"), ("2", "2"), ("3", "3")], node.Option("partialImages", "0"), value => node.SetOption("partialImages", value));
                    Toggle("多轮对话改图（Responses）", node.Option("store") == "true", value => node.SetOption("store", value ? "true" : null));
                    Note("开启后本轮会话保存在 OpenAI；之后在生成结果上继续生成，会接着上一轮修改，保持主体一致。仅 Responses 调用方式生效。");
                    _params = ParamsPanel;
                }
                if (vendor == Vendor.Google)
                {
                    Toggle("多轮对话改图", node.Option("store") == "true", value => node.SetOption("store", value ? "true" : null));
                    Note("开启后本轮会话保存在 Google；之后在生成结果上继续生成，会接着上一轮修改，保持主体一致。");
                }
                if (image.Interactive)
                {
                    Toggle("图层拆分", node.Option("layers") == "true", value => node.SetOption("layers", value ? "true" : null));
                    Toggle("透明背景", node.Option("transparent") == "true", value => node.SetOption("transparent", value ? "true" : null));
                    Note("图层拆分：把 1 张输入图拆成底图和最多 16 个透明图层，按原位置铺到画布并编组。透明背景：输入 1 张带透明通道的图，输出保留透明。两者都只接受 1 张输入图片。");
                }
                if (image.ThinkingLevels.Length > 1) Combo("思考强度", image.ThinkingLevels.Select(t => (t, t == "auto" ? "自动" : t)), node.Option("thinking", "auto"), value => node.SetOption("thinking", value));
                if (image.WebSearch) Toggle("联网搜索", node.Option("webSearch") == "true", value => node.SetOption("webSearch", value ? "true" : null));
                if (image.ImageSearch) Toggle("图片搜索", node.Option("imageSearch") == "true", value => node.SetOption("imageSearch", value ? "true" : null));
                if (vendor == Vendor.Ark)
                {
                    if (image.Stream) Toggle("流式返回", node.Option("stream") == "true", value => node.SetOption("stream", value ? "true" : null));
                    if (image.FastPrompt) Toggle("快速提示词优化", node.Option("fastPrompt") == "true", value => node.SetOption("fastPrompt", value ? "true" : null));
                }
                if (vendor == Vendor.Qwen)
                {
                    Toggle("提示词智能改写", node.Option("promptExtend", "true") == "true", value => node.SetOption("promptExtend", value ? "true" : "false"));
                    Toggle("思考模式（出图更好，耗时更长）", node.Option("qwenThinking", "true") == "true", value => node.SetOption("qwenThinking", value ? "true" : "false"));
                    var advanced = new StackPanel { Spacing = 4 };
                    _params.Children.Add(new Expander { Header = "高级", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                    _params = advanced;
                    Combo("改写方式", [("direct", "直接增强"), ("agent", "智能体增强（仅文生图）")], node.Option("promptExtendMode", "direct"), value => node.SetOption("promptExtendMode", value));
                    Text("种子（留空随机，固定后结果更稳定）", node.Option("seed"), value => node.SetOption("seed", long.TryParse(value.Trim(), out var seed) ? seed.ToString() : null));
                    _params = ParamsPanel;
                }
                if (image.NegativePrompt) Text("反向提示词", node.Option("negativePrompt"), value => node.SetOption("negativePrompt", value));
                if (vendor is Vendor.Ark or Vendor.Qwen) Toggle("添加水印", node.Option("watermark") == "true", value => node.SetOption("watermark", value ? "true" : null));
                break;
            case Capability.Text:
                Combo("推理强度", [("auto", "自动"), ("low", "低"), ("medium", "中"), ("high", "高"), ("xhigh", "极高")], node.Option("reasoning", "auto"), value => node.SetOption("reasoning", value));
                Note("系统提示词可在设置页统一配置。");
                break;
            case Capability.Audio:
                if (vendor == Vendor.Google)
                {
                    // Gemini reads the text verbatim; how to say it goes in the style, and the output is always WAV.
                    var voice = ModelFamilies.GeminiVoices.FirstOrDefault(v => v.Equals(node.Option("voice"), StringComparison.OrdinalIgnoreCase)) ?? ModelFamilies.GeminiVoices[0];
                    Combo("音色", ModelFamilies.GeminiVoices.Select(v => (v, v)), voice, value => node.SetOption("voice", value));
                    Text("语气与风格（可选，如：轻声、语速放慢）", node.Option("instructions"), value => node.SetOption("instructions", value));
                    Note("输出为 WAV。文本会逐字朗读，可在文本里插入 <short pause>、<laugh> 等英文标签控制停顿和笑声。");
                    break;
                }
                if (vendor != Vendor.OpenAI) Note("语音生成目前支持 OpenAI 与 Google Gemini。");
                Combo("音色", Voices.Select(v => (v, v)), node.Option("voice", "alloy"), value => node.SetOption("voice", value));
                Combo("格式", new[] { "mp3", "wav", "opus", "aac", "flac" }.Select(f => (f, f.ToUpperInvariant())), node.Option("audioFormat", "mp3"), value => node.SetOption("audioFormat", value));
                Combo("语速", new[] { "0.5", "0.75", "1", "1.25", "1.5", "2" }.Select(s => (s, $"{s}×")), node.Option("speed", "1"), value => node.SetOption("speed", value));
                Text("朗读要求（可选）", node.Option("instructions"), value => node.SetOption("instructions", value));
                break;
        }
    }

    private static readonly Dictionary<string, string> EffortLabels = new()
    {
        ["none"] = "关闭", ["minimal"] = "最少", ["low"] = "低", ["medium"] = "中", ["high"] = "高", ["xhigh"] = "极高", ["max"] = "最高",
    };

    private void Combo(string header, IEnumerable<(string Value, string Label)> items, string current, Action<string> apply)
    {
        var list = items.ToList();
        var box = new ComboBox { Width = 150, ItemsSource = list.Select(i => i.Label).ToList() };
        box.SelectedIndex = Math.Max(0, list.FindIndex(i => i.Value == current));
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex < 0) return; _host.History.Record(); apply(list[box.SelectedIndex].Value); ShowEstimate(); };
        Row(header, box);
    }

    private void Toggle(string header, bool value, Action<bool> apply)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => { _host.History.Record(); apply(toggle.IsOn); ShowEstimate(); };
        Row(header, toggle);
    }

    private void Text(string header, string value, Action<string> apply)
    {
        // AcceptsReturn before Text, or a multi-line value is cut at its first line break.
        var box = new TextBox { Header = header, AcceptsReturn = true, Text = value, TextWrapping = TextWrapping.Wrap };
        // The box itself is the undo key, so one typing session shares one undo point; the BoxText check skips the echo of the initial value.
        box.TextChanged += (_, _) => { if (box.Text == NodeView.BoxText(value)) return; value = box.Text; _host.History.Record(box); apply(value.Replace('\r', '\n')); };
        _params.Children.Add(box);
    }

    private void Note(string text) => _params.Children.Add(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryCaption"], Margin = new(0, -2, 0, 6),
    });

    /// <summary>Compact "label | control" row; the label also names the control for screen readers.</summary>
    private void Row(string label, FrameworkElement control)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 36 };
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, label);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        _params.Children.Add(row);
    }
}
