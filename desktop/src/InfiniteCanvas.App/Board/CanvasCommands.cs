using System.Diagnostics;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// The handlers of the command table (<see cref="CanvasCommands"/>) and the surfaces built from it: the node menu and the two floating bars.
/// Every command acts on the whole selection; commands that only make sense for one node are hidden when several are selected.
/// </summary>
public sealed partial class CanvasView
{
    /// <summary>One element of a floating bar: a command button, a divider, or a fixed label.</summary>
    private sealed record BarItem(UIElement Element, CommandSpec? Spec = null, bool Divider = false);

    private readonly List<BarItem> _imageItems = [];
    private readonly List<BarItem> _selectionItems = [];
    private readonly TextBlock _selectionCount = new() { Style = (Style)Application.Current.Resources["SecondaryCaption"], VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 6, 0) };
    private Dictionary<string, Func<Task>>? _handlers;

    private CommandContext Context() => CommandContext.From(Doc, _selected,
        node => MarkSupport(node) is var (mask, points, markup) && (mask || points || markup), CanvasGenerator.CanGenerate, CanGroup, Generator.IsRunning);

    /// <summary>The one shown node that is selected (a collapsed batch counts as its cover), or null.</summary>
    private CanvasNode? Subject()
    {
        var hidden = Stacks.Hidden(Doc);
        var shown = Doc.Nodes.Where(n => _selected.Contains(n.Id) && !hidden.Contains(n)).Take(2).ToList();
        return shown.Count == 1 ? shown[0] : null;
    }

    /// <summary>Run a command on the current selection; a failure is logged and shown with the command's name as the title.</summary>
    public async Task RunAsync(string id)
    {
        if (IsClosing) return;
        var spec = CanvasCommands.Find(id);
        try { await Handlers[id](); }
        catch (Exception ex)
        {
            App.Log(ex);
            await Dialogs.MessageAsync(spec.Title, ex.Message);
        }
    }

    private Func<Task> Node(Func<CanvasNode, Task> action) => () => Subject() is { } node ? action(node) : Task.CompletedTask;
    private Func<Task> Node(Action<CanvasNode> action) => Node(node => { action(node); return Task.CompletedTask; });
    private Func<Task> Local(Func<CanvasNode, Task> action) => Node(node => TrackLocalAsync(() => action(node)));
    private Func<Task> Do(Action action) => () => { action(); return Task.CompletedTask; };

    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private string PathOf(CanvasNode node) => ProjectStore.AssetPath(Doc.Id, node.Content!);

    private Dictionary<string, Func<Task>> Handlers => _handlers ??= new()
    {
        ["new-text"] = Do(() => AddNodeAtCenter(NodeKind.Text)),
        ["new-image"] = Do(() => AddNodeAtCenter(NodeKind.Image)),
        ["new-video"] = Do(() => AddNodeAtCenter(NodeKind.Video)),
        ["new-audio"] = Do(() => AddNodeAtCenter(NodeKind.Audio)),
        ["new-config"] = Do(() => AddNodeAtCenter(NodeKind.Config)),
        ["import"] = () => ImportWithPickerAsync(),
        ["paste"] = () => PasteAsync(ViewCenterWorld),
        ["select-all"] = Do(() => Select(Doc.Nodes.Select(n => n.Id))),
        ["undo"] = Do(Undo),
        ["redo"] = Do(Redo),
        ["fit"] = Do(FitToContent),
        ["zoom-reset"] = Do(ResetZoom),
        ["match-width"] = Do(() => MatchSize(true, false)),
        ["match-height"] = Do(() => MatchSize(false, true)),
        ["match-both"] = Do(() => MatchSize(true, true)),

        ["generate"] = Node(node => Generator.GenerateAsync(node)),
        ["retry"] = Node(node => Generator.GenerateAsync(node)),
        ["stop"] = Node(node => Generator.Cancel(node.Id)),

        ["view"] = Node(node => { if (node is { Kind: NodeKind.Image, HasContent: true }) ShowPreview(node); }),

        ["mark"] = Local(EditMarksCoreAsync),
        ["crop"] = Local(node => ImageTools.CropAsync(this, node)),
        ["split"] = Local(node => ImageTools.SplitAsync(this, node)),
        ["angle"] = Local(node => ImageTools.AngleAsync(this, node)),
        ["upscale"] = Local(UpscaleAsync),
        ["reverse"] = Local(node => ImageTools.ReversePromptAsync(this, node)),

        ["promote"] = Local(PromoteAsync),
        ["keep-one"] = Local(KeepOneAsync),
        ["recompose"] = Local(node => ImageTools.RecomposeAsync(this, node)),
        ["library"] = AddSelectionToLibraryAsync,
        ["save-as"] = Node(node => SaveAsAsync(node, PathOf(node))),
        ["open"] = Node(node => OpenFile(PathOf(node))),
        ["reveal"] = Node(node => Process.Start("explorer.exe", $"/select,\"{PathOf(node)}\"")),

        ["compare"] = () => TrackLocalAsync(ShowCompareAsync),
        ["compose"] = () => ComposeInputs() is { Count: >= 2 } inputs ? TrackLocalAsync(() => ImageTools.ComposeAsync(this, inputs)) : Task.CompletedTask,
        ["group"] = Do(Group),
        ["ungroup"] = Do(Ungroup),
        ["expand"] = Node(ToggleStack),
        ["collapse"] = Node(ToggleStack),

        ["copy"] = CopySelectionAsync,
        ["duplicate"] = Do(Duplicate),
        ["rename"] = Node(RenameAsync),
        ["free-resize"] = Do(ToggleFreeResize),
        ["clear-marks"] = Node(node => { ClearMarks(node); _panel.Refresh(); }),

        ["delete"] = Do(DeleteSelection),
    };

    /// <summary>Free every selected picture and video from its aspect ratio, or lock them all again when they were all free.</summary>
    private void ToggleFreeResize()
    {
        var hidden = Stacks.Hidden(Doc);
        var nodes = Doc.Nodes.Where(n => _selected.Contains(n.Id) && !hidden.Contains(n) && n.Kind is NodeKind.Image or NodeKind.Video).ToList();
        if (nodes.Count == 0) return;
        var free = !nodes.All(n => n.FreeResize);
        History.Record();
        foreach (var node in nodes)
        {
            node.FreeResize = free;
            // Locking the ratio again snaps the node back to the media's own aspect.
            if (!free && node.NaturalWidth > 0) node.Height = Math.Round(node.Width * node.NaturalHeight / node.NaturalWidth);
        }
    }

    private async Task AddSelectionToLibraryAsync()
    {
        var hidden = Stacks.Hidden(Doc);
        var nodes = Doc.Nodes.Where(n => _selected.Contains(n.Id) && !hidden.Contains(n) && n.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio && n.HasContent).ToList();
        await TrackLocalAsync(async () => { foreach (var node in nodes) await AddToLibraryAsync(node); });
    }

    // ---------- Surfaces ----------

    private UIElement BarButton(CommandSpec spec, bool text)
    {
        var button = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"] };
        if (text) button.Content = spec.Name;
        else
        {
            button.Padding = new Thickness(10, 7, 10, 7);
            button.Content = new FontIcon { Glyph = spec.Glyph, FontSize = 15 };
        }
        ToolTipService.SetToolTip(button, spec.Tip);
        AutomationProperties.SetName(button, spec.Name);
        button.Click += async (_, _) => await RunAsync(spec.Id);
        return button;
    }

    private static UIElement BarDivider() => new Microsoft.UI.Xaml.Shapes.Rectangle { Style = (Style)Application.Current.Resources["ToolbarDivider"] };

    /// <summary>A bar's row from a list of command ids ("|" is a divider, null a label that is always shown).</summary>
    private StackPanel BarRow(List<BarItem> items, bool text, UIElement? lead, params string[] ids)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        if (lead is not null) items.Add(new BarItem(lead));
        foreach (var id in ids)
            items.Add(id == "|" ? new BarItem(BarDivider(), Divider: true) : new BarItem(BarButton(CanvasCommands.Find(id), text), CanvasCommands.Find(id)));
        foreach (var item in items) row.Children.Add(item.Element);
        return row;
    }

    /// <summary>Show the commands the selection offers, and only the dividers that still have something on both sides.</summary>
    private static void ApplyBar(List<BarItem> items, CommandContext context)
    {
        foreach (var item in items)
            if (item.Spec is { } spec) item.Element.Visibility = spec.Visible(context) ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].Divider) continue;
            var before = items.Take(i).Any(b => !b.Divider && b.Element.Visibility == Visibility.Visible);
            var after = items.Skip(i + 1).TakeWhile(b => !b.Divider).Any(b => b.Element.Visibility == Visibility.Visible);
            items[i].Element.Visibility = before && after ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private Border BuildSelectionBar() => FloatingBar(
        BarRow(_selectionItems, true, _selectionCount, "|", "match-width", "match-height", "match-both", "|", "compare", "compose", "|", "group", "ungroup"), PositionSelectionBar);

    private Border BuildImageBar() => FloatingBar(
        BarRow(_imageItems, false, null, "view", "|", "mark", "crop", "split", "angle", "upscale", "reverse", "|", "library", "open"), PositionImageBar);

    /// <summary>The node menu: the command table's offer for the current selection, grouped, with shortcuts. The selection is already set.</summary>
    private MenuFlyout BuildNodeMenu()
    {
        var context = Context();
        var menu = new MenuFlyout();
        var subject = Subject();
        menu.Items.Add(new MenuFlyoutItem { Text = subject is null ? $"已选 {context.Count} 个" : context.InBatch && !context.BatchOpen ? $"{subject.Title} · 这一批 · 封面" : subject.Title, IsEnabled = false });
        var offered = CanvasCommands.Offered(context);
        foreach (var group in offered.GroupBy(c => c.Group))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var spec in group)
            {
                if (spec.Id == "free-resize")
                {
                    var free = new ToggleMenuFlyoutItem { Text = spec.Name, IsChecked = context.AllFree, Icon = new FontIcon { Glyph = spec.Glyph }, KeyboardAcceleratorTextOverride = spec.Shortcut ?? "" };
                    free.Click += async (_, _) => await RunAsync(spec.Id);
                    menu.Items.Add(free);
                }
                else menu.Items.Add(NodeMenuItem(spec));
            }
        }
        return menu;
    }

    private MenuFlyoutItem NodeMenuItem(CommandSpec spec)
    {
        var item = new MenuFlyoutItem { Text = spec.Name, Icon = new FontIcon { Glyph = spec.Glyph } };
        if (spec.Shortcut is not null) item.KeyboardAcceleratorTextOverride = spec.Shortcut;
        item.Click += async (_, _) => await RunAsync(spec.Id);
        return item;
    }

    /// <summary>Menu entry for a command of the table whose handler needs a point (the create menu).</summary>
    internal static MenuFlyoutItem Item(string id, Func<Task> action) => Item(CanvasCommands.Find(id), action);

    private static MenuFlyoutItem Item(CommandSpec spec, Func<Task> action) => Item(spec.Name, spec.Glyph, action, spec.Shortcut);
}
