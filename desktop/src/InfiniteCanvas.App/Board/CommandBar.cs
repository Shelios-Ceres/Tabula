using InfiniteCanvas.Core.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// The command bar that floats at the selection (hosted by <see cref="CanvasView"/>): a chip for several nodes or a collapsed batch, the
/// selection's commands (<see cref="CanvasCommands.Bar"/>) and ⋯ 更多 for the rest of the node menu. Built from the command table only.
/// </summary>
public sealed class CommandBar : UserControl
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<TextBlock> _labels = [];
    private CanvasView? _board;
    private string _signature = "";
    private bool _compact;

    public CommandBar()
    {
        Content = _row;
        ActualThemeChanged += (_, _) => Rebuild(true);
    }

    /// <summary>The bar's width with every label shown, so the host can decide whether the labels fit.</summary>
    public double FullWidth { get; private set; }

    /// <summary>Whether the selection has anything to show; the host hides the bar when it has not.</summary>
    public bool HasContent { get; private set; }

    /// <summary>Raised after the bar was rebuilt, because its size changed.</summary>
    public event Action? Rebuilt;

    /// <summary>Icons only, when the labels would not fit the window. Every button keeps its tooltip and automation name.</summary>
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
    }

    /// <summary>Re-read the selection now (the host calls this when the selection changes, so the bar never shows the previous one).</summary>
    public void Refresh() => Rebuild(false);

    private void ApplyCompact()
    {
        foreach (var label in _labels) label.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Rebuild(bool force)
    {
        if (_board is null || _board.Doc is null) return;
        var context = _board.Context();
        var items = CanvasCommands.BarItems(context);
        var more = CanvasCommands.More(context).Count > 0;
        var chip = ChipOf(context, _board.Subject());
        var signature = $"{string.Join(",", items.Select(i => i.Spec is null ? "|" : i.Spec.Id + (i.Text ? "+" : "")))};{more};{chip.Text};{chip.Kind}";
        if (!force && signature == _signature) return;
        _signature = signature;
        _row.Children.Clear();
        _labels.Clear();
        HasContent = context.Count > 0 && (items.Count > 0 || more);
        if (chip.Text.Length > 0)
        {
            _row.Children.Add(Chip(chip));
            if (items.Count > 0) _row.Children.Add(Divider());
        }
        foreach (var (spec, text) in items) _row.Children.Add(spec is null ? Divider() : Command(spec, text));
        if (more) _row.Children.Add(More());
        // Measure with every label to tell the host how wide the full bar is, then narrow it if the host asks.
        foreach (var label in _labels) label.Visibility = Visibility.Visible;
        _row.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        FullWidth = _row.DesiredSize.Width + 12;
        ApplyCompact();
        Rebuilt?.Invoke();
    }

    /// <summary>A chip only where the node's own title is not already on screen: several nodes, or a collapsed batch (the tools then act on its cover).</summary>
    private static (string Text, NodeKind? Kind) ChipOf(CommandContext context, CanvasNode? subject) =>
        context.Count > 1 ? ($"已选 {context.Count} 个", null)
        : subject is not null && context.InBatch && !context.BatchOpen ? ("这一批 · 封面", subject.Kind)
        : ("", null);

    private static string Glyph(NodeKind kind) => kind switch
    {
        NodeKind.Text => "", NodeKind.Image => "", NodeKind.Video => "", NodeKind.Audio => "", NodeKind.Config => "", _ => "",
    };

    private static UIElement Divider() => new Microsoft.UI.Xaml.Shapes.Rectangle { Style = (Style)Application.Current.Resources["ToolbarDivider"] };

    /// <summary>A command button: icon alone with its name and shortcut in the tooltip, or icon plus text.</summary>
    private Button Command(CommandSpec spec, bool text)
    {
        var icon = new FontIcon { Glyph = spec.Glyph, FontSize = 15 };
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
        button.Click += async (_, _) => { if (_board is not null) await _board.RunAsync(spec.Id); };
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

    /// <summary>What is selected, when its title is not shown elsewhere; a collapsed batch says so, because the picture tools then act on its cover.</summary>
    private UIElement Chip((string Text, NodeKind? Kind) chip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        if (chip.Kind is { } kind) row.Children.Add(new FontIcon { Glyph = Glyph(kind), FontSize = 14, Foreground = Palette.Brush(kind, ActualTheme) });
        row.Children.Add(new TextBlock { Text = chip.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var border = new Border { Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center, Child = row, Background = AccentTint() };
        AutomationProperties.SetName(border, chip.Text);
        return border;
    }

    private static Brush AccentTint() =>
        new SolidColorBrush(Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Windows.UI.Color c ? Windows.UI.Color.FromArgb(0x2A, c.R, c.G, c.B) : Windows.UI.Color.FromArgb(0x2A, 0, 120, 212));
}
