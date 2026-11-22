using System.ComponentModel;
using Tabula.App.Pages;
using Tabula.Core.Canvas;
using Tabula.Core.Models;
using Tabula.Core.Settings;
using Tabula.Core.Storage;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Tabula.App.Board;

/// <summary>
/// Bottom floating pill bar with two distinct states:
/// (1) Post-generation info view (图片信息框): selecting an existing finished picture/text/media shows its own origin (prompt, model, specs, wired sources)
/// and offers 沿用设置 / 以此图生图 without confusing it with the canvas draft.
/// (2) Pre-generation composer (生成参数框): selecting a 配方, empty/running/failed node, calling up with / or Enter, or clicking 以此图生图 / 沿用设置
/// shows the unified 36-DIP pill composer for configuring and running the next generation.
/// </summary>
public sealed partial class ComposerPanel : UserControl
{
    private static readonly string[] Voices = ["alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar", "fable", "onyx", "nova"];
    private readonly CanvasView _host;
    private ComposerState _state = new(ComposerMode.New, []);
    private List<CanvasNode> _refs = [];
    private List<CanvasNode> _selectedNodes = [];
    /// <summary>The node the prompt and parameters are read from and written to: the bound node, else the canvas's draft.</summary>
    private CanvasNode? _node;
    private List<string> _models = [];
    private bool _loading;
    /// <summary>An IME is composing: Enter then confirms the candidate and must not generate.</summary>
    private bool _composing;
    private bool _called;
    /// <summary>User explicitly switched from an inspected finished node into the pre-generation composer.</summary>
    private bool _manualCompose;
    private Panel _params = null!;
    /// <summary>Set while rebuilding the open flyout, so the 高级 section the user is working in stays expanded.</summary>
    private bool _keepAdvanced;
    private PromptLibraryFlyout? _library;
    private ThemeShadow? _shadow;
    private CanvasNode? _inspectSubject;

    /// <summary>Raised when toggling between post-generation info view and pre-generation composer so the board updates 参考 N badges.</summary>
    public event Action? ViewModeChanged;

    private Border[] FixedCapsules =>
    [
        InspectPromptCapsule, InspectTitleCapsule, InspectSpecCapsule, InspectNotesCapsule, InspectReuseCapsule, InspectComposeCapsule,
        HintCapsule, MarksCapsule, BackInfoCapsule, AddCapsule, TargetCapsule, PromptCapsule,
        ModeCapsule, ModelCapsule, ParamsCapsule, LibraryCapsule, InfoCapsule, RecipeCapsule, EstimateCapsule, GenerateCapsule,
    ];

    public ComposerPanel(CanvasView host)
    {
        InitializeComponent();
        _host = host;
        foreach (var capsule in FixedCapsules) BlockCanvas(capsule);
        ActualThemeChanged += (_, _) => { if (_node is not null) Refresh(); };
        Unloaded += (_, _) => { if (_node is not null) _node.PropertyChanged -= Node_PropertyChanged; };
    }

    private static void BlockCanvas(UIElement element)
    {
        // Keep interaction on each capsule from starting canvas gestures, while open gaps between capsules remain transparent to the board.
        element.PointerPressed += (_, e) => e.Handled = true;
        element.PointerReleased += (_, e) => e.Handled = true;
        element.PointerMoved += (_, e) => e.Handled = true;
        element.PointerWheelChanged += (_, e) => e.Handled = true;
        element.DoubleTapped += (_, e) => e.Handled = true;
        element.RightTapped += (_, e) => e.Handled = true;
    }

    public ComposerState State => _state;
    public IReadOnlyList<CanvasNode> References => _refs;
    public bool HasFocus => PromptBox.FocusState != FocusState.Unfocused;
    public bool IsComposing { get; private set; }
    private static AppSettings Settings => SettingsStore.Current;
    private Capability Capability => _node is null ? Capability.Image : CanvasGenerator.CapabilityOf(_node);
    private bool New => _state.Mode == ComposerMode.New;

    /// <summary>The draft counts as something to save as a 配方 when nothing is bound and there is a prompt or a reference.</summary>
    public bool CanSaveRecipe => IsComposing && _state.Mode is ComposerMode.New && _node is not null && (!string.IsNullOrWhiteSpace(_node.Prompt) || _refs.Count > 0);

    /// <summary>The width of the group in layout units: the canvas decides, from the room between its panels.</summary>
    public void SetWidth(double width) => Shell.Width = width;

    /// <summary>Shadow goes on each rounded capsule, not the outer container, so it follows each capsule's corners and lets LiquidGlass swap it out.</summary>
    public void CastShadowOn(UIElement receiver)
    {
        _shadow = new ThemeShadow();
        _shadow.Receivers.Add(receiver);
        foreach (var capsule in FixedCapsules)
            ApplyShadow(capsule, 24);
    }

    private void ApplyShadow(Border capsule, float elevation = 24)
    {
        if (_shadow is null) return;
        capsule.Shadow = _shadow;
        capsule.Translation = new System.Numerics.Vector3(0, 0, elevation);
    }

    /// <summary>Whether the current selection is a single finished node or an active/failed task whose info bar can be returned to.</summary>
    private bool CanInspect => (_state.Mode is ComposerMode.Stop or ComposerMode.Retry)
        || (New && _selectedNodes.Count == 1 && (_selectedNodes[0].HasContent || _selectedNodes[0].Kind == NodeKind.Group));

    private bool ShouldCompose()
    {
        if (_state.Mode is ComposerMode.Recipe or ComposerMode.Fill) return true;
        if (_state.Mode is ComposerMode.Stop or ComposerMode.Retry) return _manualCompose || HasFocus;
        if (_called || _manualCompose || HasFocus) return true;
        if (_selectedNodes.Count == 0) return true;
        if (_selectedNodes.Count == 1) return !_selectedNodes[0].HasContent && _selectedNodes[0].Kind != NodeKind.Group;
        // Multi-selection with usable references enters the pre-generation composer directly.
        return _refs.Count > 0;
    }

    /// <summary>The selection (or what it holds) changed: take the new mode and references. Switching selection returns to info view for finished pictures.</summary>
    public void Update(ComposerState state, IReadOnlyList<CanvasNode> references, IReadOnlyList<CanvasNode>? selectedNodes = null, bool called = false, bool selectionChanged = false)
    {
        if (selectionChanged) _manualCompose = false;
        _called = called;
        _selectedNodes = selectedNodes is not null ? [.. selectedNodes] : [];
        var node = state.Bound ?? Composer.DraftOf(_host.Doc);
        _state = state;
        _refs = [.. references];
        if (node != _node)
        {
            ParamsFlyout.Hide();
            _library?.Hide();
            if (_node is not null) _node.PropertyChanged -= Node_PropertyChanged;
            _node = node;
            node.PropertyChanged += Node_PropertyChanged;
            ShowPrompt();
        }
        Refresh();
    }

    private void ShowPrompt()
    {
        _loading = true;
        PromptBox.Text = NodeView.BoxText(_node!.Prompt);
        _loading = false;
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || _node is null) return;
        if (e.PropertyName is nameof(CanvasNode.Model) or nameof(CanvasNode.Mode) or nameof(CanvasNode.Count)
            or nameof(CanvasNode.Size) or nameof(CanvasNode.Ratio) or nameof(CanvasNode.Quality) or nameof(CanvasNode.Options))
        {
            ParamsFlyout.Hide();
            Refresh();
            return;
        }
        if (e.PropertyName != nameof(CanvasNode.Prompt)) return;
        var text = NodeView.BoxText(_node.Prompt);
        if (PromptBox.Text == text) return;
        var start = Math.Min(PromptBox.SelectionStart, text.Length);
        var length = Math.Min(PromptBox.SelectionLength, text.Length - start);
        PromptBox.Text = text;
        PromptBox.Select(start, length);
        ShowEstimate();
        ShowButtons();
    }

    /// <summary>Re-read everything that depends on settings, connections or running state.</summary>
    public void Refresh()
    {
        if (_node is null) return;
        _loading = true;
        var composing = ShouldCompose();
        var modeChanged = composing != IsComposing;
        IsComposing = composing;
        InspectView.Visibility = composing ? Visibility.Collapsed : Visibility.Visible;
        ComposeView.Visibility = composing ? Visibility.Visible : Visibility.Collapsed;

        if (!composing)
        {
            ShowInspect();
        }
        else
        {
            var capability = Capability;
            _node.Model = Settings.ModelFor(capability, _node.Model);
            _models = Settings.ModelsFor(capability).ToList();
            ModelBox.ItemsSource = _models.Select(Settings.Label).ToList();
            ModelBox.SelectedIndex = _models.IndexOf(_node.Model);
            ModeCapsule.Visibility = _state.Mode == ComposerMode.Fill ? Visibility.Collapsed : Visibility.Visible;
            UpdateModeButtons(_node.Mode);
            PromptBox.PlaceholderText = _state.Mode switch
            {
                ComposerMode.Recipe => "写下这个配方的提示词，Enter 运行配方，Shift+Enter 换行",
                ComposerMode.Fill => "描述想要生成到这个节点里的内容，Enter 生成，Shift+Enter 换行",
                _ when capability == Capability.Audio => "输入要朗读的文字，Enter 生成，Shift+Enter 换行",
                _ when capability == Capability.Text => "输入要让模型完成的任务，Enter 生成，Shift+Enter 换行",
                _ when _refs.Count > 0 => "描述要怎么用这些参考，例如：把她的外套换成红色，Enter 生成",
                _ => "描述想要生成的画面，选中画布上的图片或文本可作为参考，Enter 生成",
            };
            ShowReferences();
            ShowInfo();
            ParamsText.Text = ParamsSummary();
            ShowEstimate();
            ShowButtons();
        }
        _loading = false;
        if (modeChanged) ViewModeChanged?.Invoke();
    }

    // ---------- Post-generation & task status info view (图片 / 生成状态信息框) ----------

    private void ShowInspectSources(IEnumerable<CanvasNode> sources)
    {
        InspectRefsRow.Children.Clear();
        var list = sources.ToList();
        for (var i = 0; i < list.Count; i++) InspectRefsRow.Children.Add(Chip(list[i], i + 1, editable: false, prefix: "原参考"));
        InspectRefsScroll.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowInspect()
    {
        InspectRefsRow.Children.Clear();
        InspectRefsScroll.Visibility = Visibility.Collapsed;
        InspectPromptCapsule.Visibility = Visibility.Collapsed;
        InspectSpecCapsule.Visibility = Visibility.Collapsed;
        InspectNotesCapsule.Visibility = Visibility.Collapsed;
        InspectReuseCapsule.Visibility = Visibility.Collapsed;
        InspectComposeCapsule.Visibility = Visibility.Collapsed;
        InspectComposeIcon.Glyph = "\uE945";
        _inspectSubject = null;

        // Running or failed tasks: show their own upstream references, prompt, model, status and Stop/Retry action instead of an unrelated draft.
        if (_state.Mode is ComposerMode.Stop or ComposerMode.Retry)
        {
            var lead = _state.Nodes[0];
            var origin = Composer.ReuseSource(_host.Doc, lead) ?? lead;
            _inspectSubject = lead;
            InspectKindIcon.Glyph = _state.Mode == ComposerMode.Stop ? "\uE895" : "\uE783";
            InspectKindIcon.Foreground = _state.Mode == ComposerMode.Stop
                ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            InspectPrimaryText.Text = !string.IsNullOrWhiteSpace(origin.Model) ? Settings.Label(origin.Model) : Stacks.OwnTitle(lead);

            var specs = new List<string>
            {
                _state.Mode == ComposerMode.Stop
                    ? (_state.Nodes.Count > 1 ? $"{_state.Nodes.Count} 个正在生成…" : "正在生成…")
                    : (_state.Nodes.Count > 1 ? $"{_state.Nodes.Count} 个失败，可重试" : string.IsNullOrWhiteSpace(lead.Error) ? "生成失败" : lead.Error),
            };
            if (origin.Size is { Length: > 0 } size && size != "auto") specs.Add(size);
            if (origin.Ratio is { Length: > 0 } ratio && ratio != "auto") specs.Add(ratio);
            InspectSpecText.Text = string.Join(" · ", specs);
            InspectSpecCapsule.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(origin.Prompt))
            {
                InspectPromptText.Text = origin.Prompt.Trim();
                InspectPromptCapsule.Visibility = Visibility.Visible;
            }
            var refs = _state.Nodes.SelectMany(n => Composer.SourcesOf(_host.Doc, n)).DistinctBy(n => n.Id).ToList();
            ShowInspectSources(refs);

            if (!string.IsNullOrWhiteSpace(origin.Prompt) || !string.IsNullOrWhiteSpace(origin.Model))
                InspectReuseCapsule.Visibility = Visibility.Visible;

            var isStop = _state.Mode == ComposerMode.Stop;
            InspectComposeIcon.Glyph = isStop ? "\uE71A" : "\uE72C";
            InspectComposeText.Text = isStop ? "停止" : "重试";
            AutomationProperties.SetName(InspectComposeButton, InspectComposeText.Text);
            InspectComposeCapsule.Visibility = Visibility.Visible;
            return;
        }

        if (_selectedNodes.Count >= 2)
        {
            InspectKindIcon.Glyph = "\uE762";
            InspectKindIcon.Foreground = new SolidColorBrush(Palette.Kind(NodeKind.Image, ActualTheme));
            InspectPrimaryText.Text = $"已选 {_selectedNodes.Count} 个节点";
            var refCount = _refs.Count;
            InspectSpecText.Text = refCount > 0 ? $"其中 {refCount} 项可作参考" : "未包含图片或文本参考";
            InspectSpecCapsule.Visibility = Visibility.Visible;
            if (refCount > 0)
            {
                InspectComposeText.Text = $"作为参考生成 ({refCount})";
                AutomationProperties.SetName(InspectComposeButton, "作为参考生成");
                InspectComposeCapsule.Visibility = Visibility.Visible;
            }
            return;
        }

        var subject = _selectedNodes.FirstOrDefault() ?? _host.Subject();
        if (subject is null) return;
        _inspectSubject = subject;
        InspectKindIcon.Glyph = subject.Kind switch
        {
            NodeKind.Image => "\uEB9F",
            NodeKind.Video => "\uE714",
            NodeKind.Audio => "\uE8D6",
            NodeKind.Text => "\uE8D2",
            NodeKind.Group => "\uE8B7",
            _ => "\uE945",
        };
        InspectKindIcon.Foreground = new SolidColorBrush(Palette.Kind(subject.Kind, ActualTheme));

        var sources = Composer.SourcesOf(_host.Doc, subject);
        ShowInspectSources(sources);

        var originNode = Composer.OriginOf(_host.Doc, subject);
        if (originNode is not null)
        {
            // AI-generated output: show its real model, specs, original prompt, wired references, and notes.
            InspectPrimaryText.Text = !string.IsNullOrWhiteSpace(originNode.Model) ? Settings.Label(originNode.Model) : Stacks.OwnTitle(subject);
            var specs = new List<string>();
            if (subject.Kind == NodeKind.Image)
            {
                if (originNode.Size is { Length: > 0 } size && size != "auto") specs.Add(size);
                if (originNode.Ratio is { Length: > 0 } ratio && ratio != "auto") specs.Add(ratio);
                if (originNode.Quality is { Length: > 0 } quality && quality != "auto") specs.Add(quality);
                if (subject.NaturalWidth > 0 && subject.NaturalHeight > 0) specs.Add($"{subject.NaturalWidth}×{subject.NaturalHeight}");
                if (subject.Content is { Length: > 0 } file && Path.GetExtension(file) is { Length: > 1 } ext) specs.Add(ext[1..].ToUpperInvariant());
            }
            else if (subject.Kind == NodeKind.Text && subject.Content is { Length: > 0 } txt) specs.Add($"{txt.Length} 字");
            if (specs.Count > 0)
            {
                InspectSpecText.Text = string.Join(" · ", specs);
                InspectSpecCapsule.Visibility = Visibility.Visible;
            }
            if (!string.IsNullOrWhiteSpace(originNode.Prompt))
            {
                InspectPromptText.Text = originNode.Prompt.Trim();
                InspectPromptCapsule.Visibility = Visibility.Visible;
            }
            InspectReuseCapsule.Visibility = Visibility.Visible;
        }
        else
        {
            // Locally imported file, tool-derived image, plain typed note, or group: compact factual metadata only.
            InspectPrimaryText.Text = Stacks.OwnTitle(subject);
            var specs = new List<string>();
            if (subject.Kind == NodeKind.Image)
            {
                if (subject.NaturalWidth > 0 && subject.NaturalHeight > 0) specs.Add($"{subject.NaturalWidth}×{subject.NaturalHeight}");
                if (subject.Content is { Length: > 0 } file) specs.Add(Media.IsSvg(file) ? "SVG 矢量" : Path.GetExtension(file).TrimStart('.').ToUpperInvariant());
                if (sources.Count == 0) specs.Add("本地导入");
            }
            else if (subject.Kind == NodeKind.Text) specs.Add($"{(subject.Content ?? "").Length} 字");
            else if (subject.Kind == NodeKind.Group) specs.Add($"{CanvasGraph.GroupMembers(_host.Doc, subject.Id).Count()} 个节点");
            else if (subject.Content is { Length: > 0 } file) specs.Add(Path.GetExtension(file).TrimStart('.').ToUpperInvariant());
            if (specs.Count > 0)
            {
                InspectSpecText.Text = string.Join(" · ", specs.Where(s => s.Length > 0));
                InspectSpecCapsule.Visibility = Visibility.Visible;
            }
        }

        // Model notes / recognised text on the inspected node.
        var notes = new[] { (Label: "模型说明", Text: subject.Option(NodeKeys.ModelText)), (Label: "识别文字", Text: NodeKeys.RecognizedText(subject)), (Label: "参考图", Text: subject.Option(NodeKeys.InputNote)) }.Where(n => n.Text.Length > 0).ToList();
        var info = string.Join("\n", notes.Select(n => $"{n.Label}：{n.Text}"));
        InspectNotesCapsule.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(InspectNotesButton, info.Length > 0 ? info : null);
        InspectNotesButton.Flyout = notes.Count > 0 ? new Flyout { Content = new TextBlock { Text = info, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, IsTextSelectionEnabled = true } } : null;

        if (_refs.Count > 0 || subject.Kind == NodeKind.Group)
        {
            InspectComposeText.Text = subject.Kind == NodeKind.Image ? "以此图生图" : "作为参考生成";
            AutomationProperties.SetName(InspectComposeButton, InspectComposeText.Text);
            InspectComposeCapsule.Visibility = _refs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(InspectPromptText.Text)) return;
        var pkg = new DataPackage();
        pkg.SetText(InspectPromptText.Text);
        Clipboard.SetContent(pkg);
        CopyPromptIcon.Glyph = "\uE73E";
        _ = ResetCopyIconAsync();
    }

    private async Task ResetCopyIconAsync()
    {
        await Task.Delay(1200);
        CopyPromptIcon.Glyph = "\uE8C8";
    }

    private async void InspectPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Mode is ComposerMode.Stop or ComposerMode.Retry) await SubmitAsync();
        else StartCompose_Click(sender, e);
    }

    private void StartCompose_Click(object sender, RoutedEventArgs e)
    {
        _manualCompose = true;
        Refresh();
        FocusPrompt();
    }

    private void BackToInfo_Click(object sender, RoutedEventArgs e)
    {
        _manualCompose = false;
        _called = false;
        Refresh();
    }

    private void InspectReuse_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectSubject is null) return;
        var origin = Composer.OriginOf(_host.Doc, _inspectSubject) ?? Composer.ReuseSource(_host.Doc, _inspectSubject) ?? _inspectSubject;
        Composer.Reuse(Composer.DraftOf(_host.Doc), origin);
        _host.DraftChanged(true);
        _manualCompose = true;
        if (_state.Bound is null) ShowPrompt();
        Refresh();
        FocusPrompt();
    }

    // ---------- Pre-generation composer (生成参数框) ----------

    private void UpdateModeButtons(Capability current)
    {
        var activeBrush = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        foreach (var (button, cap) in new[] { (ModeImageButton, Capability.Image), (ModeTextButton, Capability.Text), (ModeAudioButton, Capability.Audio) })
        {
            var selected = cap == current;
            if (selected) button.Background = activeBrush;
            else button.ClearValue(BackgroundProperty);
            if (button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is { } text)
                text.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _node is null || sender is not Button { Tag: string tag } || tag == _node.Mode.ToString()) return;
        var (node, mode) = (_node, Enum.Parse<Capability>(tag));
        SetParameter(() => node.Mode = mode);
        Refresh();
    }

    private string? Problem()
    {
        var resolved = Settings.Resolve(_node!.Model);
        // ProtectedKey avoids a DPAPI decrypt on every refresh.
        return resolved is null ? $"还没有可用的{Labels.Name(Capability)}模型"
            : string.IsNullOrEmpty(resolved.Value.Channel.ProtectedKey) ? $"服务商「{Settings.ChannelName(resolved.Value.Channel)}」还没有填写 API Key" : null;
    }

    private void ShowReferences()
    {
        RefChips.Children.Remove(BackInfoCapsule);
        RefChips.Children.Remove(AddCapsule);
        RefChips.Children.Remove(TargetCapsule);
        RefChips.Children.Clear();

        BackInfoCapsule.Visibility = CanInspect && _manualCompose ? Visibility.Visible : Visibility.Collapsed;
        if (BackInfoCapsule.Visibility == Visibility.Visible) RefChips.Children.Add(BackInfoCapsule);

        var shown = New ? _refs : _state.Bound is { } bound ? Composer.SourcesOf(_host.Doc, bound)
            : _state.Nodes.SelectMany(n => Composer.SourcesOf(_host.Doc, n)).DistinctBy(n => n.Id).ToList();
        for (var i = 0; i < shown.Count; i++) RefChips.Children.Add(Chip(shown[i], i + 1, New));
        AddCapsule.Visibility = New ? Visibility.Visible : Visibility.Collapsed;
        TargetText.Text = _state.Mode switch
        {
            ComposerMode.Recipe => "结果放在配方右边",
            ComposerMode.Fill => $"生成到「{_state.Bound!.Title}」",
            ComposerMode.Stop => $"{_state.Nodes.Count} 个正在生成",
            ComposerMode.Retry => $"{_state.Nodes.Count} 个失败，可重试",
            _ => _refs.Count > 0 ? "结果放在参考右边" : "结果放在视图中央",
        };
        RefChips.Children.Add(AddCapsule);
        RefChips.Children.Add(TargetCapsule);
    }

    /// <summary>One reference as a unified Height=36, CornerRadius=18 floating glass pill. Non-editable chips locate the reference on click.</summary>
    private UIElement Chip(CanvasNode node, int number, bool editable, string prefix = "参考")
    {
        UIElement thumb;
        if (node.Kind == NodeKind.Image && node.HasContent && ProjectStore.AssetPath(_host.Doc.Id, node.Content!) is { } path && File.Exists(path))
        {
            var source = Media.IsSvg(path) ? new SvgImageSource(new Uri(path)) : (ImageSource)new BitmapImage { DecodePixelWidth = 48, UriSource = new Uri(path) };
            thumb = new Border { Width = 24, Height = 24, CornerRadius = new(12), Child = new Image { Stretch = Stretch.UniformToFill, Source = source } };
        }
        else if (node.Kind == NodeKind.Image)
            thumb = new Border { Width = 24, Height = 24, CornerRadius = new(12), Background = new SolidColorBrush(Palette.Kind(NodeKind.Image, ActualTheme)), Child = new FontIcon { Glyph = "\uEB9F", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } };
        else thumb = new Border { Width = 24, Height = 24, CornerRadius = new(12), Background = new SolidColorBrush(Palette.Kind(NodeKind.Text, ActualTheme)), Child = new FontIcon { Glyph = "\uE8D2", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } };
        var label = new TextBlock { Text = $"{prefix} {number} · {Stacks.OwnTitle(node)}", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 152, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { thumb, label } };
        UIElement inner = row;
        if (editable)
        {
            var pinned = _host.IsPinned(node);
            row.Children.Add(ChipButton("\uE718", pinned ? $"取消固定参考 {number}" : $"固定参考 {number}", pinned ? "取消固定：选中其他节点时不再保留它" : "固定为参考：选中其他节点时也保留它", () => _host.TogglePin(node), pinned));
            row.Children.Add(ChipButton("\uE711", $"移除参考 {number}", "不作为这次的参考（节点仍保持选中）", () => _host.DropReference(node), false));
        }
        else
        {
            var jump = new Button
            {
                Style = (Style)Application.Current.Resources["ToolbarButton"],
                Height = 30, MinHeight = 0, Padding = new(4, 0, 10, 0), CornerRadius = new(15),
                Content = row,
            };
            jump.Click += (_, _) => _host.FocusNode(node);
            AutomationProperties.SetName(jump, $"{prefix} {number}：{Stacks.OwnTitle(node)}");
            ToolTipService.SetToolTip(jump, $"点击定位到{prefix}：{Stacks.OwnTitle(node)}");
            inner = jump;
        }
        var chip = new Border
        {
            Style = (Style)Application.Current.Resources["GlassCapsule"],
            Height = 36,
            CornerRadius = new(18),
            Padding = editable ? new(6, 0, 4, 0) : new(2),
            Child = inner,
        };
        BlockCanvas(chip);
        ApplyShadow(chip, 24);
        if (editable)
        {
            ToolTipService.SetToolTip(chip, Stacks.OwnTitle(node));
            AutomationProperties.SetName(chip, $"{prefix} {number}：{Stacks.OwnTitle(node)}");
        }
        return chip;
    }

    private static Button ChipButton(string glyph, string name, string tip, Action click, bool active)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["ToolbarButton"], Padding = new(0), MinHeight = 0, MinWidth = 0, Width = 26, Height = 26, CornerRadius = new(13),
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
        };
        if (active) ((FontIcon)button.Content).Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private void AddMenu_Opening(object? sender, object e)
    {
        AddMenu.Items.Clear();
        var library = new MenuFlyoutItem { Text = "从资料库选…", Icon = new FontIcon { Glyph = "\uE8F1" } };
        library.Click += (_, _) => _host.RequestLibrary();
        var import = new MenuFlyoutItem { Text = "导入文件…", Icon = new FontIcon { Glyph = "\uE8E5" } };
        import.Click += async (_, _) => await _host.RunAsync("import");
        AddMenu.Items.Add(library);
        AddMenu.Items.Add(import);
    }

    /// <summary>Missing-key hints, local-edit marks, and model notes in the pre-generation composer.</summary>
    private void ShowInfo()
    {
        var hint = Problem();
        HintText.Text = hint ?? "";
        HintCapsule.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(HintText, hint);
        var first = New ? _refs.FirstOrDefault(r => r.Kind == NodeKind.Image) : null;
        var parts = new List<string>();
        if (first is not null && NodeKeys.HasMarks(first))
        {
            if (first.Option(NodeKeys.Mask).Length > 0) parts.Add("涂抹重绘区域");
            if (first.Option(NodeKeys.Markup).Length > 0) parts.Add("画圈标注");
            if (NodeKeys.SelectionsOf(first).Count is > 0 and var count) parts.Add($"{count} 处位置");
        }
        MarksCapsule.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MarksText.Text = parts.Count > 0 ? $"已标注 · {Stacks.OwnTitle(first!)} · " + string.Join(" · ", parts) : "";
        InfoRow.Visibility = HintCapsule.Visibility == Visibility.Visible || MarksCapsule.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

        var subject = _host.Subject();
        var notes = subject is null ? [] : new[] { (Label: "模型说明", Text: subject.Option(NodeKeys.ModelText)), (Label: "识别文字", Text: NodeKeys.RecognizedText(subject)), (Label: "参考图", Text: subject.Option(NodeKeys.InputNote)) }.Where(n => n.Text.Length > 0).ToList();
        var info = string.Join("\n", notes.Select(n => $"{n.Label}：{n.Text}"));
        InfoCapsule.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(InfoButton, info.Length > 0 ? info : null);
        AutomationProperties.SetHelpText(InfoButton, info);
        InfoButton.Flyout = notes.Count > 0 ? new Flyout { Content = new TextBlock { Text = info, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, IsTextSelectionEnabled = true } } : null;
    }

    /// <summary>The button says what Enter will do for this selection; 存为配方 only where there is a draft to save.</summary>
    private void ShowButtons()
    {
        if (_node is null) return;
        var (text, glyph) = _state.Mode switch
        {
            ComposerMode.Stop => ("停止", "\uE71A"),
            ComposerMode.Retry => ("重试", "\uE72C"),
            ComposerMode.Recipe => ("运行配方", "\uE768"),
            _ => ("生成", "\uE768"),
        };
        GenerateText.Text = text;
        GenerateIcon.Glyph = glyph;
        AutomationProperties.SetName(GenerateButton, text);
        var hasInput = !string.IsNullOrWhiteSpace(_node.Prompt) || (_state.Bound is { } bound ? Composer.Wired(_host.Doc, bound).Count > 0 : _refs.Count > 0);
        GenerateButton.IsEnabled = _state.Mode is ComposerMode.Stop or ComposerMode.Retry || hasInput && Problem() is null;
        GenerateCapsule.Opacity = GenerateButton.IsEnabled ? 1 : 0.55;
        ToolTipService.SetToolTip(GenerateButton, Problem() is { } problem && _state.Mode is not (ComposerMode.Stop or ComposerMode.Retry) ? problem : $"{text}（Enter 或 Ctrl+Enter）");
        RecipeCapsule.Visibility = New ? Visibility.Visible : Visibility.Collapsed;
        RecipeButton.IsEnabled = CanSaveRecipe;
    }

    /// <summary>Expected cost of the next image run from the model, parameters, prompt and references; image runs only.</summary>
    private void ShowEstimate()
    {
        var resolved = Settings.Resolve(_node!.Model);
        var show = Capability == Capability.Image && resolved is not null && _state.Mode is not (ComposerMode.Stop or ComposerMode.Retry);
        EstimateCapsule.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var references = (New ? _refs : _state.Bound is { } bound ? Composer.Wired(_host.Doc, bound) : []).Count(r => r.Kind == NodeKind.Image);
        const string Caveat = "按官方价格或上次同样设置的实际用量估算，仅供参考：不含思考 token、免费额度和优惠";
        if (Prices.EstimateImage(resolved!.Value.Channel, resolved.Value.Model.Name, _node, references) is { } estimate)
        {
            EstimateText.Text = $"≈ {Prices.Money(estimate.Amount, estimate.Currency)}";
            ToolTipService.SetToolTip(EstimateCapsule, $"预计花费 ≈ {estimate.Text}\n{Caveat}");
        }
        else
        {
            EstimateText.Text = "预计花费 —";
            ToolTipService.SetToolTip(EstimateCapsule, $"预计花费：这个模型和设置还没有官方单价，生成一次后按实际用量估算\n{Caveat}");
        }
    }

    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window.Frame.Content is CanvasPage page && await page.PrepareLeaveAsync())
            App.Window.Frame.Navigate(typeof(SettingsPage), null, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
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
            {
                foreach (var value in new[] { image.Tiers.Contains(node.Size) ? node.Size : image.Tiers.FirstOrDefault(), image.Ratios.Contains(node.Ratio) ? node.Ratio : image.Ratios.FirstOrDefault() })
                    if (value is { Length: > 0 } && value != "auto") parts.Add(value);
                if (node.Option("outputFormat") is "gif" or "svg" && image.OutputFormats.Contains(node.Option("outputFormat"))) parts.Add(node.Option("outputFormat").ToUpperInvariant());
            }
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "参数";
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        if (_refs.FirstOrDefault(r => r.Kind == NodeKind.Image && NodeKeys.HasMarks(r)) is not { } marked) return;
        _host.ClearMarks(marked);
        Refresh();
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _node is null || PromptBox.Text == NodeView.BoxText(_node.Prompt)) return;
        if (_state.Bound is not null) _host.History.Record($"{_node.Id}/prompt");
        _node.Prompt = PromptBox.Text.Replace('\r', '\n');
        _host.DraftChanged(_state.Bound is null);
        ShowEstimate();
        ShowButtons();
    }

    private void PromptBox_CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => _composing = true;

    private void PromptBox_CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _composing = false);

    private void PromptBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _host.LeaveComposer();
            return;
        }
        if (e.Key != VirtualKey.Enter || _composing) return;
        var down = Windows.UI.Core.CoreVirtualKeyStates.Down;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(down);
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(down);
        if (shift && !ctrl) return;
        e.Handled = true;
        if (!e.KeyStatus.WasKeyDown) _ = SubmitAsync();
    }

    public void FocusPrompt()
    {
        if (!IsComposing)
        {
            _manualCompose = true;
            Refresh();
        }
        PromptBox.UpdateLayout();
        PromptBox.Focus(FocusState.Keyboard);
        PromptBox.SelectionStart = PromptBox.Text.Length;
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _node is null || ModelBox.SelectedIndex < 0 || _models[ModelBox.SelectedIndex] == _node.Model) return;
        var (node, model) = (_node, _models[ModelBox.SelectedIndex]);
        SetParameter(() => node.Model = model);
        ShowInfo();
        ShowButtons();
    }

    private async void Generate_Click(object sender, RoutedEventArgs e) => await SubmitAsync();

    /// <summary>What the button and Enter do for this selection: stop, retry, run the bound node, or start a new generation from the references.</summary>
    public async Task SubmitAsync()
    {
        if (_node is null || _host.IsClosing) return;
        var generator = _host.Generator;
        switch (_state.Mode)
        {
            case ComposerMode.Stop:
                foreach (var node in _state.Nodes.ToList()) generator.Cancel(node.Id);
                break;
            case ComposerMode.Retry:
                var doc = _host.Doc;
                await Task.WhenAll(_state.Nodes.ToList().GroupBy(node => CanvasGenerator.RetrySource(doc, node)?.Id ?? node.Id)
                    .Select(async group => { foreach (var node in group) await generator.GenerateAsync(node); }));
                break;
            case ComposerMode.Recipe or ComposerMode.Fill:
                if (!IsComposing) { FocusPrompt(); break; }
                await generator.GenerateAsync(_state.Bound!);
                break;
            default:
                if (!IsComposing) { FocusPrompt(); break; }
                if (!GenerateButton.IsEnabled) { FocusPrompt(); break; }
                await _host.GenerateFromComposerAsync(_node, _refs);
                break;
        }
    }

    /// <summary>沿用这次的设置: the node's prompt, model and parameters go into the composer's draft, and the cursor into the prompt.</summary>
    public void Reuse(CanvasNode node)
    {
        if (Composer.ReuseSource(_host.Doc, node) is not { } source) return;
        Composer.Reuse(Composer.DraftOf(_host.Doc), source);
        _host.DraftChanged(true);
        _manualCompose = true;
        if (_state.Bound is null) ShowPrompt();
        Refresh();
        FocusPrompt();
    }

    private void SaveRecipe_Click(object sender, RoutedEventArgs e) => SaveRecipe();

    public void SaveRecipe()
    {
        if (!CanSaveRecipe || _node is null) return;
        _host.SaveRecipeFromComposer(_node, _refs);
    }

    /// <summary>The flyout hangs off the prompt capsule so it opens above the input and never covers what the user is typing.</summary>
    private void Params_Click(object sender, RoutedEventArgs e) => FlyoutBase.ShowAttachedFlyout(PromptCapsule);

    private void PromptLibrary_Click(object sender, RoutedEventArgs e) => (_library ??= new PromptLibraryFlyout(() => PromptBox.Text, UsePrompt)).ShowAt(PromptCapsule);

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
                if (image.Complexities.Length > 0)
                {
                    Combo("复杂度", [("auto", "默认（高）"), .. image.Complexities.Select(c => (c, EffortLabels[c]))], node.Option("complexity", "auto"), value => node.SetOption("complexity", value == "auto" ? null : value));
                    Note("复杂度决定模型动笔前规划多少：低适合局部小改，高适合密集文字和复杂排版，耗时更长。不另收费。");
                }
                if (image.OutputFormats.Length > 1) Combo("输出格式", image.OutputFormats.Select(f => (f, f.ToUpperInvariant())), node.Option("outputFormat", image.OutputFormats[0]), value =>
                {
                    node.SetOption("outputFormat", value);
                    // GIF has its own frame settings, so the list is rebuilt.
                    if (image.OutputFormats.Contains("gif")) DispatcherQueue.TryEnqueue(() => ParamsFlyout_Opening(ParamsPanel, EventArgs.Empty));
                });
                var format = node.Option("outputFormat", image.OutputFormats[0]);
                if (format == "gif" && image.OutputFormats.Contains("gif"))
                {
                    Text("GIF 帧数（2–64，留空为 9）", node.Option("frameCount"), value => node.SetOption("frameCount", int.TryParse(value.Trim(), out var frames) ? frames.ToString() : null));
                    Text("每帧时长（毫秒，20–2000，留空为 200）", node.Option("frameDuration"), value => node.SetOption("frameDuration", int.TryParse(value.Trim(), out var duration) ? duration.ToString() : null));
                }
                if (format is "gif" or "svg" && image.OutputFormats.Contains(format))
                    Note("GIF 和 SVG 只能文生图：不能连接参考图，也没有流式预览；SVG 每次只出 1 张，多张会分开请求。GIF 的帧数、每帧尺寸和张数一起受总像素上限约束，超出时请减少帧数或改用自动分辨率。");
                if (image.Background) Combo("背景", [("auto", "自动"), ("transparent", "透明"), ("opaque", "不透明")], node.Option("background", "auto"), value => node.SetOption("background", value));
                if (vendor == Vendor.OpenAI)
                {
                    Combo("审核强度", [("auto", "标准"), ("low", "宽松")], node.Option("moderation", "auto"), value => node.SetOption("moderation", value));
                    var advanced = new StackPanel { Spacing = 4 };
                    _params.Children.Add(new Expander { Header = "高级", Content = advanced, IsExpanded = _keepAdvanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                    _keepAdvanced = false;
                    _params = advanced;
                    if (image.ResponsesTool)
                    {
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
                    }
                    else Note("这个模型只能用 Image API 调用，官方没有列出 Responses 图像工具支持。");
                    Toggle("流式预览（Image API）", node.Option("stream") == "true", value => node.SetOption("stream", value ? "true" : null));
                    Combo("中间预览张数（额外计费）", [("0", "0"), ("1", "1"), ("2", "2"), ("3", "3")], node.Option("partialImages", "0"), value => node.SetOption("partialImages", value));
                    if (image.ResponsesTool)
                    {
                        Toggle("多轮对话改图（Responses）", node.Option("store") == "true", value => node.SetOption("store", value ? "true" : null));
                        Note("开启后本轮会话保存在 OpenAI；之后在生成结果上继续生成，会接着上一轮修改，保持主体一致。仅 Responses 调用方式生效。");
                    }
                    _params = ParamsPanel;
                }
                if (vendor == Vendor.Google)
                {
                    Toggle("多轮对话改图", node.Option("store") == "true", value => node.SetOption("store", value ? "true" : null));
                    Note("开启后本轮会话保存在 Google；之后在生成结果上继续生成，会接着上一轮修改，保持主体一致。");
                }
                if (image.Interactive)
                {
                    Toggle("图层拆分", node.Option("layers") == "true", value =>
                    {
                        node.SetOption("layers", value ? "true" : null);
                        // The 5.5 layer settings only show while the split is on, so the list is rebuilt.
                        if (image.LayerOptions) DispatcherQueue.TryEnqueue(() => ParamsFlyout_Opening(ParamsPanel, EventArgs.Empty));
                    });
                    if (image.LayerOptions && node.Option("layers") == "true")
                    {
                        Text($"最多张数（{NodeKeys.LayerMin}–{NodeKeys.LayerMaxLimit}，含底图，留空为 {NodeKeys.LayerMaxDefault}）", node.Option(NodeKeys.LayerMax),
                            value => node.SetOption(NodeKeys.LayerMax, int.TryParse(value.Trim(), out var layerMax) && layerMax is >= NodeKeys.LayerMin and <= NodeKeys.LayerMaxLimit ? layerMax.ToString() : null));
                        Toggle("识别文字", node.Option(NodeKeys.LayerText) == "true", value => node.SetOption(NodeKeys.LayerText, value ? "true" : null));
                    }
                    if (!image.Rgba) Toggle("透明背景", node.Option("transparent") == "true", value => node.SetOption("transparent", value ? "true" : null));
                    Note(image.LayerOptions
                        ? "图层拆分：把 1 张输入图拆成底图和最多 99 个透明图层（上面的最多张数含底图，越大拆得越细，按返回的张数计费），按原位置铺到画布并编组；开启「识别文字」后，规整文字的图层会带上识别出的文字。图层拆分只接受 1 张输入图片，也不使用「背景」设置。"
                        : "图层拆分：把 1 张输入图拆成底图和最多 16 个透明图层，按原位置铺到画布并编组。透明背景：输入 1 张带透明通道的图，输出保留透明。两者都只接受 1 张输入图片。");
                }
                if (image.ThinkingLevels.Length > 1) Combo("思考强度", image.ThinkingLevels.Select(t => (t, t == "auto" ? "自动" : t)), node.Option("thinking", "auto"), value => node.SetOption("thinking", value));
                if (image.WebSearch) Toggle("联网搜索", node.Option("webSearch") == "true", value => node.SetOption("webSearch", value ? "true" : null));
                if (image.ImageSearch) Toggle("图片搜索", node.Option("imageSearch") == "true", value => node.SetOption("imageSearch", value ? "true" : null));
                if (vendor == Vendor.Ark)
                {
                    if (image.Stream) Toggle("流式返回", node.Option("stream") == "true", value => node.SetOption("stream", value ? "true" : null));
                    if (image.FastPrompt) Toggle("快速提示词优化", node.Option("fastPrompt") == "true", value => node.SetOption("fastPrompt", value ? "true" : null));
                }
                if (vendor == Vendor.MiniMax)
                {
                    Toggle("提示词增强", node.Option("promptExtend", "true") == "true", value => node.SetOption("promptExtend", value ? "true" : "false"));
                    Note("关闭后按原提示词生成，此时必须选定分辨率和比例（不能选「自动」）。有参考图的改图始终会增强提示词。自动分辨率由 MiniMax 决定，默认 4K，花费最高。");
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
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) SetParameter(() => apply(list[box.SelectedIndex].Value)); };
        Row(header, box);
    }

    private void Toggle(string header, bool value, Action<bool> apply)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => SetParameter(() => apply(toggle.IsOn));
        Row(header, toggle);
    }

    private void Text(string header, string value, Action<string> apply)
    {
        // AcceptsReturn before Text, or a multi-line value is cut at its first line break.
        var box = new TextBox { Header = header, AcceptsReturn = true, Text = value, TextWrapping = TextWrapping.Wrap };
        // The box itself is the undo key, so one typing session shares one undo point; the BoxText check skips the echo of the initial value.
        box.TextChanged += (_, _) => { if (box.Text == NodeView.BoxText(value)) return; value = box.Text; SetParameter(() => apply(value.Replace('\r', '\n')), box); };
        _params.Children.Add(box);
    }


    /// <summary>
    /// A change made in this composer: one undo point when it belongs to a bound node (the draft is not part of undo), written while <see cref="_loading"/> is set so <see cref="Node_PropertyChanged"/>
    /// (which reloads the composer for outside edits) ignores it, then only the summary and estimate follow. Typing in a parameter box
    /// must not reload the inputs or the model list on every key.
    /// </summary>
    private void SetParameter(Action apply, object? undoKey = null)
    {
        if (_state.Bound is not null) _host.History.Record(undoKey);
        _loading = true;
        try { apply(); }
        finally { _loading = false; }
        _host.DraftChanged(_state.Bound is null);
        ParamsText.Text = ParamsSummary();
        ShowEstimate();
        ShowButtons();
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
