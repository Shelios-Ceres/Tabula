using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using InfiniteCanvas.Agents;
using InfiniteCanvas.Core.Canvas;
using InfiniteCanvas.Core.Models;
using InfiniteCanvas.Core.Settings;
using InfiniteCanvas.Core.Storage;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ModelContextProtocol;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Canvas = Microsoft.UI.Xaml.Controls.Canvas;
using Rect = InfiniteCanvas.Core.Canvas.Rect;

namespace InfiniteCanvas.App.Board;

/// <summary>
/// The infinite canvas: a Win2D layer for the grid and connections, a zoomed XAML world layer for nodes,
/// a Win2D overlay for selection/connection drafts and a screen layer for the prompt panel.
/// Only nodes near the viewport are realized as views.
/// </summary>
public sealed partial class CanvasView : UserControl
{
    private const double MinZoom = 0.05, MaxZoom = 5;
    private enum Gesture { None, Pan, Box, Drag, Resize, Connect }

    public CanvasDocument Doc { get; private set; } = new();
    public CanvasHistory History { get; private set; } = null!;
    public CanvasGenerator Generator { get; private set; } = null!;
    public event Action? Changed;
    public event Action? ViewportChanged;
    public bool IsClosing { get; private set; }
    private readonly HashSet<Task> _localTasks = [];

    private readonly Dictionary<string, NodeView> _views = [];
    private readonly HashSet<CanvasNode> _tracked = [];
    private readonly HashSet<string> _selected = [];
    private readonly PromptPanel _panel;
    private readonly Border _selectionBar;
    /// <summary>Image tools above a single selected picture.</summary>
    private readonly Border _imageBar;
    private Button? _imageMarkButton;
    private Button[] _pixelTools = [];
    /// <summary>The 对比 button of the selection bar and its divider, shown when exactly two bitmap pictures are selected.</summary>
    private readonly Button _composeButton = new() { Content = "拼版…", Style = (Style)Application.Current.Resources["ToolbarButton"], Visibility = Visibility.Collapsed };
    private readonly StackPanel _compareGroup = new() { Orientation = Orientation.Horizontal, Spacing = 2, Visibility = Visibility.Collapsed };
    private readonly TextBlock _selectionCount = new() { Style = (Style)Application.Current.Resources["SecondaryCaption"], VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 6, 0) };
    private readonly List<string> _order = [];
    private Thickness _safe = new(0, 68, 0, 72);
    private string? _selectedConnection;
    private bool _suspend, _space, _moved;
    private double _lastZoom = -1;

    private Gesture _gesture;
    private Point _startScreen, _startWorld, _currentWorld;
    private Viewport _startViewport;
    private HashSet<string> _boxInitial = [];
    private List<(CanvasNode Node, double X, double Y)> _dragStart = [];
    private CanvasNode? _active;
    private bool _connectFromSource;
    private (double W, double H) _resizeStart;
    private Color _ink = Colors.Black, _accent = Colors.DodgerBlue;
    /// <summary>Node pressed by the current gesture, so a click without movement can open its stack.</summary>
    private CanvasNode? _pressed;
    /// <summary>Nodes added since the canvas opened (not by undo / redo) that still have to play their arrival animation when realized.</summary>
    private readonly HashSet<CanvasNode> _fresh = [];
    private bool _restoring;
    /// <summary>Running viewport glide; any other viewport change stops it.</summary>
    private EventHandler<object>? _glide;
    private bool _gliding;
    private Popup? _preview;
    /// <summary>When a stack last opened or closed: the second click of a double click on a stack must not also open the preview.</summary>
    private long _stackToggled;

    public CanvasView()
    {
        InitializeComponent();
        _panel = new PromptPanel(this) { Visibility = Visibility.Collapsed };
        ScreenLayer.Children.Add(_panel);
        _panel.CastShadowOn(Backdrop);
        _selectionBar = BuildSelectionBar();
        ScreenLayer.Children.Add(_selectionBar);
        _imageBar = BuildImageBar();
        ScreenLayer.Children.Add(_imageBar);
        _panel.SizeChanged += (_, _) => PositionPanel();
        Motion.ShowHide(_panel, 0.5f, 0);
        Motion.ShowHide(_selectionBar, 0.5f, 1);
        Motion.ShowHide(_imageBar, 0.5f, 1);
        Motion.ShowHide(EmptyHint);
        // Text boxes and media controls inside nodes swallow pointer presses; still select the node they belong to.
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Handled && _gesture == Gesture.None && HitTest(e.OriginalSource).View is { } view && !_selected.Contains(view.Node.Id))
                Select((e.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)) != 0 ? [.. _selected, view.Node.Id] : [view.Node.Id]);
        }), true);
        ActualThemeChanged += (_, _) => UpdateColors();
        Loaded += (_, _) => { UpdateColors(); ApplyViewport(); FocusCanvas(); };
        Unloaded += (_, _) =>
        {
            // A window-level popup would otherwise stay over the next page.
            ClosePreview();
            foreach (var view in _views.Values) view.Detach();
            Backdrop.RemoveFromVisualTree();
            Overlay.RemoveFromVisualTree();
        };
    }

    public void Load(CanvasDocument doc)
    {
        Doc = doc;
        // A damaged or hand-edited project.json may carry no usable zoom; the grid loop and ToWorld divide by it.
        if (doc.Viewport.K is not (>= MinZoom and <= MaxZoom)) doc.Viewport = new(0, 0, 1);
        History = new CanvasHistory(doc);
        Generator = new CanvasGenerator(doc, DispatcherQueue, () => History.Record(), RaiseChanged);
        Generator.TargetsPrepared += targets => EnsureVisible(CanvasGraph.Bounds(targets));
        Generator.RunningChanged += () => _panel.Refresh();
        // Anything that was mid-generation when the app closed is interrupted, except resumable vendor tasks.
        foreach (var node in doc.Nodes.Where(n => n.Status == NodeStatus.Loading && n.TaskId is null))
        {
            node.Status = NodeStatus.Error;
            node.Error = CanvasGenerator.Interrupted;
        }
        doc.Nodes.CollectionChanged += (_, e) =>
        {
            if (!_restoring && e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace && e.NewItems is { } added) foreach (CanvasNode node in added) _fresh.Add(node);
            if (!_suspend) SyncTracking();
        };
        doc.Connections.CollectionChanged += (_, _) => { if (!_suspend) ConnectionsChanged(); };
        SyncTracking();
        ApplyViewport();
        Generator.ResumeTasks();
    }

    /// <summary>Leaving stops running generations, so ask first; OpenAI Responses image tasks resume on the next open.</summary>
    public async Task<bool> ConfirmLeaveAsync() => Generator.RunningCount == 0 && _localTasks.Count == 0
        || await Dialogs.ConfirmAsync("离开画布？", $"有 {Generator.RunningCount} 个生成任务、{_localTasks.Count} 个本地操作仍在进行。离开前会保存已完成的结果并等待导入结束；生成会中断，OpenAI Responses 生图任务下次打开画布会继续查询。", "保存并离开", destructive: true);

    public Task PrepareLeaveAsync()
    {
        IsClosing = true;
        return Task.WhenAll(_localTasks.Append(Generator.ShutdownAsync()).ToArray());
    }

    public void ResumeAfterCancelledLeave()
    {
        IsClosing = false;
        Generator.ResumeAfterCancelledLeave();
    }

    private async Task<T> TrackLocalAsync<T>(Func<Task<T>> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _localTasks.Add(done.Task);
        try { return await action(); }
        finally { _localTasks.Remove(done.Task); done.SetResult(); }
    }

    private Task TrackLocalAsync(Func<Task> action) => TrackLocalAsync(async () => { await action(); return true; });

    private void RaiseChanged() => Changed?.Invoke();

    private void UpdateColors()
    {
        _ink = ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Color color) _accent = color;
        Backdrop.Invalidate();
        Overlay.Invalidate();
    }

    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ---------- Document tracking ----------

    private void SyncTracking()
    {
        var current = Doc.Nodes.ToHashSet();
        foreach (var old in _tracked.Where(n => !current.Contains(n)).ToList())
        {
            old.PropertyChanged -= Node_PropertyChanged;
            _tracked.Remove(old);
        }
        foreach (var node in current.Where(n => _tracked.Add(n))) node.PropertyChanged += Node_PropertyChanged;
        foreach (var (id, view) in _views.Where(pair => !current.Contains(pair.Value.Node)).ToList())
        {
            view.Detach();
            World.Children.Remove(view);
            _views.Remove(id);
        }
        _fresh.IntersectWith(current);
        _selected.RemoveWhere(id => Doc.Find(id) is null);
        _order.RemoveAll(id => !_selected.Contains(id));
        EmptyHint.Visibility = Doc.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Realize();
        ConnectionsChanged();
    }

    private void ConnectionsChanged()
    {
        if (_selectedConnection is not null && Doc.Connections.All(c => c.Id != _selectedConnection)) _selectedConnection = null;
        foreach (var view in _views.Values) view.SyncConfig();
        Backdrop.Invalidate();
        UpdatePanel();
        RaiseChanged();
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CanvasNode.X) or nameof(CanvasNode.Y) or nameof(CanvasNode.Width) or nameof(CanvasNode.Height))
        {
            Backdrop.Invalidate();
            if (sender is CanvasNode moved && _selected.Contains(moved.Id)) PositionPanel();
        }
        if (e.PropertyName is nameof(CanvasNode.Content) or nameof(CanvasNode.Prompt))
            foreach (var view in _views.Values.Where(v => v.Node.Kind == NodeKind.Config)) view.SyncConfig();
        // Opening, closing or forming a stack changes which cards exist; a member's status shows on its lead.
        if (e.PropertyName is nameof(CanvasNode.Options) or nameof(CanvasNode.Status) && sender is CanvasNode changed && Stacks.Of(changed) is not null) Realize();
        if (e.PropertyName != nameof(CanvasNode.Preview)) RaiseChanged();
    }

    // ---------- Viewport ----------

    private Viewport V
    {
        get => Doc.Viewport;
        set
        {
            if (!_gliding) StopGlide();
            Doc.Viewport = value;
            ApplyViewport();
            RaiseChanged();
        }
    }

    /// <summary>
    /// Glide to a viewport (Windows 11 decelerate curve): the world point in the middle of the free area and the zoom (in log space)
    /// move together, so the content never swings sideways while zooming. Gestures, the wheel and other moves stop it.
    /// </summary>
    private void Glide(Viewport target)
    {
        StopGlide();
        if (Root.ActualWidth <= 0) { V = target; return; }
        var from = V;
        var (a, b) = (ToWorld(Center), new Point((Center.X - target.X) / target.K, (Center.Y - target.Y) / target.K));
        var clock = Stopwatch.StartNew();
        _glide = (_, _) =>
        {
            var t = Motion.EaseOut(clock.Elapsed.TotalMilliseconds / Motion.Normal.TotalMilliseconds);
            var k = Math.Exp(Math.Log(from.K) + (Math.Log(target.K) - Math.Log(from.K)) * t);
            var (x, y) = (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            _gliding = true;
            V = t >= 1 ? target : new(Center.X - x * k, Center.Y - y * k, k);
            _gliding = false;
            if (t >= 1) StopGlide();
        };
        CompositionTarget.Rendering += _glide;
    }

    private void StopGlide()
    {
        if (_glide is null) return;
        CompositionTarget.Rendering -= _glide;
        _glide = null;
    }

    public Viewport Viewport
    {
        get => V;
        set => V = value with { K = Math.Clamp(value.K, MinZoom, MaxZoom) };
    }

    public double Zoom => V.K;

    /// <summary>Screen margins covered by the page's toolbars and library panel; panel placement, view centring, fitting and auto-pan keep clear of them.</summary>
    public Thickness SafeArea
    {
        get => _safe;
        set
        {
            _safe = value;
            EmptyHint.Margin = value;
            PositionPanel();
        }
    }

    private Point Center => new(_safe.Left + (Root.ActualWidth - _safe.Left - _safe.Right) / 2, _safe.Top + (Root.ActualHeight - _safe.Top - _safe.Bottom) / 2);
    private Point ToWorld(Point p) => new((p.X - V.X) / V.K, (p.Y - V.Y) / V.K);
    private Point ToScreen(double x, double y) => new(x * V.K + V.X, y * V.K + V.Y);
    public Point ViewCenterWorld => ToWorld(Center);

    /// <summary>The whole canvas area currently on screen, in world coordinates.</summary>
    public Rect VisibleWorld
    {
        get
        {
            var (topLeft, bottomRight) = (ToWorld(new Point(0, 0)), ToWorld(new Point(Root.ActualWidth, Root.ActualHeight)));
            return new(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        }
    }

    /// <summary>Pan, keeping the zoom, so a world point sits in the middle of the free area; glides unless it follows a drag (the mini map).</summary>
    public void CenterOn(Point world, bool glide = false)
    {
        var target = new Viewport(Center.X - world.X * V.K, Center.Y - world.Y * V.K, V.K);
        if (glide) Glide(target);
        else V = target;
    }

    private void ApplyViewport()
    {
        WorldTransform.ScaleX = WorldTransform.ScaleY = V.K;
        WorldTransform.TranslateX = V.X;
        WorldTransform.TranslateY = V.Y;
        if (_lastZoom != V.K)
        {
            _lastZoom = V.K;
            foreach (var view in _views.Values) view.SetZoom(V.K);
        }
        Realize();
        Backdrop.Invalidate();
        Overlay.Invalidate();
        PositionPanel();
        ViewportChanged?.Invoke();
    }

    public void ZoomAt(Point screen, double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var world = ToWorld(screen);
        V = new(screen.X - world.X * zoom, screen.Y - world.Y * zoom, zoom);
    }

    /// <summary>Toolbar and keyboard zoom glide around the middle of the free area.</summary>
    public void ZoomBy(double factor) => GlideZoom(V.K * factor);
    public void ResetZoom() => GlideZoom(1);

    private void GlideZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var world = ToWorld(Center);
        Glide(new(Center.X - world.X * zoom, Center.Y - world.Y * zoom, zoom));
    }

    public void FitToContent()
    {
        if (Doc.Nodes.Count == 0) { Glide(new(Center.X, Center.Y, 1)); return; }
        var bounds = CanvasGraph.Bounds(Doc.Nodes);
        var zoom = Math.Clamp(Math.Min((Root.ActualWidth - _safe.Left - _safe.Right - 160) / Math.Max(1, bounds.Width), (Root.ActualHeight - _safe.Top - _safe.Bottom - 80) / Math.Max(1, bounds.Height)), MinZoom, 1);
        var (cx, cy) = (bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        Glide(new(Center.X - cx * zoom, Center.Y - cy * zoom, zoom));
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The preview is sized to the old canvas area.
        ClosePreview();
        Realize();
        PositionPanel();
    }

    /// <summary>Create views only for nodes near the viewport; distant ones are dropped to keep large canvases fluid.</summary>
    private void Realize()
    {
        if (Root.ActualWidth <= 0) return;
        var margin = 400 / V.K;
        var view = new Rect(-V.X / V.K - margin, -V.Y / V.K - margin, (Root.ActualWidth - V.X) / V.K + margin, (Root.ActualHeight - V.Y) / V.K + margin);
        var anchor = AnchorNode();
        var hidden = Stacks.Hidden(Doc);
        foreach (var (index, node) in Doc.Nodes.Index())
        {
            // Members under a collapsed stack's lead never get a view.
            var visible = !hidden.Contains(node) && (view.Intersects(Rect.Of(node)) || _selected.Contains(node.Id) || node.Kind == NodeKind.Group);
            if (visible && !_views.ContainsKey(node.Id))
            {
                var nodeView = new NodeView(node, this) { IsSelected = _selected.Contains(node.Id), IsAnchor = node == anchor };
                nodeView.SetZoom(V.K);
                _views[node.Id] = nodeView;
                World.Children.Add(nodeView);
                if (_fresh.Remove(node)) Motion.Appear(nodeView);
            }
            else if (!visible && _views.Remove(node.Id, out var old))
            {
                old.Detach();
                World.Children.Remove(old);
            }
            // Stack by document order (groups underneath), not by the order views happened to be created in.
            if (_views.TryGetValue(node.Id, out var realized)) Canvas.SetZIndex(realized, node.Kind == NodeKind.Group ? index - Doc.Nodes.Count : index);
        }
        RefreshStacks(hidden);
    }

    /// <summary>Count and failures on each realized stack lead; every other card shows no stack chrome.</summary>
    private void RefreshStacks(HashSet<CanvasNode> hidden)
    {
        var stacks = Doc.Nodes.Where(n => Stacks.Of(n) is not null).GroupBy(n => Stacks.Of(n)!).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var view in _views.Values)
        {
            var members = Stacks.Of(view.Node) is { } id && stacks.TryGetValue(id, out var list) && list.Count > 1 && list[0] == view.Node ? list : null;
            view.SetStack(members?.Count ?? 0, Stacks.IsOpen(view.Node), members?.Count(n => n.Status == NodeStatus.Success && n.HasContent) ?? 0, members?.Count(n => n.Status == NodeStatus.Error) ?? 0);
        }
    }

    /// <summary>The ids plus every member of a collapsed stack whose lead is among them: moving, deleting or grouping the lead acts on the whole stack.</summary>
    private HashSet<string> WithStacks(IEnumerable<string> ids) => Stacks.Expand(Doc, ids);

    /// <summary>Spread a collapsed stack into a grid, or gather an opened one back under its lead, gliding the cards. One undo step.</summary>
    public void ToggleStack(CanvasNode node)
    {
        if (Stacks.Of(node) is not { } id || _gesture != Gesture.None) return;
        var members = Stacks.Members(Doc, id);
        if (members.Count < 2) return;
        _stackToggled = Environment.TickCount64;
        History.Record();
        if (!Stacks.IsOpen(node))
        {
            var grid = Stacks.Open(members);
            Move(members.Select((m, i) => (m, grid[i].X, grid[i].Y)).ToList(), null);
            EnsureVisible(new Rect(grid.Min(g => g.X), grid.Min(g => g.Y), grid.Max(g => g.X) + members.Max(m => m.Width), grid.Max(g => g.Y) + members.Max(m => m.Height)));
            return;
        }
        // The card the user picked becomes the new cover of the stack.
        if (members.FirstOrDefault(m => _selected.Contains(m.Id)) is { } cover && cover != members[0])
        {
            Doc.Nodes.Move(Doc.Nodes.IndexOf(cover), Doc.Nodes.IndexOf(members[0]));
            members = Stacks.Members(Doc, id);
        }
        var lead = members[0];
        // The cover rides on top while the others slide under it; the next Realize restores document order.
        if (_views.TryGetValue(lead.Id, out var leadView)) Canvas.SetZIndex(leadView, Doc.Nodes.Count + 1);
        Move(members.Select(m => (m, lead.X, lead.Y)).ToList(), () =>
        {
            foreach (var member in members) member.SetOption(NodeKeys.StackOpen, null);
            Select(_selected.Where(s => members.All(m => m.Id != s) || s == lead.Id).ToList());
        });
    }

    /// <summary>Glide nodes to new positions with the decelerate curve; positions are real document values every frame, so connections and shadows follow.</summary>
    private void Move(List<(CanvasNode Node, double X, double Y)> moves, Action? done)
    {
        var starts = moves.Select(m => (m.Node.X, m.Node.Y)).ToList();
        var clock = Stopwatch.StartNew();
        EventHandler<object>? tick = null;
        tick = (_, _) =>
        {
            var t = Motion.EaseOut(clock.Elapsed.TotalMilliseconds / 300);
            for (var i = 0; i < moves.Count; i++)
            {
                moves[i].Node.X = starts[i].X + (moves[i].X - starts[i].X) * t;
                moves[i].Node.Y = starts[i].Y + (moves[i].Y - starts[i].Y) * t;
            }
            if (t < 1) return;
            CompositionTarget.Rendering -= tick;
            done?.Invoke();
        };
        CompositionTarget.Rendering += tick;
    }

    // ---------- Selection ----------

    public IReadOnlyCollection<string> Selection => _selected;

    public void Select(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        _selectedConnection = null;
        _selected.Clear();
        foreach (var id in list) _selected.Add(id);
        // Click order is kept so the first node picked stays the reference for size matching.
        _order.RemoveAll(id => !_selected.Contains(id));
        foreach (var id in list.Where(id => !_order.Contains(id))) _order.Add(id);
        var anchor = AnchorNode()?.Id;
        foreach (var (id, view) in _views)
        {
            view.IsSelected = _selected.Contains(id);
            view.IsAnchor = id == anchor;
        }
        Realize();
        Backdrop.Invalidate();
        UpdatePanel();
    }

    /// <summary>The reference node for size matching: the first-clicked non-group node of a multi-selection.</summary>
    private CanvasNode? AnchorNode() => Doc.Nodes.Count(n => n.Kind != NodeKind.Group && _selected.Contains(n.Id)) < 2
        ? null
        : _order.Select(Doc.Find).FirstOrDefault(n => n is not null && n.Kind != NodeKind.Group);

    /// <summary>Match the selected nodes to the reference node's width, height or both. Media keep their aspect unless both are matched.</summary>
    public void MatchSize(bool width, bool height)
    {
        if (AnchorNode() is not { } anchor) return;
        var targets = Doc.Nodes.Where(n => _selected.Contains(n.Id) && n.Kind != NodeKind.Group && n != anchor).ToList();
        if (targets.Count == 0) return;
        History.Record();
        CanvasGraph.MatchSize(anchor, targets, width, height);
    }

    private Border BuildSelectionBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        Button Add(string text, string tip, Action action)
        {
            var button = new Button { Content = text, Style = (Style)Application.Current.Resources["ToolbarButton"] };
            ToolTipService.SetToolTip(button, tip);
            button.Click += (_, _) => action();
            row.Children.Add(button);
            return button;
        }
        void Divider() => row.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Style = (Style)Application.Current.Resources["ToolbarDivider"] });
        row.Children.Add(_selectionCount);
        Divider();
        Add("同宽", "宽度统一为基准节点（图片、视频保持比例）", () => MatchSize(true, false));
        Add("同高", "高度统一为基准节点（图片、视频保持比例）", () => MatchSize(false, true));
        Add("同宽高", "宽和高都统一为基准节点", () => MatchSize(true, true));
        _compareGroup.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Style = (Style)Application.Current.Resources["ToolbarDivider"] });
        var compare = new Button { Content = "对比", Style = (Style)Application.Current.Resources["ToolbarButton"] };
        ToolTipService.SetToolTip(compare, "并排对比选中的两张图片 (C)");
        AutomationProperties.SetName(compare, "对比");
        compare.Click += async (_, _) => await TrackLocalAsync(ShowCompareAsync);
        _compareGroup.Children.Add(compare);
        row.Children.Add(_compareGroup);
        ToolTipService.SetToolTip(_composeButton, "把选中的图片拼成一张");
        AutomationProperties.SetName(_composeButton, "拼版");
        _composeButton.Click += async (_, _) => { if (ComposeInputs() is { Count: >= 2 } inputs) await TrackLocalAsync(() => ImageTools.ComposeAsync(this, inputs)); };
        row.Children.Add(_composeButton);
        Divider();
        Add("编组", "把选中节点放进一个分组 (Ctrl+G)", Group);
        return FloatingBar(row, PositionSelectionBar);
    }

    /// <summary>The single selected image that has a picture, or null.</summary>
    private CanvasNode? ImageBarNode() => _selected.Count == 1 && Doc.Find(_selected.First()) is { Kind: NodeKind.Image, HasContent: true } node ? node : null;

    private Border BuildImageBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        Button Add(string glyph, string tip, Func<CanvasNode, Task> action)
        {
            var button = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Padding = new Thickness(10, 7, 10, 7), Content = new FontIcon { Glyph = glyph, FontSize = 15 } };
            ToolTipService.SetToolTip(button, tip);
            AutomationProperties.SetName(button, tip);
            button.Click += async (_, _) =>
            {
                if (ImageBarNode() is not { } node) return;
                try { if (!IsClosing) await TrackLocalAsync(() => action(node)); }
                catch (Exception ex)
                {
                    App.Log(ex);
                    await Dialogs.MessageAsync(tip, ex.Message);
                }
            };
            row.Children.Add(button);
            return button;
        }
        _imageMarkButton = Add("", "局部编辑", EditMarksCoreAsync);
        _pixelTools =
        [
            Add("", "裁剪", node => ImageTools.CropAsync(this, node)),
            Add("", "拆分", node => ImageTools.SplitAsync(this, node)),
            Add("", "多角度", node => ImageTools.AngleAsync(this, node)),
            Add("", "本地放大", UpscaleAsync),
            Add("", "反推提示词", node => ImageTools.ReversePromptAsync(this, node)),
        ];
        row.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Style = (Style)Application.Current.Resources["ToolbarDivider"] });
        Add("", "加入资料库", AddToLibraryAsync);
        Add("", "用默认应用打开", node =>
        {
            Process.Start(new ProcessStartInfo(ProjectStore.AssetPath(Doc.Id, node.Content!)) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        return FloatingBar(row, PositionImageBar);
    }

    /// <summary>
    /// A floating glass capsule over the canvas: the show / hide animation goes on the outer wrapper and the shadow's Z translation on
    /// the inner capsule, like the prompt panel. With both on one element its hit-test bounds no longer matched where it was drawn,
    /// so clicks on its buttons fell through to the canvas and cleared the selection.
    /// </summary>
    private Border FloatingBar(UIElement content, Action reposition)
    {
        var capsule = new Border { Style = (Style)Application.Current.Resources["GlassCapsule"], Child = content };
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(Backdrop);
        capsule.Shadow = shadow;
        capsule.Translation = new Vector3(0, 0, 24);
        var bar = new Border { Child = capsule, Visibility = Visibility.Collapsed };
        // Clicks on the bar must not start canvas gestures underneath.
        bar.PointerPressed += (_, e) => e.Handled = true;
        bar.DoubleTapped += (_, e) => e.Handled = true;
        bar.RightTapped += (_, e) => e.Handled = true;
        bar.SizeChanged += (_, _) => reposition();
        return bar;
    }

    private void PositionImageBar()
    {
        var node = _gesture is Gesture.None or Gesture.Pan ? ImageBarNode() : null;
        _imageBar.Visibility = node is null ? Visibility.Collapsed : Visibility.Visible;
        if (node is null) return;
        if (_imageMarkButton is not null)
            _imageMarkButton.Visibility = MarkSupport(node) is var (mask, points, markup) && (mask || points || markup) ? Visibility.Visible : Visibility.Collapsed;
        // SVG has no pixels for the bitmap tools.
        foreach (var tool in _pixelTools) tool.Visibility = Media.IsSvg(node.Content) ? Visibility.Collapsed : Visibility.Visible;
        var topLeft = ToScreen(node.X, node.Y);
        var right = ToScreen(node.Right, node.Y).X;
        var (width, height) = (Math.Max(_imageBar.ActualWidth, 300), Math.Max(_imageBar.ActualHeight, 44));
        var minLeft = _safe.Left + 8;
        var left = Math.Clamp((topLeft.X + right) / 2 - width / 2, minLeft, Math.Max(minLeft, Root.ActualWidth - width - 8));
        // Above the node's floating title; the prompt panel sits below the node.
        var top = Math.Max(_safe.Top, topLeft.Y - height - 34);
        Canvas.SetLeft(_imageBar, left);
        Canvas.SetTop(_imageBar, Math.Min(top, Math.Max(_safe.Top, Root.ActualHeight - height - 8)));
    }

    private void PositionSelectionBar()
    {
        PositionImageBar();
        // Decide visibility first: while dragging, the bar is hidden and every node move must stay cheap.
        var show = _gesture is Gesture.None or Gesture.Pan && AnchorNode() is not null;
        _selectionBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var nodes = Doc.Nodes.Where(n => _selected.Contains(n.Id)).ToList();
        _selectionCount.Text = $"已选 {nodes.Count} 个";
        var pictures = ComposeInputs().Count;
        _compareGroup.Visibility = pictures == 2 ? Visibility.Visible : Visibility.Collapsed;
        _composeButton.Visibility = pictures >= 2 ? Visibility.Visible : Visibility.Collapsed;
        var bounds = CanvasGraph.Bounds(nodes);
        var topLeft = ToScreen(bounds.Left, bounds.Top);
        var bottom = ToScreen(bounds.Right, bounds.Bottom);
        var (width, height) = (Math.Max(_selectionBar.ActualWidth, 260), Math.Max(_selectionBar.ActualHeight, 44));
        var minLeft = _safe.Left + 8;
        var left = Math.Clamp((topLeft.X + bottom.X) / 2 - width / 2, minLeft, Math.Max(minLeft, Root.ActualWidth - width - 8));
        // Above the selection, or below it when the top toolbar would cover it.
        var top = topLeft.Y - height - 34 < _safe.Top ? bottom.Y + 14 : topLeft.Y - height - 34;
        Canvas.SetLeft(_selectionBar, left);
        Canvas.SetTop(_selectionBar, Math.Clamp(top, _safe.Top, Math.Max(_safe.Top, Root.ActualHeight - height - 8)));
    }

    private void UpdatePanel()
    {
        var node = _selected.Count == 1 && _gesture != Gesture.Box ? Doc.Find(_selected.First()) : null;
        if (node is null || !CanvasGenerator.CanGenerate(node))
        {
            _panel.Visibility = Visibility.Collapsed;
            _panel.Bind(null);
            PositionSelectionBar();
            return;
        }
        if (_panel.Node != node) _panel.Bind(node);
        else _panel.Refresh();
        _panel.Visibility = Visibility.Visible;
        PositionPanel();
    }

    private void PositionPanel()
    {
        PositionSelectionBar();
        if (_panel.Node is not { } node || _panel.Visibility != Visibility.Visible) return;
        var anchor = ToScreen(node.X + node.Width / 2, node.Bottom);
        var width = _panel.ActualWidth > 0 ? _panel.ActualWidth : 480;
        var height = _panel.ActualHeight > 0 ? _panel.ActualHeight : 180;
        var minLeft = _safe.Left + 8;
        var left = Math.Clamp(anchor.X - width / 2, minLeft, Math.Max(minLeft, Root.ActualWidth - width - 8));
        var top = anchor.Y + 14;
        if (top + height > Root.ActualHeight - _safe.Bottom - 16) top = ToScreen(0, node.Y).Y - height - 14;
        Canvas.SetLeft(_panel, left);
        Canvas.SetTop(_panel, Math.Clamp(top, _safe.Top, Math.Max(_safe.Top, Root.ActualHeight - height - 8)));
    }

    // ---------- Hit testing ----------

    private (NodeView? View, string? Part) HitTest(object source)
    {
        string? part = null;
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == _panel) return (null, "panel");
            if (element is FrameworkElement { Tag: string tag } && tag is "source" or "target" or "resize") part ??= tag;
            if (element is NodeView view) return (view, part);
            if (element == Root) break;
        }
        return (null, null);
    }

    private CanvasNode? NodeAt(Point world, CanvasNode? exclude = null)
    {
        var hidden = Stacks.Hidden(Doc);
        return Doc.Nodes.Reverse()
        .Where(n => n != exclude && !hidden.Contains(n) && Rect.Of(n).Contains(world.X, world.Y))
        .OrderBy(n => n.Kind == NodeKind.Group ? 1 : 0)
        .FirstOrDefault();
    }

    private static Point Out(CanvasNode node) => new(node.Right, node.Y + node.Height / 2);
    private static Point In(CanvasNode node) => new(node.X, node.Y + node.Height / 2);

    private static (Vector2 A, Vector2 B, Vector2 C, Vector2 D) Curve(Point from, Point to)
    {
        var dx = (float)Math.Max(40, Math.Abs(to.X - from.X) * 0.5);
        var a = new Vector2((float)from.X, (float)from.Y);
        var d = new Vector2((float)to.X, (float)to.Y);
        return (a, a + new Vector2(dx, 0), d - new Vector2(dx, 0), d);
    }

    /// <summary>
    /// Where each connection is drawn (null when an end is missing). A batch's members share their inputs, so an input the stack's lead
    /// also has is drawn once, to the lead; members hidden under a collapsed lead draw from / to the lead. Only the drawing changes:
    /// every member keeps its own connections for generation and retries.
    /// </summary>
    private Func<CanvasConnection, (CanvasNode From, CanvasNode To)?> LinkEnds()
    {
        var byId = new Dictionary<string, CanvasNode>();
        var leads = new Dictionary<string, CanvasNode>();
        foreach (var node in Doc.Nodes)
        {
            byId.TryAdd(node.Id, node);
            if (Stacks.Of(node) is { } stack) leads.TryAdd(stack, node);
        }
        var hidden = Stacks.Hidden(Doc);
        var pairs = Doc.Connections.Select(c => (c.From, c.To)).ToHashSet();
        CanvasNode Shown(CanvasNode node) => hidden.Contains(node) ? leads[Stacks.Of(node)!] : node;
        return connection =>
        {
            if (!byId.TryGetValue(connection.From, out var from) || !byId.TryGetValue(connection.To, out var to)) return null;
            var end = Shown(to);
            if (Stacks.Of(to) is { } stack && leads[stack] is var lead && end != lead && pairs.Contains((connection.From, lead.Id))) end = lead;
            return (Shown(from), end);
        };
    }

    /// <summary>The lines on screen: one per distinct pair of drawn ends, keeping the first connection as its representative.</summary>
    private IEnumerable<(CanvasConnection Connection, CanvasNode From, CanvasNode To)> Links()
    {
        var ends = LinkEnds();
        var drawn = new HashSet<(CanvasNode, CanvasNode)>();
        foreach (var connection in Doc.Connections)
            if (ends(connection) is { } line && line.From != line.To && drawn.Add(line)) yield return (connection, line.From, line.To);
    }

    /// <summary>Box around a curve's control points (the curve always lies inside it), grown by <paramref name="pad"/>.</summary>
    private static Rect Hull(Point from, Point to, double pad)
    {
        var (a, b, c, d) = Curve(from, to);
        return new(Math.Min(a.X, c.X) - pad, Math.Min(a.Y, d.Y) - pad, Math.Max(b.X, d.X) + pad, Math.Max(a.Y, d.Y) + pad);
    }

    private CanvasConnection? ConnectionAt(Point world)
    {
        var tolerance = 8 / V.K;
        var p = new Vector2((float)world.X, (float)world.Y);
        foreach (var (connection, from, to) in Links().Reverse())
        {
            if (!Hull(Out(from), In(to), tolerance).Contains(world.X, world.Y)) continue;
            var (a, b, c, d) = Curve(Out(from), In(to));
            // Sample densely enough that neighbouring points stay a few screen pixels apart on long or zoomed-in curves.
            var steps = Math.Clamp((int)((Vector2.Distance(a, b) + Vector2.Distance(b, c) + Vector2.Distance(c, d)) * V.K / 6), 40, 2000);
            for (var i = 0; i <= steps; i++)
            {
                var t = i / (float)steps;
                var u = 1 - t;
                var point = u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
                if (Vector2.Distance(point, p) < tolerance) return connection;
            }
        }
        return null;
    }

    // ---------- Drawing ----------

    private static CanvasGeometry Bezier(ICanvasResourceCreator creator, Point from, Point to)
    {
        var (a, b, c, d) = Curve(from, to);
        using var builder = new CanvasPathBuilder(creator);
        builder.BeginFigure(a);
        builder.AddCubicBezier(b, c, d);
        builder.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(builder);
    }

    private void Backdrop_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var spacing = 24 * V.K;
        while (spacing < 18) spacing *= 2;
        var dot = Alpha(_ink, 30);
        var (ox, oy) = (((V.X % spacing) + spacing) % spacing, ((V.Y % spacing) + spacing) % spacing);
        for (var x = ox; x < sender.ActualWidth; x += spacing)
        for (var y = oy; y < sender.ActualHeight; y += spacing)
            ds.FillCircle((float)x, (float)y, 1.1f, dot);

        ds.Transform = Matrix3x2.CreateScale((float)V.K) * Matrix3x2.CreateTranslation((float)V.X, (float)V.Y);
        DrawCardShadows(sender, ds);
        var stroke = (float)(1.6 / V.K);
        // Each link runs from its source's kind colour to its target's, so the flow of images, text and video reads at a glance.
        var colors = new Dictionary<NodeKind, Color>();
        Color KindColor(NodeKind kind) => colors.TryGetValue(kind, out var c) ? c : colors[kind] = Alpha(Palette.Kind(kind, ActualTheme), 190);
        var visible = new Rect(-V.X / V.K, -V.Y / V.K, (sender.ActualWidth - V.X) / V.K, (sender.ActualHeight - V.Y) / V.K);
        // A batch's shared inputs end at its lead, so selecting any member lights them up too.
        var selectedStacks = Doc.Nodes.Where(n => _selected.Contains(n.Id)).Select(Stacks.Of).OfType<string>().ToHashSet();
        foreach (var (connection, from, to) in Links())
        {
            if (!visible.Intersects(Hull(Out(from), In(to), stroke * 2))) continue;
            var highlighted = connection.Id == _selectedConnection || _selected.Contains(from.Id) || _selected.Contains(to.Id) || Stacks.Of(to) is { } stack && selectedStacks.Contains(stack);
            using var geometry = Bezier(sender, Out(from), In(to));
            if (highlighted) ds.DrawGeometry(geometry, _accent, stroke * 1.5f);
            else
            {
                var (start, end) = (Out(from), In(to));
                using var brush = new CanvasLinearGradientBrush(sender, KindColor(from.Kind), KindColor(to.Kind))
                {
                    StartPoint = new Vector2((float)start.X, (float)start.Y),
                    EndPoint = new Vector2((float)end.X, (float)end.Y),
                };
                ds.DrawGeometry(geometry, brush, stroke);
            }
        }
    }

    /// <summary>
    /// Soft shadow under every visible card: all card shapes go into one command list that is blurred once per frame,
    /// which stays cheap on large canvases compared with per-node XAML shadows.
    /// </summary>
    private void DrawCardShadows(CanvasControl sender, CanvasDrawingSession ds)
    {
        using var shapes = new CanvasCommandList(sender);
        using (var session = shapes.CreateDrawingSession())
        {
            var color = Color.FromArgb(ActualTheme == ElementTheme.Dark ? (byte)120 : (byte)34, 0, 0, 0);
            foreach (var node in _views.Values.Select(v => v.Node).Where(n => n.Kind != NodeKind.Group))
                session.FillRoundedRectangle(new Windows.Foundation.Rect(node.X + 2, node.Y + 6, Math.Max(0, node.Width - 4), Math.Max(0, node.Height - 2)), 8, 8, color);
        }
        using var blur = new GaussianBlurEffect { Source = shapes, BlurAmount = 12, BorderMode = EffectBorderMode.Soft };
        ds.DrawImage(blur);
    }

    private void Overlay_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        if (_gesture == Gesture.Box)
        {
            var a = ToScreen(_startWorld.X, _startWorld.Y);
            var b = ToScreen(_currentWorld.X, _currentWorld.Y);
            var rect = new Windows.Foundation.Rect(a, b);
            ds.FillRoundedRectangle(rect, 4, 4, Alpha(_accent, 28));
            ds.DrawRoundedRectangle(rect, 4, 4, _accent, 1);
        }
        if (_gesture == Gesture.Connect && _active is not null)
        {
            ds.Transform = Matrix3x2.CreateScale((float)V.K) * Matrix3x2.CreateTranslation((float)V.X, (float)V.Y);
            var from = _connectFromSource ? Out(_active) : _currentWorld;
            var to = _connectFromSource ? _currentWorld : In(_active);
            using var geometry = Bezier(sender, from, to);
            using var style = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawGeometry(geometry, _accent, (float)(2 / V.K), style);
        }
    }

    // ---------- Pointer ----------

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        var (view, part) = HitTest(e.OriginalSource);
        if (part == "panel") return;
        StopGlide();
        Root.Focus(FocusState.Pointer);
        _pressed = null;
        _startScreen = point.Position;
        _startWorld = _currentWorld = ToWorld(point.Position);
        _moved = false;
        var props = point.Properties;
        var additive = Down(VirtualKey.Control) || Down(VirtualKey.Shift);

        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && _space))
        {
            _gesture = Gesture.Pan;
            _startViewport = V;
        }
        else if (!props.IsLeftButtonPressed) return;
        else if (view is not null && part is "source" or "target")
        {
            _gesture = Gesture.Connect;
            _active = view.Node;
            _connectFromSource = part == "source";
        }
        else if (view is not null && part == "resize")
        {
            _gesture = Gesture.Resize;
            _active = view.Node;
            _resizeStart = (view.Node.Width, view.Node.Height);
        }
        else if ((view is null || view.Node.Kind == NodeKind.Group) && ConnectionAt(_startWorld) is { } connection)
        {
            // A group's blank area counts as canvas, so connections running across it stay clickable.
            Select([]);
            _selectedConnection = connection.Id;
            Backdrop.Invalidate();
            return;
        }
        else if (view is not null && !(additive && view.Node.Kind == NodeKind.Group))
        {
            var id = view.Node.Id;
            _selectedConnection = null;
            if (additive)
            {
                if (!_selected.Remove(id)) _selected.Add(id);
                Select(_selected.ToList());
                // Deselecting is just a click: no drag, and not Handled so the handledEventsToo handler does not reselect it.
                if (!_selected.Contains(id)) return;
            }
            else if (!_selected.Contains(id)) Select([id]);
            _pressed = additive ? null : view.Node;
            StartDrag();
        }
        else if (additive)
        {
            // Shift / Ctrl + drag on empty canvas or inside a group draws a selection box that adds to the selection.
            _gesture = Gesture.Box;
            _active = view?.Node;
            _selectedConnection = null;
            _boxInitial = [.. _selected];
        }
        else
        {
            // Plain drag on empty canvas pans it; a click without movement just clears the selection.
            _gesture = Gesture.Pan;
            _startViewport = V;
            _selectedConnection = null;
            Select([]);
        }
        if (_gesture == Gesture.Pan) ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void StartDrag()
    {
        // Moving a group moves its members with it.
        // Moving a collapsed stack's lead moves the cards under it too.
        var moving = WithStacks(_selected);
        var groups = _selected.Where(id => Doc.Find(id)?.Kind == NodeKind.Group).ToHashSet();
        _dragStart = Doc.Nodes.Where(n => moving.Contains(n.Id) || (n.GroupId is not null && groups.Contains(n.GroupId))).Select(n => (n, n.X, n.Y)).ToList();
        _gesture = Gesture.Drag;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture == Gesture.None) return;
        var position = e.GetCurrentPoint(Root).Position;
        _currentWorld = ToWorld(position);
        var (dx, dy) = (_currentWorld.X - _startWorld.X, _currentWorld.Y - _startWorld.Y);
        if (!_moved && Math.Abs(position.X - _startScreen.X) + Math.Abs(position.Y - _startScreen.Y) < 3) return;
        if (!_moved && _gesture is Gesture.Drag or Gesture.Resize) History.Record();
        _moved = true;
        switch (_gesture)
        {
            case Gesture.Pan:
                V = new(_startViewport.X + position.X - _startScreen.X, _startViewport.Y + position.Y - _startScreen.Y, _startViewport.K);
                break;
            case Gesture.Drag:
                foreach (var (node, x, y) in _dragStart)
                {
                    node.X = x + dx;
                    node.Y = y + dy;
                }
                break;
            case Gesture.Resize when _active is { } node:
                var width = Math.Max(80, _resizeStart.W + dx);
                var keepRatio = node.Kind is NodeKind.Image or NodeKind.Video && !node.FreeResize && !Down(VirtualKey.Shift);
                node.Width = width;
                node.Height = keepRatio ? width * _resizeStart.H / _resizeStart.W : Math.Max(60, _resizeStart.H + dy);
                break;
            case Gesture.Box:
                var box = new Rect(Math.Min(_startWorld.X, _currentWorld.X), Math.Min(_startWorld.Y, _currentWorld.Y), Math.Max(_startWorld.X, _currentWorld.X), Math.Max(_startWorld.Y, _currentWorld.Y));
                var hiddenCards = Stacks.Hidden(Doc);
                Select(_boxInitial.Concat(Doc.Nodes.Where(n => !hiddenCards.Contains(n) && (n.Kind != NodeKind.Group ? box.Intersects(Rect.Of(n)) : box.Contains(n.X, n.Y) && box.Contains(n.Right, n.Bottom))).Select(n => n.Id)));
                Overlay.Invalidate();
                break;
            case Gesture.Connect:
                Overlay.Invalidate();
                break;
        }
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture == Gesture.None) return;
        var gesture = _gesture;
        _gesture = Gesture.None;
        ProtectedCursor = null;
        Root.ReleasePointerCapture(e.Pointer);
        if (gesture == Gesture.Connect && _active is { } active)
        {
            // A group's blank area takes the link only when the group can feed the node; otherwise it acts as empty canvas.
            if (NodeAt(_currentWorld, active) is { } target && (target.Kind != NodeKind.Group || CanvasGraph.NormalizeConnection(active, target, _connectFromSource) is not null))
                Connect(active, target, _connectFromSource);
            else if (_moved) ShowCreateMenu(e.GetCurrentPoint(Root).Position, (active, _connectFromSource));
        }
        // Ctrl / Shift + click on a group without dragging toggles it like any other node.
        if (gesture == Gesture.Box && !_moved && _active is { } group) Select(_selected.Contains(group.Id) ? _selected.Where(id => id != group.Id) : _selected.Append(group.Id));
        if (gesture == Gesture.Drag && _moved) UpdateGroupMembership();
        // A plain click on a collapsed batch opens it.
        if (gesture == Gesture.Drag && !_moved && _pressed is { } clicked && Stacks.Of(clicked) is not null && !Stacks.IsOpen(clicked))
            DispatcherQueue.TryEnqueue(() => ToggleStack(clicked));
        _pressed = null;
        // Resizing a group takes in the free nodes now inside it and lets go of members left outside.
        if (gesture == Gesture.Resize && _moved && _active is { Kind: NodeKind.Group } resized)
            foreach (var node in Doc.Nodes.Where(n => n.Kind != NodeKind.Group && (n.GroupId is null || n.GroupId == resized.Id)))
                node.GroupId = Rect.Of(resized).Contains(node.X + node.Width / 2, node.Y + node.Height / 2) ? resized.Id
                    : node.GroupId == resized.Id ? CanvasGraph.ContainingGroup(Doc, node)?.Id : null;
        _active = null;
        Overlay.Invalidate();
        Backdrop.Invalidate();
        UpdatePanel();
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture == Gesture.None) return;
        _gesture = Gesture.None;
        ProtectedCursor = null;
        _active = null;
        Overlay.Invalidate();
        UpdatePanel();
    }

    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (HitTest(e.OriginalSource).Part == "panel") return;
        StopGlide();
        var point = e.GetCurrentPoint(Root);
        var delta = point.Properties.MouseWheelDelta;
        // Touchpad: two-finger scroll pans and pinch (reported with Ctrl) zooms. Mouse wheel zooms, Shift+wheel scrolls sideways.
        var touchpad = e.Pointer.PointerDeviceType == PointerDeviceType.Touchpad && !Down(VirtualKey.Control);
        if (point.Properties.IsHorizontalMouseWheel) V = V with { X = V.X - delta };
        else if (touchpad) V = V with { Y = V.Y + delta };
        else if (Down(VirtualKey.Shift)) V = V with { X = V.X + delta };
        else ZoomAt(point.Position, V.K * Math.Pow(1.1, delta / 120.0));
        e.Handled = true;
    }

    private void UpdateGroupMembership()
    {
        foreach (var (node, _, _) in _dragStart.Where(d => d.Node.Kind != NodeKind.Group))
        {
            if (node.GroupId is not null && _selected.Contains(node.GroupId)) continue;
            node.GroupId = CanvasGraph.ContainingGroup(Doc, node)?.Id;
        }
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var (view, part) = HitTest(e.OriginalSource);
        if (part == "panel") return;
        // A node still generating gets no import: the result would silently replace the imported file.
        if (view?.Node is { Kind: NodeKind.Image or NodeKind.Video or NodeKind.Audio, HasContent: false, Status: not NodeStatus.Loading } node) _ = ImportIntoAsync(node);
        else if (view?.Node is { Kind: NodeKind.Image, HasContent: true } picture && part is null && Environment.TickCount64 - _stackToggled > 600) ShowPreview(picture);
        else if (view is null || view.Node.Kind == NodeKind.Group) ShowCreateMenu(e.GetPosition(Root), null);
    }

    // ---------- Editing ----------

    public void Connect(CanvasNode first, CanvasNode second, bool firstIsSource)
    {
        if (CanvasGraph.NormalizeConnection(first, second, firstIsSource) is not { } pair) return;
        var (from, to) = pair;
        if (Doc.Connections.Any(c => c.From == from && c.To == to)) return;
        History.Record();
        Doc.Connections.Add(CanvasConnection.Create(from, to));
    }

    public CanvasNode AddNode(NodeKind kind, Point world, (CanvasNode Node, bool FromSource)? link = null)
    {
        History.Record();
        var node = CanvasNode.Create(kind, world.X, world.Y);
        if (link is { } l)
        {
            node.X = l.FromSource ? world.X : world.X - node.Width;
            node.Y = world.Y - node.Height / 2;
        }
        else node.X = CanvasGraph.FreeX(node, Doc.Nodes);
        node.GroupId = CanvasGraph.ContainingGroup(Doc, node)?.Id;
        Doc.Nodes.Add(node);
        if (link is { } connectTo && CanvasGraph.NormalizeConnection(connectTo.Node, node, connectTo.FromSource) is { } pair)
            Doc.Connections.Add(CanvasConnection.Create(pair.From, pair.To));
        Select([node.Id]);
        EnsureVisible(node);
        return node;
    }

    /// <summary>Pan just enough to bring a node (and the panel under it) into view.</summary>
    public void EnsureVisible(CanvasNode node) => EnsureVisible(Rect.Of(node));

    /// <summary>Select a node and glide it to the middle of the free area without zooming; a card hidden in a collapsed stack is found through its lead.</summary>
    public void Locate(CanvasNode node)
    {
        if (!Doc.Nodes.Contains(node)) return;
        if (Stacks.Hidden(Doc).Contains(node)) node = Stacks.Members(Doc, Stacks.Of(node)!)[0];
        Select([node.Id]);
        var middle = ToScreen(node.X + node.Width / 2, node.Y + node.Height / 2);
        Glide(V with { X = V.X + Center.X - middle.X, Y = V.Y + Center.Y - middle.Y });
    }

    /// <summary>Pan (never zoom) just enough to bring an area and the panel under it into view, clear of the safe area; content larger than the view keeps its top-left visible.</summary>
    public void EnsureVisible(Rect bounds)
    {
        var (topLeft, bottomRight) = (ToScreen(bounds.Left, bounds.Top), ToScreen(bounds.Right, bounds.Bottom));
        const double margin = 80, panelSpace = 220;
        var (minX, minY) = (Math.Max(margin, _safe.Left + 40), _safe.Top + 12);
        var dx = Math.Min(0, Root.ActualWidth - margin - bottomRight.X);
        if (topLeft.X + dx < minX) dx = minX - topLeft.X;
        var dy = topLeft.Y < minY ? minY - topLeft.Y : bottomRight.Y > Root.ActualHeight - panelSpace ? Math.Max(minY - topLeft.Y, Root.ActualHeight - panelSpace - bottomRight.Y) : 0;
        if (dx != 0 || dy != 0) Glide(V with { X = V.X + dx, Y = V.Y + dy });
    }

    /// <summary>Toolbar creation also ends any text editing, so canvas shortcuts act on the new node.</summary>
    public void AddNodeAtCenter(NodeKind kind)
    {
        FocusCanvas();
        AddNode(kind, ViewCenterWorld);
    }

    public void DeleteSelection()
    {
        if (_selectedConnection is { } connectionId)
        {
            History.Record();
            // One line on screen can stand for the same input of every member in a batch; deleting it removes them all.
            var ends = LinkEnds();
            if (Doc.Connections.FirstOrDefault(c => c.Id == connectionId) is { } connection && ends(connection) is { } line)
                foreach (var same in Doc.Connections.Where(c => ends(c) is { } other && other == line).ToList()) Doc.Connections.Remove(same);
            _selectedConnection = null;
            return;
        }
        if (!Doc.Nodes.Any(n => _selected.Contains(n.Id))) return;
        History.Record();
        DeleteNodes(WithStacks(_selected));
    }

    /// <summary>Remove nodes with their connections and group membership, stopping their generations; the caller records history. A collapsed stack's lead takes its whole stack along unless <paramref name="wholeStacks"/> is false.</summary>
    public void DeleteNodes(IReadOnlyCollection<string> nodeIds, bool wholeStacks = true)
    {
        if (wholeStacks) nodeIds = WithStacks(nodeIds);
        var removed = Doc.Nodes.Where(n => nodeIds.Contains(n.Id)).ToList();
        if (removed.Count == 0) return;
        Batch(() =>
        {
            var ids = removed.Select(n => n.Id).ToHashSet();
            foreach (var node in removed)
            {
                Generator.Cancel(node.Id);
                Doc.Nodes.Remove(node);
            }
            foreach (var member in Doc.Nodes.Where(n => n.GroupId is not null && ids.Contains(n.GroupId))) member.GroupId = null;
            foreach (var connection in Doc.Connections.Where(c => ids.Contains(c.From) || ids.Contains(c.To)).ToList()) Doc.Connections.Remove(connection);
        });
        Select(_selected.Where(id => !nodeIds.Contains(id)).ToList());
    }

    /// <summary>Recycled files come back through the recycle bin, never by reviving JSON references; one pass over the history for all of them.</summary>
    public void ForgetAssets(IEnumerable<string> names)
    {
        var files = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        History.ForgetAssets(files);
        DeleteNodes(Doc.Nodes.Where(n => n.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio && n.Content is { } asset && files.Contains(asset)).Select(n => n.Id).ToList(), wholeStacks: false);
        RaiseChanged();
    }

    public void Duplicate()
    {
        var sources = Doc.Nodes.Where(n => _selected.Contains(n.Id) && n.Kind != NodeKind.Group).ToList();
        if (sources.Count == 0) return;
        History.Record();
        var copies = new List<string>();
        Batch(() =>
        {
            foreach (var source in sources)
            {
                var copy = source.Clone(newId: true);
                copy.X += 40;
                copy.Y += 40;
                // A copy is a card of its own, never another member of the batch it came from.
                copy.SetOption(NodeKeys.Stack, null);
                copy.SetOption(NodeKeys.StackOpen, null);
                copy.GroupId = CanvasGraph.ContainingGroup(Doc, copy)?.Id;
                copy.Status = copy.HasContent ? NodeStatus.Success : NodeStatus.Idle;
                copy.TaskId = null;
                Doc.Nodes.Add(copy);
                copies.Add(copy.Id);
            }
        });
        Select(copies);
    }

    public void Group()
    {
        if (!CanGroup(_selected)) return;
        History.Record();
        if (GroupNodes(_selected.ToList(), null) is { } group) Select([group.Id]);
    }

    /// <summary>Whether grouping these nodes would change anything: at least two members, not already all in one group.</summary>
    private bool CanGroup(IReadOnlyCollection<string> ids)
    {
        var members = GroupCandidates(ids);
        return members.Count >= 2 && !members.All(n => n.GroupId is not null && n.GroupId == members[0].GroupId);
    }

    // Groups among the ids are expanded into their members and merged into the new group; groups never nest.
    private List<CanvasNode> GroupCandidates(IReadOnlyCollection<string> picked)
    {
        var ids = WithStacks(picked);
        var groups = Doc.Nodes.Where(n => ids.Contains(n.Id) && n.Kind == NodeKind.Group).Select(n => n.Id).ToHashSet();
        return Doc.Nodes.Where(n => n.Kind != NodeKind.Group && (ids.Contains(n.Id) || (n.GroupId is not null && groups.Contains(n.GroupId)))).ToList();
    }

    /// <summary>Put nodes (and members of groups among them) into one new group; the caller records history. Null when nothing changes.</summary>
    public CanvasNode? GroupNodes(IReadOnlyCollection<string> ids, string? title)
    {
        if (!CanGroup(ids)) return null;
        var members = GroupCandidates(ids);
        var old = members.Select(n => n.GroupId).OfType<string>().Concat(Doc.Nodes.Where(n => ids.Contains(n.Id) && n.Kind == NodeKind.Group).Select(n => n.Id)).ToHashSet();
        var rect = CanvasGraph.GroupRect(members);
        var group = new CanvasNode { Kind = NodeKind.Group, Title = string.IsNullOrWhiteSpace(title) ? "分组" : title, X = rect.Left, Y = rect.Top, Width = rect.Width, Height = rect.Height };
        Batch(() =>
        {
            Doc.Nodes.Insert(0, group);
            foreach (var member in members) member.GroupId = group.Id;
            RemoveEmptyGroups(old);
        });
        return group;
    }

    /// <summary>Move nodes (collapsed stacks whole) into an existing group, which keeps its ID, title and connections; the caller records history.</summary>
    public List<string> AddToGroupNodes(CanvasNode group, IReadOnlyCollection<string> ids, bool fit) => Regroup(ids, group.Id, fit);

    /// <summary>Take nodes (collapsed stacks whole) out of their groups; the caller records history.</summary>
    public List<string> RemoveFromGroupNodes(IReadOnlyCollection<string> ids, bool fit) => Regroup(ids, null, fit);

    private List<string> Regroup(IReadOnlyCollection<string> ids, string? groupId, bool fit)
    {
        var moved = new List<string>();
        Batch(() =>
        {
            var (nodes, left) = CanvasGraph.MoveToGroup(Doc, WithStacks(ids), groupId);
            moved = nodes.Select(n => n.Id).ToList();
            // Before the empty ones go: fitting skips groups without members, and RemoveEmptyGroups trims this set.
            if (fit) foreach (var id in left.Append(groupId).OfType<string>()) CanvasGraph.FitGroup(Doc, id);
            RemoveEmptyGroups(left);
        });
        return moved;
    }

    public void Ungroup()
    {
        if (!Doc.Nodes.Any(n => _selected.Contains(n.Id) && (n.Kind == NodeKind.Group || n.GroupId is not null))) return;
        History.Record();
        Select(UngroupNodes(_selected.ToList()).Select(n => n.Id));
    }

    /// <summary>Release the members of groups among the ids, and the ids that sit in a group; empty groups go away. The caller records history.</summary>
    public List<CanvasNode> UngroupNodes(IReadOnlyCollection<string> ids)
    {
        ids = WithStacks(ids);
        var groups = Doc.Nodes.Where(n => ids.Contains(n.Id) && n.Kind == NodeKind.Group).Select(n => n.Id).ToHashSet();
        var released = Doc.Nodes.Where(n => n.GroupId is not null && (groups.Contains(n.GroupId) || ids.Contains(n.Id))).ToList();
        var old = released.Select(n => n.GroupId!).Concat(groups).ToHashSet();
        Batch(() =>
        {
            foreach (var node in released) node.GroupId = null;
            RemoveEmptyGroups(old);
        });
        return released;
    }

    /// <summary>Delete the groups in <paramref name="ids"/> that have no members left, together with their connections.</summary>
    private void RemoveEmptyGroups(HashSet<string> ids)
    {
        ids.RemoveWhere(id => Doc.Nodes.Any(n => n.GroupId == id));
        foreach (var group in Doc.Nodes.Where(n => ids.Contains(n.Id)).ToList()) Doc.Nodes.Remove(group);
        foreach (var connection in Doc.Connections.Where(c => ids.Contains(c.From) || ids.Contains(c.To)).ToList()) Doc.Connections.Remove(connection);
    }

    // Not mid-gesture: a drag or resize would keep moving the node instances the restore just replaced.
    public void Undo() { if (History.CanUndo && _gesture == Gesture.None) Restore(() => History.Undo()); }
    public void Redo() { if (History.CanRedo && _gesture == Gesture.None) Restore(() => History.Redo()); }

    /// <summary>Undo / redo put back earlier nodes: no arrival animations for them.</summary>
    private void Restore(Action action)
    {
        _restoring = true;
        try { Batch(action); }
        finally { _restoring = false; }
    }

    /// <summary>Apply many collection edits with one refresh instead of one per item.</summary>
    private void Batch(Action action)
    {
        _suspend = true;
        try { action(); }
        finally { _suspend = false; }
        SyncTracking();
    }

    // ---------- Menus ----------

    internal static MenuFlyoutItem Item(string text, string glyph, Action action, string? shortcut = null) =>
        Item(text, glyph, () => { action(); return Task.CompletedTask; }, shortcut);

    /// <summary>Menu entry whose action is awaited; a failure is logged and shown instead of crashing or vanishing.</summary>
    internal static MenuFlyoutItem Item(string text, string glyph, Func<Task> action, string? shortcut = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (shortcut is not null) item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex)
            {
                App.Log(ex);
                _ = Dialogs.MessageAsync("操作失败", ex.Message);
            }
        };
        return item;
    }

    private static readonly (NodeKind Kind, string Label)[] CreatableKinds =
        [(NodeKind.Text, "文本"), (NodeKind.Image, "图片"), (NodeKind.Video, "视频"), (NodeKind.Audio, "音频"), (NodeKind.Config, "生成配置")];

    private void ShowCreateMenu(Point screen, (CanvasNode Node, bool FromSource)? link)
    {
        var menu = new MenuFlyout();
        var world = ToWorld(screen);
        foreach (var (kind, label) in CreatableKinds) menu.Items.Add(Item($"新建{label}节点", NodeView.Glyph(kind), () => AddNode(kind, world, link)));
        if (link is null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("导入文件…", "", () => ImportWithPickerAsync(world)));
            menu.Items.Add(Item("粘贴", "", () => PasteAsync(world), "Ctrl+V"));
            menu.Items.Add(Item("显示全部内容", "", FitToContent, "Ctrl+1"));
        }
        menu.ShowAt(Root, new FlyoutShowOptions { Position = screen });
    }

    private void Root_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (e.Handled) return;
        var (view, part) = HitTest(e.OriginalSource);
        if (part == "panel" || e.OriginalSource is TextBox) return;
        var keyboard = !e.TryGetPosition(Root, out var position);
        var node = view?.Node ?? (keyboard ? _order.Select(Doc.Find).LastOrDefault(n => n is not null) : null);
        if (keyboard) position = node is null ? Center : ToScreen(node.X + node.Width / 2, node.Y + node.Height / 2);
        e.Handled = true;
        var world = ToWorld(position);
        if (!keyboard && (node is null || node.Kind == NodeKind.Group) && ConnectionAt(world) is { } connection)
        {
            Select([]);
            _selectedConnection = connection.Id;
            Backdrop.Invalidate();
            var connectionMenu = new MenuFlyout();
            var ends = LinkEnds();
            var line = ends(connection);
            var count = Doc.Connections.Count(c => line is not null && ends(c) == line);
            connectionMenu.Items.Add(Item(count > 1 ? $"删除这一批的输入（{count} 条连线）" : "删除连线", "", DeleteSelection, "Delete"));
            connectionMenu.ShowAt(Root, new FlyoutShowOptions { Position = position });
            return;
        }
        if (node is null)
        {
            ShowCreateMenu(position, null);
            return;
        }
        if (!_selected.Contains(node.Id)) Select([node.Id]);
        var menu = new MenuFlyout();
        if (CanvasGenerator.CanGenerate(node))
            menu.Items.Add(Generator.IsRunning(node.Id) ? Item("停止生成", "", () => Generator.Cancel(node.Id)) : Item(node.Status == NodeStatus.Error && !node.HasContent ? "重试" : "生成", "", () => Generator.GenerateAsync(node)));
        if (Stacks.Of(node) is { } stack && Stacks.Members(Doc, stack).Count > 1)
        {
            menu.Items.Add(Item(Stacks.IsOpen(node) ? "收起这一批" : "展开这一批", Stacks.IsOpen(node) ? "\uE73F" : "\uE740", () => ToggleStack(node)));
            menu.Items.Add(Item("留这张，删掉这一摞的其他", "", () => TrackLocalAsync(() => KeepOneAsync(node))));
        }
        menu.Items.Add(Item("重命名", "", () => RenameAsync(node)));
        menu.Items.Add(Item("复制节点", "", Duplicate, "Ctrl+D"));
        if (ComposeInputs() is { Count: >= 2 } composable) menu.Items.Add(Item("拼版…", "", () => TrackLocalAsync(() => ImageTools.ComposeAsync(this, composable))));
        if (node.Kind == NodeKind.Image && node.Option(NodeKeys.Compose).Length > 0) menu.Items.Add(Item("按上次设置重新拼", "", () => TrackLocalAsync(() => ImageTools.RecomposeAsync(this, node))));
        if (_selected.Count > 1) menu.Items.Add(Item("编组", "", Group, "Ctrl+G"));
        if (node.Kind == NodeKind.Group || node.GroupId is not null) menu.Items.Add(Item("取消编组", "", Ungroup, "Ctrl+Shift+G"));
        if (node.Kind is NodeKind.Image or NodeKind.Video)
        {
            var free = new ToggleMenuFlyoutItem { Text = "自由缩放（不锁定比例）", IsChecked = node.FreeResize };
            free.Click += (_, _) =>
            {
                History.Record();
                node.FreeResize = free.IsChecked;
                // Locking the ratio again snaps the node back to the media's own aspect.
                if (!node.FreeResize && node.NaturalWidth > 0) node.Height = Math.Round(node.Width * node.NaturalHeight / node.NaturalWidth);
            };
            menu.Items.Add(free);
        }
        if (node.HasContent && node.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio)
        {
            var path = ProjectStore.AssetPath(Doc.Id, node.Content!);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("用默认应用打开", "", () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })));
            menu.Items.Add(Item("在资源管理器中显示", "", () => Process.Start("explorer.exe", $"/select,\"{path}\"")));
            if (node.Kind == NodeKind.Image && MarkSupport(node) is var (mask, points, markup) && (mask || points || markup))
                menu.Items.Insert(0, Item(mask && markup ? "局部编辑（涂抹 / 标注）…" : mask ? "局部重绘（涂抹蒙版）…" : "局部编辑（点选 / 框选）…", "", () => EditMarksAsync(node)));
            var bitmap = node.Kind == NodeKind.Image && !Media.IsSvg(node.Content);
            if (bitmap) menu.Items.Add(Item("本地放大…", "", () => TrackLocalAsync(() => UpscaleAsync(node))));
            if (bitmap)
            {
                menu.Items.Add(Item("裁剪…", "\uE7A8", () => TrackLocalAsync(() => ImageTools.CropAsync(this, node))));
                menu.Items.Add(Item("拆分…", "\uF0E2", () => TrackLocalAsync(() => ImageTools.SplitAsync(this, node))));
                menu.Items.Add(Item("多角度…", "\uE89E", () => TrackLocalAsync(() => ImageTools.AngleAsync(this, node))));
                menu.Items.Add(Item("反推提示词", "\uE8FD", () => TrackLocalAsync(() => ImageTools.ReversePromptAsync(this, node))));
            }
            menu.Items.Add(Item("另存为…", "", () => SaveAsAsync(node, path)));
            menu.Items.Add(Item("加入资料库", "", () => AddToLibraryAsync(node)));
            if (node.Kind == NodeKind.Image) menu.Items.Add(Item("设为定稿…", "", () => TrackLocalAsync(() => PromoteAsync(node))));
        }
        if (node.Kind == NodeKind.Text && node.HasContent)
            menu.Items.Add(Item("复制文本", "", () => { var data = new DataPackage(); data.SetText(node.Content); Clipboard.SetContent(data); }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("删除", "", DeleteSelection, "Delete"));
        menu.ShowAt(Root, new FlyoutShowOptions { Position = position });
    }

    /// <summary>Keep this card of a batch and remove the others from the canvas (their files stay), after the usual confirmation. One undo step.</summary>
    private async Task KeepOneAsync(CanvasNode node)
    {
        if (Stacks.Of(node) is not { } stack || Stacks.Members(Doc, stack).Count < 2) return;
        var others = Stacks.Members(Doc, stack).Count - 1;
        if (!await Dialogs.ConfirmAsync("留这张，删掉其余？", $"将从画布移除这一批的另外 {others} 个节点，它们的文件不会被删除，仍能在资料库的「画布文件」里找到。可以撤销。", "删掉其余", destructive: true)) return;
        // The canvas may have changed while the dialog was open.
        if (Doc.Find(node.Id) != node || Stacks.Of(node) is null) return;
        History.Record();
        var removed = new List<string>();
        Batch(() => removed = Picks.KeepOne(Doc, node.Id));
        DeleteNodes(removed, wholeStacks: false);
        Select([node.Id]);
    }

    /// <summary>
    /// Let this picture take the place of another (finalise it): choose the picture to replace, optionally replace the library file of the same name,
    /// and the canvas change is one undo step. The old node leaves the canvas; no asset file is deleted.
    /// </summary>
    private async Task PromoteAsync(CanvasNode node)
    {
        bool Single(CanvasNode n) => Stacks.Of(n) is not { } s || Stacks.Members(Doc, s).Count < 2;
        var others = Doc.Nodes.Where(n => n != node && n.Kind == NodeKind.Image && n.HasContent && Single(n)).ToList();
        if (others.Count == 0)
        {
            await Dialogs.MessageAsync("没有可以替换的图片", "画布上没有其他可以被取代的图片。一批里的图片请先用「留这张」整理。");
            return;
        }
        bool Named(CanvasNode n) => n.GroupId is { } group && Doc.Find(group) is { } g && (g.Title.Contains("定稿") || g.Title.Contains("官方"));
        // The picture selected beside this one first, then the ones in 定稿 / 官方 groups; with neither, every other picture.
        var candidates = others.Where(n => _selected.Contains(n.Id)).Concat(others.Where(n => !_selected.Contains(n.Id) && Named(n))).ToList();
        var listed = candidates.Count > 0;
        if (!listed) candidates = others;
        string Label(CanvasNode n) => Doc.Find(n.GroupId ?? "") is { } g ? $"{n.Title} · 分组「{g.Title}」" : n.Title;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, ItemsSource = candidates.Select(Label).ToList(), MaxHeight = 300, MinWidth = 380, SelectedIndex = 0 };
        AutomationProperties.SetName(list, "被取代的图片");
        var library = new CheckBox { IsChecked = true, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(library, "替换资料库里同名的文件");
        void ShowLibrary()
        {
            string? file = null;
            try { file = list.SelectedIndex >= 0 && LibraryStore.Find(Doc.Id, candidates[list.SelectedIndex].Title) is { } item ? Path.GetFileName(item.Path) : null; }
            catch (LibraryException) { /* An ambiguous name: no library replacement is offered. */ }
            library.Visibility = file is null ? Visibility.Collapsed : Visibility.Visible;
            library.Content = file is null ? "" : $"资料库里同名的「{file}」用新图替换，旧文件移到回收站（不能撤销）";
        }
        list.SelectionChanged += (_, _) => ShowLibrary();
        ShowLibrary();
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(new TextBlock
        {
            Text = listed
                ? $"选择要被「{node.Title}」取代的图片。它会从画布上删除，新图占它的位置、标题、分组和连线；文件不会被删除。"
                : $"选择要被「{node.Title}」取代的图片（没有选中的，也没有「定稿 / 官方」分组里的图片，所以列出了全部）。它会从画布上删除，新图占它的位置、标题、分组和连线；文件不会被删除。",
            TextWrapping = TextWrapping.Wrap,
        });
        form.Children.Add(list);
        form.Children.Add(library);
        if (!await Dialogs.FormAsync("设为定稿", form, "设为定稿") || list.SelectedIndex < 0) return;
        var target = candidates[list.SelectedIndex];
        if (Doc.Find(node.Id) != node || Doc.Find(target.Id) != target) return;
        try { Picks.CheckPromote(Doc, node.Id, target.Id); }
        catch (ArgumentException ex)
        {
            await Dialogs.MessageAsync("不能设为定稿", ex.Message);
            return;
        }
        if (library.Visibility == Visibility.Visible && library.IsChecked == true)
        {
            try { LibraryStore.ReplaceByTitle(Doc.Id, target.Title, ProjectStore.AssetPath(Doc.Id, node.Content!)); }
            catch (LibraryException ex)
            {
                await Dialogs.MessageAsync("资料库里的文件没能替换", ex.Message + "\n\n画布没有改动。");
                return;
            }
        }
        History.Record();
        var removed = new List<string>();
        Batch(() => removed = Picks.Promote(Doc, node.Id, target.Id));
        DeleteNodes(removed, wholeStacks: false);
        Select([node.Id]);
        EnsureVisible(Rect.Of(node));
    }

    /// <summary>Pick a local Real-ESRGAN model and factor, then upscale into a new node beside this one.</summary>
    private async Task UpscaleAsync(CanvasNode node)
    {
        var models = Upscaler.Models();
        if (models.Count == 0)
        {
            await Dialogs.MessageAsync("没有找到本地放大工具", $"请把 Real-ESRGAN-ncnn-vulkan（realesrgan-ncnn-vulkan.exe 与 models 文件夹）放到：\n{Upscaler.Dir}");
            return;
        }
        var model = new ComboBox { Header = "模型", ItemsSource = models.Select(m => m.Label).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var factor = new ComboBox { Header = "倍数", ItemsSource = new List<string> { "2×", "4×" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var size = new TextBlock { Style = (Style)Application.Current.Resources["SecondaryCaption"], TextWrapping = TextWrapping.Wrap };
        void ShowSize()
        {
            var scale = factor.SelectedIndex == 0 ? 2 : 4;
            size.Text = (node.NaturalWidth > 0 ? $"输出 {node.NaturalWidth * scale}×{node.NaturalHeight * scale}。" : "") + "在本机显卡上运行，图片不会上传，结果放在右侧新节点。";
        }
        factor.SelectionChanged += (_, _) => ShowSize();
        ShowSize();
        var form = new StackPanel { Spacing = 12, MinWidth = 360, Children = { model, factor, size } };
        if (!await Dialogs.FormAsync("本地放大", form, "放大")) return;
        _ = Generator.UpscaleAsync(node, models[model.SelectedIndex].Name, factor.SelectedIndex == 0 ? 2 : 4);
    }

    private async Task RenameAsync(CanvasNode node)
    {
        if (await Dialogs.InputAsync("重命名节点", node.Title) is not { Length: > 0 } title) return;
        History.Record();
        node.Title = title;
    }

    // ---------- Keyboard ----------

    /// <summary>Give keyboard focus back to the canvas so its shortcuts work again.</summary>
    public void FocusCanvas() => Root.Focus(FocusState.Programmatic);

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Keys pressed on the prompt panel's buttons and lists stay theirs (not Handled, so Tab / arrows still navigate).
        if (e.OriginalSource is TextBox or PasswordBox or RichEditBox || HitTest(e.OriginalSource).Part == "panel") return;
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != Root; element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase) return;
        var ctrl = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Space: _space = true; break;
            case VirtualKey.Delete or VirtualKey.Back: DeleteSelection(); break;
            case VirtualKey.Z when ctrl && shift: Redo(); break;
            case VirtualKey.Z when ctrl: Undo(); break;
            case VirtualKey.Y when ctrl: Redo(); break;
            case VirtualKey.A when ctrl: Select(Doc.Nodes.Select(n => n.Id)); break;
            case VirtualKey.D when ctrl: Duplicate(); break;
            case VirtualKey.G when ctrl && shift: Ungroup(); break;
            case VirtualKey.G when ctrl: Group(); break;
            case VirtualKey.Number0 when ctrl: ResetZoom(); break;
            case VirtualKey.Number1 when ctrl: FitToContent(); break;
            case VirtualKey.Escape when _preview is not null: ClosePreview(); break;
            case VirtualKey.Escape: _selectedConnection = null; Select([]); break;
            case VirtualKey.C when ctrl: CopySelection(); break;
            // Ctrl + = / Ctrl + - (main keyboard or keypad) zoom like the toolbar buttons.
            case (VirtualKey)187 or VirtualKey.Add when ctrl: ZoomBy(1.2); break;
            case (VirtualKey)189 or VirtualKey.Subtract when ctrl: ZoomBy(1 / 1.2); break;
            case VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down when _selected.Count > 0: Nudge(e.Key, shift ? 10 : 1); break;
            // Deferred so this Enter is consumed before the editor gets focus and cannot type a line break.
            case VirtualKey.Enter when _selected.Count == 1: DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusSelected); break;
            case VirtualKey.F2 when _selected.Count == 1 && Doc.Find(_selected.First()) is { } renamed: _ = RenameAsync(renamed); break;
            case VirtualKey.V when ctrl: e.Handled = true; await PasteAsync(ViewCenterWorld); return;
            case VirtualKey.C when !ctrl && !shift && ComposeInputs().Count == 2: _ = TrackLocalAsync(ShowCompareAsync); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Arrow keys move the selection by one screen pixel (Shift: ten); a run of presses on the same selection is one undo step.</summary>
    private void Nudge(VirtualKey key, double pixels)
    {
        var step = pixels / V.K;
        var (dx, dy) = key switch { VirtualKey.Left => (-step, 0.0), VirtualKey.Right => (step, 0.0), VirtualKey.Up => (0.0, -step), _ => (0.0, step) };
        var moving = WithStacks(_selected);
        var groups = _selected.Where(id => Doc.Find(id)?.Kind == NodeKind.Group).ToHashSet();
        var nodes = Doc.Nodes.Where(n => moving.Contains(n.Id) || n.GroupId is not null && groups.Contains(n.GroupId)).ToList();
        if (nodes.Count == 0) return;
        History.Record("nudge:" + string.Join(",", _selected.Order()));
        foreach (var node in nodes)
        {
            node.X += dx;
            node.Y += dy;
        }
    }

    /// <summary>Ctrl+C: the selection's media files (plus the picture itself when it is a single image) and the text of text nodes, for other apps or pasting back.</summary>
    private async void CopySelection()
    {
        var nodes = Doc.Nodes.Where(n => _selected.Contains(n.Id)).ToList();
        var files = nodes.Where(n => n.HasContent && n.Kind is NodeKind.Image or NodeKind.Video or NodeKind.Audio)
            .Select(n => ProjectStore.AssetPath(Doc.Id, n.Content!)).Where(File.Exists).ToList();
        var texts = nodes.Where(n => n.Kind == NodeKind.Text && n.HasContent).Select(n => n.Content!).ToList();
        if (files.Count == 0 && texts.Count == 0) return;
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            if (files.Count > 0)
            {
                var items = new List<IStorageItem>();
                foreach (var file in files) items.Add(await StorageFile.GetFileFromPathAsync(file));
                data.SetStorageItems(items);
                if (files.Count == 1 && Media.KindOf(files[0]) == NodeKind.Image && !Media.IsSvg(files[0])) data.SetBitmap(RandomAccessStreamReference.CreateFromFile((StorageFile)items[0]));
            }
            if (texts.Count > 0) data.SetText(string.Join("\n\n", texts));
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another app.
            App.Log(ex);
            await Dialogs.MessageAsync("复制失败", ex.Message);
        }
    }

    /// <summary>
    /// Double-clicking a picture shows it over the whole canvas area on acrylic, above the page toolbars (a window-level popup);
    /// Ctrl + wheel or pinch zooms it, and a click beside it or Esc closes.
    /// </summary>
    private void ShowPreview(CanvasNode node)
    {
        ClosePreview();
        var path = ProjectStore.AssetPath(Doc.Id, node.Content!);
        if (!File.Exists(path)) return;
        var image = new Image { Source = Media.IsSvg(path) ? new SvgImageSource(new Uri(path)) : new BitmapImage(new Uri(path)), Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        // The picture fits and centres in the visible area at 100 %; zooming enlarges it from there.
        var frame = new Grid { Children = { image } };
        var scroller = new ScrollViewer
        {
            Content = frame, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.5f, MaxZoomFactor = 8,
            HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        scroller.SizeChanged += (_, e) => (frame.Width, frame.Height) = (e.NewSize.Width, e.NewSize.Height);
        Grid.SetRow(scroller, 1);
        var close = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new FontIcon { Glyph = "", FontSize = 14 }, HorizontalAlignment = HorizontalAlignment.Right, AllowFocusOnInteraction = true };
        ToolTipService.SetToolTip(close, "关闭 (Esc)");
        AutomationProperties.SetName(close, "关闭预览");
        close.Click += (_, _) => ClosePreview();
        var caption = new TextBlock { Text = node.Title + (node.NaturalWidth > 0 ? $"  ·  {node.NaturalWidth} × {node.NaturalHeight}" : ""), VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var bar = new Grid { Padding = new Thickness(20, 12, 12, 0), Children = { caption, close } };
        var surface = new Grid
        {
            Width = Root.ActualWidth, Height = Root.ActualHeight, Background = (Brush)Application.Current.Resources["GlassThickBrush"],
            Padding = new Thickness(0, 0, 0, 24), RowSpacing = 12, Children = { bar, scroller },
        };
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition());
        AutomationProperties.SetName(surface, "图片预览");
        surface.Tapped += (_, e) => { if (e.OriginalSource is not Image) ClosePreview(); };
        surface.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { e.Handled = true; ClosePreview(); } };
        // Focus inside the popup so Esc reaches it.
        surface.Loaded += (_, _) => close.Focus(FocusState.Programmatic);
        var origin = Root.TransformToVisual(null).TransformPoint(new Point(0, 0));
        _preview = new Popup { XamlRoot = XamlRoot, Child = surface, HorizontalOffset = origin.X, VerticalOffset = origin.Y, IsOpen = true };
        Motion.Appear(surface, 0.98f);
    }

    /// <summary>
    /// Compare the two selected bitmap pictures (left one is A) in the same full-screen layer as the preview: slider, overlay, difference or flicker, with an
    /// offset to line them up. Esc or the close button leaves it.
    /// </summary>
    private async Task ShowCompareAsync()
    {
        if (ComposeInputs() is not { Count: 2 } pair) return;
        List<CanvasBitmap> bitmaps;
        try { bitmaps = await ImageTools.LoadInputsAsync(this, pair); }
        catch (InvalidOperationException ex)
        {
            await Dialogs.MessageAsync("无法对比", ex.Message);
            return;
        }
        ClosePreview();
        var surface = new CompareSurface(pair[0], pair[1], bitmaps[0], bitmaps[1], Root.ActualWidth, Root.ActualHeight, ClosePreview);
        var origin = Root.TransformToVisual(null).TransformPoint(new Point(0, 0));
        _preview = new Popup { XamlRoot = XamlRoot, Child = surface, HorizontalOffset = origin.X, VerticalOffset = origin.Y, IsOpen = true };
        _preview.Closed += (_, _) => surface.Dispose();
        Motion.Appear(surface, 0.98f);
    }

    /// <summary>For the image_compare tool: the same measurement without a dialog; a failure is a tool error.</summary>
    internal Task<(ImageDifference Difference, bool SizeMismatch)> CompareForAgentAsync(CanvasNode a, CanvasNode b, double threshold) => IsClosing ? throw new McpException("画布正在关闭，请稍后重试") : TrackLocalAsync(async () =>
    {
        try { return await ImageMeasure.MeasureAsync(this, a, b, threshold); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    });

    private void ClosePreview()
    {
        if (_preview is null) return;
        _preview.IsOpen = false;
        _preview = null;
        FocusCanvas();
    }

    private void Root_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space) _space = false;
    }

    /// <summary>Enter on a selected node: edit a text node in place, otherwise jump into the prompt box.</summary>
    private void FocusSelected()
    {
        if (_selected.Count != 1) return;
        var id = _selected.First();
        if (Doc.Find(id) is { Kind: NodeKind.Text } && _views.TryGetValue(id, out var view)) view.FocusEditor();
        else if (_panel.Visibility == Visibility.Visible) _panel.FocusPrompt();
    }

    // ---------- Import / paste ----------

    /// <summary>Clipboard/drag format used by the library panel: newline-separated file paths.</summary>
    public const string LibraryFormat = "InfiniteCanvas.LibraryPaths";

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) && !e.DataView.Contains(LibraryFormat)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "放到画布";
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        var world = ToWorld(e.GetPosition(Root));
        if (e.DataView.Contains(LibraryFormat))
        {
            var paths = (string)await e.DataView.GetDataAsync(LibraryFormat);
            await ImportFilesAsync(paths.Split('\n', StringSplitOptions.RemoveEmptyEntries), world);
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            await ImportFilesAsync(items.OfType<StorageFile>().Select(f => f.Path).ToList(), world);
        }
    }

    private async Task PasteAsync(Point world)
    {
        if (IsClosing) return;
        // The clipboard can be locked by another app; read everything before adding a node so a failure leaves none behind.
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                await ImportFilesAsync(items.OfType<StorageFile>().Select(f => f.Path).ToList(), world);
            }
            else if (content.Contains(StandardDataFormats.Bitmap))
            {
                await TrackLocalAsync(async () =>
                {
                    var reference = await content.GetBitmapAsync();
                    using var source = await reference.OpenReadAsync();
                    // Bitmaps from Win32 apps (screenshots, browsers, Office) usually arrive as BMP; re-encode so the bytes match image/png.
                    var decoder = await BitmapDecoder.CreateAsync(source);
                    using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
                    using var png = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png);
                    encoder.SetSoftwareBitmap(pixels);
                    await encoder.FlushAsync();
                    using var memory = new MemoryStream();
                    await png.GetInputStreamAt(0).AsStreamForRead().CopyToAsync(memory);
                    var bytes = memory.ToArray();
                    var asset = ProjectStore.SaveAsset(Doc.Id, bytes, "image/png");
                    await FillMediaAsync(AddNode(NodeKind.Image, world), asset, "image/png", bytes);
                });
            }
            else if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (!IsClosing) AddNode(NodeKind.Text, world).Content = text;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            await Dialogs.MessageAsync("粘贴失败", ex.Message);
        }
    }

    public async Task ImportWithPickerAsync(Point? world = null)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".bmp", ".mp4", ".mov", ".webm", ".mp3", ".wav", ".m4a", ".aac", ".flac" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await ImportFilesAsync(files.Select(f => f.Path).ToList(), world ?? ViewCenterWorld);
    }

    private async Task ImportIntoAsync(CanvasNode node)
    {
        var picker = new FileOpenPicker();
        var extensions = node.Kind switch
        {
            NodeKind.Image => new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".bmp" },
            NodeKind.Video => [".mp4", ".mov", ".webm"],
            _ => [".mp3", ".wav", ".m4a", ".aac", ".flac"],
        };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        if (await picker.PickSingleFileAsync() is not { } file || IsClosing || Doc.Find(node.Id) != node) return;
        await TrackLocalAsync(async () =>
        {
            var (id, path) = (Doc.Id, file.Path);
            string asset;
            try { asset = await Task.Run(() => ProjectStore.ImportAsset(id, path)); }
            catch (Exception ex)
            {
                App.Log(ex);
                await Dialogs.MessageAsync("导入失败", ex.Message);
                return;
            }
            if (Doc.Find(node.Id) != node) return;
            History.Record();
            await FillMediaAsync(node, asset, Media.MimeOf(path), null);
        });
    }

    /// <summary>Import media files as new nodes from a world point; returns the new node IDs. Files that fail are listed in a dialog.</summary>
    public Task<List<string>> ImportFilesAsync(IReadOnlyList<string> paths, Point world) => IsClosing
        ? Task.FromResult(new List<string>()) : TrackLocalAsync(async () =>
        {
            var result = await ImportFilesCoreAsync(paths, world);
            if (result.Failed.Count > 0) await Dialogs.MessageAsync("以下文件导入失败", "文件不存在、被占用或没有读取权限：\n" + string.Join("\n", result.Failed.Select(f => Path.GetFileName(f.Path))));
            return result.Ids.ToList();
        });

    /// <summary>The same import for agents: no dialog, failures come back in the result so a tool call never waits for someone to dismiss one.</summary>
    internal Task<ImportResult> ImportFilesQuietAsync(IReadOnlyList<string> paths, Point world) => IsClosing
        ? Task.FromResult(new ImportResult([], [])) : TrackLocalAsync(() => ImportFilesCoreAsync(paths, world, decodeFirst: true));

    /// <summary>Why a picture cannot be shown, or null; SVG, video and audio are not checked (an SVG may legally declare no size, and a video's size is not always readable).</summary>
    private static async Task<string?> UnreadableImageAsync(string path)
    {
        if (Media.KindOf(path) != NodeKind.Image || Media.IsSvg(path)) return null;
        // The bitmap decoder alone decides: an SVG saved as .png is not a picture the canvas can show.
        var (width, height) = (0, 0);
        try { (width, height) = await CanvasGenerator.BitmapSizeAsync(await File.ReadAllBytesAsync(path)); }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException) { }
        return width > 0 && height > 0 ? null : "图片无法解码，文件可能已损坏或格式和后缀不符";
    }

    private async Task<ImportResult> ImportFilesCoreAsync(IReadOnlyList<string> paths, Point world, bool decodeFirst = false)
    {
        var id = Doc.Id;
        var failed = new List<ImportFailure>();
        // Copy off the UI thread before touching the document, so a large or unreadable file never freezes the window or leaves an empty node.
        var imported = await Task.Run(async () =>
        {
            var done = new List<(string Path, string Asset)>();
            foreach (var path in paths.Where(p => Media.KindOf(p) is not null))
            {
                // A file already in this canvas's folder (dragged back from 画布文件) is placed as it is, not copied again.
                try
                {
                    // Agents get a file that cannot be decoded refused before anything is copied; dragging one in stays as it was.
                    if (decodeFirst && await UnreadableImageAsync(path) is { } reason) failed.Add(new(path, reason));
                    else done.Add((path, string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), ProjectStore.AssetsDir(id), StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(path) : ProjectStore.ImportAsset(id, path)));
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                    failed.Add(new(path, ex.Message));
                }
            }
            return done;
        });
        var ids = new List<string>();
        if (imported.Count > 0)
        {
            History.Record();
            foreach (var (path, asset) in imported)
            {
                var node = CanvasNode.Create(Media.KindOf(path)!.Value, world.X + ids.Count * 40, world.Y + ids.Count * 40);
                Doc.Nodes.Add(node);
                ids.Add(node.Id);
                await FillMediaAsync(node, asset, Media.MimeOf(path), null);
                // Group by the final, aspect-matched size.
                node.GroupId = CanvasGraph.ContainingGroup(Doc, node)?.Id;
            }
            Select(ids);
        }
        return new(ids, failed);
    }

    /// <summary>Selected bitmap pictures a composition can take, left to right: cards that are shown (a collapsed batch gives its lead), no SVG.</summary>
    private List<CanvasNode> ComposeInputs()
    {
        var hidden = Stacks.Hidden(Doc);
        return Doc.Nodes.Where(n => _selected.Contains(n.Id) && n.Kind == NodeKind.Image && n.HasContent && !Media.IsSvg(n.Content) && !hidden.Contains(n))
            .OrderBy(n => n.X + n.Width / 2).ThenBy(n => n.Y + n.Height / 2).ToList();
    }

    /// <summary>For agent tools: the same composition without a dialog; a failure is a tool error.</summary>
    internal Task<(string Id, int Width, int Height, double Scale)> ComposeForAgentAsync(ComposeSpec spec) => IsClosing ? throw new McpException("画布正在关闭，请稍后重试") : TrackLocalAsync(async () =>
    {
        try { return await ImageTools.ComposeSpecAsync(this, spec); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    });

    /// <summary>Give a media node a new numbered copy of the file, keeping everything but the picture; the old file stays in the canvas folder. One undo step.</summary>
    internal Task ReplaceMediaAsync(CanvasNode node, string path) => IsClosing ? throw new McpException("画布正在关闭，请稍后重试") : TrackLocalAsync(async () =>
    {
        var id = Doc.Id;
        var asset = await Task.Run(async () =>
        {
            if (await UnreadableImageAsync(path) is { } reason) throw new McpException(reason);
            return ProjectStore.ImportAsset(id, path);
        });
        // Refusing here leaves nothing referencing the file this call just copied, so it goes again.
        if (Doc.Find(node.Id) != node || Generator.IsRunning(node.Id))
        {
            try { File.Delete(ProjectStore.AssetPath(id, asset)); }
            catch (Exception ex) { App.Log(ex); }
            throw new McpException(Doc.Find(node.Id) != node ? "节点已被删除" : "节点已开始生成，没有替换");
        }
        History.Record();
        // Marks, conversations and the model's notes describe the old picture only.
        NodeKeys.ForgetPixels(node);
        await FillMediaAsync(node, asset, Media.MimeOf(path), null);
    });

    /// <summary>Attach an asset to a node and match the node to the media's aspect ratio.</summary>
    private async Task FillMediaAsync(CanvasNode node, string asset, string mimeType, byte[]? bytes)
    {
        // Reference the asset before awaiting, so a save while measuring never stores an empty node or prunes the new file.
        var previous = node.Content;
        node.MimeType = mimeType;
        node.Content = asset;
        ProjectStore.TitleFromAsset(node, previous);
        var path = ProjectStore.AssetPath(Doc.Id, asset);
        (int Width, int Height) size = (0, 0);
        if (mimeType.StartsWith("image/")) size = await CanvasGenerator.ImageSizeAsync(bytes ?? await File.ReadAllBytesAsync(path));
        else if (mimeType.StartsWith("video/")) size = await CanvasGenerator.VideoSizeAsync(path);
        node.NaturalWidth = size.Width;
        node.NaturalHeight = size.Height;
        if (size.Width > 0) node.Height = Math.Round(node.Width * size.Height / size.Width);
        node.Status = NodeStatus.Success;
        node.Error = null;
    }

    /// <summary>
    /// What local editing the node's current image model offers: a repaint mask and a markup layer (GPT Image) and / or points and boxes
    /// (Seedream 5.0 pro / flash). SVG pictures have no pixels to mark.
    /// </summary>
    public (bool Mask, bool Points, bool Markup) MarkSupport(CanvasNode node)
    {
        var settings = SettingsStore.Current;
        if (Media.IsSvg(node.Content) || settings.Resolve(settings.ModelFor(Capability.Image, node.Model)) is not { } resolved) return (false, false, false);
        var family = ModelFamilies.FindImage(resolved.Channel.Vendor, resolved.Model.Name);
        var openAI = resolved.Channel.Vendor == Vendor.OpenAI;
        return (openAI && family?.Mask == true, family?.Interactive == true, openAI && family?.Markup == true);
    }

    public Task EditMarksAsync(CanvasNode node) => IsClosing ? Task.CompletedTask : TrackLocalAsync(() => EditMarksCoreAsync(node));

    private async Task EditMarksCoreAsync(CanvasNode node)
    {
        var (allowMask, allowPoints, allowMarkup) = MarkSupport(node);
        if (!allowMask && !allowPoints && !allowMarkup)
        {
            await Dialogs.MessageAsync(Media.IsSvg(node.Content) ? "SVG 不能局部编辑" : "当前模型不支持局部编辑", Media.IsSvg(node.Content)
                ? "局部编辑需要位图，SVG 矢量图请直接用提示词修改。"
                : "GPT Image 支持涂抹蒙版局部重绘，GPT Image 2.5 / 2.6 还支持画圈标注，Seedream 5.0 pro / flash 支持点选和框选。请先在下方面板把模型切换到这些型号。");
            return;
        }
        try
        {
            var path = ProjectStore.AssetPath(Doc.Id, node.Content!);
            // The file may have been overwritten in another app, so size the mask from the file itself.
            var (width, height) = await CanvasGenerator.ImageSizeAsync(await File.ReadAllBytesAsync(path));
            if (width <= 0 || height <= 0 || Math.Max(width, height) > CanvasDevice.GetSharedDevice().MaximumBitmapSizeInPixels)
            {
                await Dialogs.MessageAsync("无法局部编辑", "这张图片无法解码，或尺寸超过显卡支持的最大位图尺寸。");
                return;
            }
            var maskPath = node.Option(NodeKeys.Mask) is { Length: > 0 } maskAsset ? ProjectStore.AssetPath(Doc.Id, maskAsset) : null;
            var mask = maskPath is not null && File.Exists(maskPath) ? await File.ReadAllBytesAsync(maskPath) : null;
            var markupPath = node.Option(NodeKeys.Markup) is { Length: > 0 } markupAsset ? ProjectStore.AssetPath(Doc.Id, markupAsset) : null;
            var markup = markupPath is not null && File.Exists(markupPath) ? await File.ReadAllBytesAsync(markupPath) : null;
            var dialog = new ImageMarkDialog(path, width, height, mask, markup, NodeKeys.SelectionsOf(node), allowMask, allowPoints, allowMarkup);
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            History.Record();
            node.SetOption(NodeKeys.Mask, dialog.MaskPng is { } png ? ProjectStore.SaveAsset(Doc.Id, png, "image/png", ProjectStore.MaskPrefix) : null);
            node.SetOption(NodeKeys.Markup, dialog.MarkupPng is { } layer ? ProjectStore.SaveAsset(Doc.Id, layer, "image/png", ProjectStore.MarkupPrefix) : null);
            NodeKeys.SetSelections(node, dialog.Selections);
            RaiseChanged();
            _panel.Refresh();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            await Dialogs.MessageAsync("局部编辑打开失败", ex.Message);
        }
    }

    public void ClearMarks(CanvasNode node)
    {
        History.Record();
        node.SetOption(NodeKeys.Mask, null);
        node.SetOption(NodeKeys.Markup, null);
        NodeKeys.SetSelections(node, []);
        RaiseChanged();
    }

    /// <summary>Give the library copy the node title, which the user may have changed from the numbered file name.</summary>
    private static string NamedCopy(CanvasNode node, string path)
    {
        var name = string.Concat(node.Title.Split(Path.GetInvalidFileNameChars())).Trim();
        if (name.Length == 0) return path;
        var dir = Path.Combine(Path.GetTempPath(), "InfiniteCanvas", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, name + Path.GetExtension(path));
        File.Copy(path, copy);
        return copy;
    }

    /// <summary>Copy into this canvas's library off the UI thread and drop the temporary named copy afterwards (never the asset itself when no copy was made).</summary>
    internal async Task AddToLibraryAsync(CanvasNode node)
    {
        var (projectId, path) = (Doc.Id, ProjectStore.AssetPath(Doc.Id, node.Content!));
        var snapshot = node.Clone();
        var copy = await Task.Run(() => NamedCopy(snapshot, path));
        try { await LibraryStore.AddAsync(projectId, [copy]); }
        finally { if (copy != path) Directory.Delete(Path.GetDirectoryName(copy)!, true); }
    }

    /// <summary>For agents: the same copy, but a name already in the library is an error unless replacing it; the library file name.</summary>
    internal async Task<string> PutInLibraryAsync(CanvasNode node, bool replace)
    {
        var (projectId, path) = (Doc.Id, ProjectStore.AssetPath(Doc.Id, node.Content!));
        var snapshot = node.Clone();
        var copy = await Task.Run(() => NamedCopy(snapshot, path));
        try { return await LibraryStore.PutAsync(projectId, copy, replace); }
        finally { if (copy != path) Directory.Delete(Path.GetDirectoryName(copy)!, true); }
    }

    private static async Task SaveAsAsync(CanvasNode node, string path)
    {
        var picker = new FileSavePicker { SuggestedFileName = node.Title };
        var ext = Path.GetExtension(path);
        picker.FileTypeChoices.Add(ext.TrimStart('.').ToUpperInvariant(), [ext]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        if (await picker.PickSaveFileAsync() is { } file) File.Copy(path, file.Path, true);
    }
}
