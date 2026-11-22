using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Tabula.App.Board;

/// <summary>Minimal flow layout for floating capsules: wraps items onto the next row when they exceed the available width instead of clipping them.</summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 6;
    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double x = 0, y = 0, rowHeight = 0, totalWidth = 0;
        var unlimited = new Size(maxWidth, double.PositiveInfinity);
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(unlimited);
            var size = child.DesiredSize;
            if (x > 0 && x + HorizontalSpacing + size.Width > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, x);
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }
            if (x > 0) x += HorizontalSpacing;
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(Math.Max(totalWidth, x), y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxWidth = finalSize.Width > 0 ? finalSize.Width : double.MaxValue;
        double x = 0, y = 0, rowHeight = 0;
        var row = new List<(UIElement Child, double X, Size Size)>();

        void Flush()
        {
            foreach (var (child, cx, size) in row)
                child.Arrange(new Rect(cx, y + (rowHeight - size.Height) / 2, size.Width, size.Height));
            row.Clear();
        }

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (row.Count > 0 && x + HorizontalSpacing + size.Width > maxWidth)
            {
                Flush();
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }
            if (row.Count > 0) x += HorizontalSpacing;
            row.Add((child, x, size));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        Flush();
        return new Size(finalSize.Width, y + rowHeight);
    }
}
