using System.Numerics;
using InfiniteCanvas.Core.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace InfiniteCanvas.App.Board;

/// <summary>Overview of the whole canvas with the visible area outlined; press or drag on it to move there.</summary>
public sealed class MiniMap : UserControl
{
    private const double MapWidth = 240, MapHeight = 160, Pad = 10;
    private readonly CanvasControl _canvas = new() { Width = MapWidth, Height = MapHeight, ClearColor = Colors.Transparent };
    private CanvasView? _board;
    private bool _dragging;
    // World → map transform of the last draw, reused for pointer input.
    private double _scale = 1;
    private Point _origin;

    public MiniMap()
    {
        var shell = new Border { Style = (Style)Application.Current.Resources["FloatingPanel"], Padding = new Thickness(0), Child = _canvas };
        Content = shell;
        AutomationProperties.SetName(this, "小地图");
        _canvas.Draw += Draw;
        _canvas.PointerPressed += (_, e) => { _dragging = true; _canvas.CapturePointer(e.Pointer); Jump(e); e.Handled = true; };
        _canvas.PointerMoved += (_, e) => { if (_dragging) Jump(e); };
        _canvas.PointerReleased += (_, e) => { _dragging = false; _canvas.ReleasePointerCapture(e.Pointer); };
        _canvas.PointerCaptureLost += (_, _) => _dragging = false;
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    public void Attach(CanvasView board)
    {
        _board = board;
        board.Changed += Refresh;
        board.ViewportChanged += Refresh;
    }

    public void Refresh()
    {
        if (Visibility == Visibility.Visible) _canvas.Invalidate();
    }

    private void Jump(PointerRoutedEventArgs e)
    {
        if (_board is null) return;
        var p = e.GetCurrentPoint(_canvas).Position;
        _board.CenterOn(new Point((p.X - _origin.X) / _scale, (p.Y - _origin.Y) / _scale));
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_board is null) return;
        var ds = args.DrawingSession;
        var nodes = _board.Doc.Nodes;
        var visible = _board.VisibleWorld;
        // Frame both the content and the visible area so the outline never leaves the map.
        var bounds = nodes.Count > 0 ? CanvasGraph.Bounds(nodes) : visible;
        var (left, top) = (Math.Min(bounds.Left, visible.Left), Math.Min(bounds.Top, visible.Top));
        var (right, bottom) = (Math.Max(bounds.Right, visible.Right), Math.Max(bounds.Bottom, visible.Bottom));
        _scale = Math.Min((MapWidth - Pad * 2) / Math.Max(1, right - left), (MapHeight - Pad * 2) / Math.Max(1, bottom - top));
        _origin = new Point(Pad + ((MapWidth - Pad * 2) - (right - left) * _scale) / 2 - left * _scale, Pad + ((MapHeight - Pad * 2) - (bottom - top) * _scale) / 2 - top * _scale);
        Rect Map(double x, double y, double w, double h) => new(_origin.X + x * _scale, _origin.Y + y * _scale, Math.Max(2, w * _scale), Math.Max(2, h * _scale));

        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color color ? color : Colors.DodgerBlue;
        // Nodes in their kind colours, like the cards and links on the canvas.
        foreach (var node in nodes)
        {
            var rect = Map(node.X, node.Y, node.Width, node.Height);
            var kind = Palette.Kind(node.Kind, ActualTheme);
            if (node.Kind == NodeKind.Group) ds.DrawRoundedRectangle(rect, 3, 3, Color.FromArgb(200, kind.R, kind.G, kind.B), 1);
            else ds.FillRoundedRectangle(rect, 2, 2, Color.FromArgb(190, kind.R, kind.G, kind.B));
        }
        var view = Map(visible.Left, visible.Top, visible.Width, visible.Height);
        ds.FillRectangle(view, Color.FromArgb(28, accent.R, accent.G, accent.B));
        ds.DrawRectangle(view, accent, 1.5f);
    }
}
