using InfiniteCanvas.Core.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// The command bar at the top of the canvas: create commands when nothing is selected, otherwise 「+ 新建 ▾」, a name chip, the selection's
/// commands (<see cref="CanvasCommands.Bar"/>), ⋯ 更多 for the rest of the node menu, and undo / redo. Built from the command table only.
/// </summary>
public sealed class CommandBar : UserControl
{
    private static readonly Dictionary<string, NodeKind> CreateKinds = new()
    {
        ["new-text"] = NodeKind.Text, ["new-image"] = NodeKind.Image, ["new-video"] = NodeKind.Video, ["new-audio"] = NodeKind.Audio, ["new-config"] = NodeKind.Config,
    };

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Button _undo, _redo;
    private readonly List<TextBlock> _labels = [];
    private CanvasView? _board;
    private string _signature = "";
    private bool _compact;

    public CommandBar()
    {
        Content = _row;
        // Undo and redo are always at the end of the bar; their enabled state follows the history.
        _undo = Command(CanvasCommands.Find("undo"), false);
        _redo = Command(CanvasCommands.Find("redo"), false);
        ActualThemeChanged += (_, _) => Rebuild(true);
    }

    /// <summary>The bar's width with every label shown, so the page can decide whether the labels fit beside the title.</summary>
    public double FullWidth { get; private set; }

    /// <summary>Raised after the bar was rebuilt, because its width changed.</summary>
    public event Action? Rebuilt;

    /// <summary>Icons only: the page narrows the bar when its labels would not fit. Every button keeps its tooltip and automation name.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            ApplyCompact();
        }
    }

    public void Attach(CanvasView board)
    {
        _board = board;
        board.CommandsChanged += () => Rebuild(false);
        board.Changed += UpdateUndo;
        Rebuild(true);
    }

    public void UpdateUndo()
    {
        _undo.IsEnabled = _board?.History?.CanUndo == true;
        _redo.IsEnabled = _board?.History?.CanRedo == true;
    }

    private void ApplyCompact()
    {
        foreach (var label in _labels) label.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Rebuild(bool force)
    {
        if (_board is null || _board.Doc is null) return;
        var context = _board.Context();
        var subject = _board.Subject();
        var items = CanvasCommands.BarItems(context);
        var more = CanvasCommands.More(context).Count > 0;
        var chip = ChipOf(context, subject);
        var signature = $"{string.Join(",", items.Select(i => i.Spec is null ? "|" : i.Spec.Id + (i.Text ? "+" : "")))};{more};{chip.Text};{chip.Kind}";
        if (!force && signature == _signature) return;
        _signature = signature;
        _row.Children.Clear();
        _labels.Clear();
        if (context.Count > 0)
        {
            _row.Children.Add(NewButton());
            _row.Children.Add(Divider());
            _row.Children.Add(Chip(chip));
            if (items.Count > 0) _row.Children.Add(Divider());
        }
        foreach (var (spec, text) in items) _row.Children.Add(spec is null ? Divider() : Command(spec, text));
        if (more) _row.Children.Add(More());
        _row.Children.Add(Divider());
        _row.Children.Add(_undo);
        _row.Children.Add(_redo);
        UpdateUndo();
        // Measure with every label to tell the page how wide the full bar is, then narrow it if the page asks.
        foreach (var label in _labels) label.Visibility = Visibility.Visible;
        _row.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        FullWidth = _row.DesiredSize.Width + 12;
        ApplyCompact();
        Rebuilt?.Invoke();
    }

    private (string Text, NodeKind? Kind) ChipOf(CommandContext context, CanvasNode? subject) =>
        context.Count == 0 ? ("", null)
        : subject is null ? ($"已选 {context.Count} 个", null)
        : context.InBatch && !context.BatchOpen ? ("这一批 · 封面", subject.Kind)
        : (subject.Title, subject.Kind);

    private static string Glyph(NodeKind kind) => kind switch
    {
        NodeKind.Text => "", NodeKind.Image => "", NodeKind.Video => "", NodeKind.Audio => "", NodeKind.Config => "", _ => "",
    };

    private static UIElement Divider() => new Microsoft.UI.Xaml.Shapes.Rectangle { Style = (Style)Application.Current.Resources["ToolbarDivider"] };

    private Brush KindBrush(NodeKind kind) => Palette.Brush(kind, ActualTheme);

    /// <summary>A command button: icon alone with its name and shortcut in the tooltip, or icon plus text.</summary>
    private Button Command(CommandSpec spec, bool text)
    {
        var icon = new FontIcon { Glyph = spec.Glyph, FontSize = 15 };
        if (CreateKinds.TryGetValue(spec.Id, out var kind)) icon.Foreground = KindBrush(kind);
        var button = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"] };
        if (text)
        {
            var label = new TextBlock { Text = spec.Short };
            _labels.Add(label);
            button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { icon, label } };
        }
        else
        {
            button.Padding = new Thickness(10, 7, 10, 7);
            button.Content = icon;
        }
        ToolTipService.SetToolTip(button, spec.Tip);
        AutomationProperties.SetName(button, spec.Name);
        button.Click += async (_, _) => { if (_board is not null) await _board.RunAsync(spec.Id); UpdateUndo(); };
        return button;
    }

    private Button NewButton()
    {
        var button = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"] };
        var label = new TextBlock { Text = "新建" };
        _labels.Add(label);
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children = { new FontIcon { Glyph = "", FontSize = 15 }, label, new FontIcon { Glyph = "", FontSize = 9 } },
        };
        ToolTipService.SetToolTip(button, "新建节点或导入文件");
        AutomationProperties.SetName(button, "新建");
        var menu = new MenuFlyout();
        foreach (var id in new[] { "new-text", "new-image", "new-video", "new-audio", "new-config", "import" })
        {
            var spec = CanvasCommands.Find(id);
            var item = new MenuFlyoutItem { Text = spec.Name, Icon = new FontIcon { Glyph = spec.Glyph } };
            if (CreateKinds.TryGetValue(id, out var kind)) ((FontIcon)item.Icon).Foreground = KindBrush(kind);
            item.Click += async (_, _) => { if (_board is not null) await _board.RunAsync(id); };
            menu.Items.Add(item);
            if (id == "new-config") menu.Items.Add(new MenuFlyoutSeparator());
        }
        button.Flyout = menu;
        return button;
    }

    private Button More()
    {
        var button = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Padding = new Thickness(10, 7, 10, 7), Content = new FontIcon { Glyph = "", FontSize = 15 } };
        ToolTipService.SetToolTip(button, "更多");
        AutomationProperties.SetName(button, "更多");
        // The menu is built when it opens, from the selection at that moment.
        button.Click += (_, _) => _board?.BuildMoreMenu().ShowAt(button);
        return button;
    }

    private static Brush AccentTint() =>
        new SolidColorBrush(Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Windows.UI.Color c ? Windows.UI.Color.FromArgb(0x2A, c.R, c.G, c.B) : Windows.UI.Color.FromArgb(0x2A, 0, 120, 212));

    /// <summary>The name of what is selected; a collapsed batch says so, because the picture tools then act on its cover.</summary>
    private UIElement Chip((string Text, NodeKind? Kind) chip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        if (chip.Kind is { } kind) row.Children.Add(new FontIcon { Glyph = Glyph(kind), FontSize = 14, Foreground = KindBrush(kind) });
        row.Children.Add(new TextBlock { Text = chip.Text, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var border = new Border { Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center, Child = row, Background = AccentTint() };
        ToolTipService.SetToolTip(border, chip.Text);
        AutomationProperties.SetName(border, chip.Text);
        return border;
    }
}
