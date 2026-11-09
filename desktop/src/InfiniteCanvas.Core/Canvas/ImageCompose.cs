namespace InfiniteCanvas.Core.Canvas;

/// <summary>One input of a composition: its node, and where the figure's head top and feet sit as fractions (0-1) of the image height; both null when not aligned.</summary>
public sealed record ComposeCell(string NodeId, double? Top = null, double? Bottom = null);

/// <summary>Everything a composition needs; stored as JSON in the result node's options under <see cref="NodeKeys.Compose"/>. Sizes are read from the files each time, never stored.</summary>
public sealed record ComposeSpec(List<ComposeCell> Cells, string Layout = "row", int Columns = 0, int Gap = 64, int Margin = 64, string Background = "#FFFFFF", string Fit = "height");

/// <summary>Where one input goes: its cell box and the rectangle its picture is drawn into (both in output pixels).</summary>
public sealed record ComposePlacement(PixelRect Cell, double X, double Y, double Width, double Height);

/// <summary>Output size, the even down-scale applied (1 when none), and each input's placement.</summary>
public sealed record ComposeResult(int Width, int Height, double Scale, List<ComposePlacement> Cells);

/// <summary>The arithmetic of a composition (contact sheet / turnaround sheet): pure, so the dialog preview, the render and the tests all use the same numbers.</summary>
public static class ImageCompose
{
    public static readonly string[] Layouts = ["row", "column", "grid"], Fits = ["height", "width"];

    /// <summary>#RRGGBB or "transparent".</summary>
    public static (byte R, byte G, byte B, byte A)? ParseBackground(string? text)
    {
        if (text is null) return null;
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return (0, 0, 0, 0);
        if (text.Length != 7 || text[0] != '#' || !int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var rgb)) return null;
        return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
    }

    /// <summary>Why a spec cannot be composed, or null: at least two inputs, known layout and fit, no negative number, a readable background, and marks that are all there or all absent (row layout, fit by height, 0 ≤ top &lt; bottom ≤ 1).</summary>
    public static string? Problem(ComposeSpec spec)
    {
        if (spec.Cells.Count < 2) return "至少要有两张图片";
        if (!Layouts.Contains(spec.Layout)) return $"layout 只能是 row、column 或 grid，收到「{spec.Layout}」";
        if (!Fits.Contains(spec.Fit)) return $"fit 只能是 height 或 width，收到「{spec.Fit}」";
        if (spec.Columns < 0 || spec.Gap < 0 || spec.Margin < 0) return "columns、gap、margin 不能是负数";
        if (ParseBackground(spec.Background) is null) return $"background 只能是 #RRGGBB 或 transparent，收到「{spec.Background}」";
        var marked = spec.Cells.Count(c => c.Top is not null || c.Bottom is not null);
        if (marked == 0) return null;
        if (marked != spec.Cells.Count || spec.Cells.Any(c => c.Top is null || c.Bottom is null)) return "头顶和脚底要么每张都标、要么都不标";
        if (spec.Layout != "row" || spec.Fit != "height") return "按头顶和脚底对齐只能用于一行（row）并按等高（height）适配";
        if (spec.Cells.Any(c => !double.IsFinite(c.Top!.Value) || !double.IsFinite(c.Bottom!.Value) || c.Top < 0 || c.Top >= c.Bottom || c.Bottom > 1)) return "头顶和脚底必须满足 0 ≤ top < bottom ≤ 1";
        return null;
    }

    /// <summary>
    /// Lay the inputs out. Fit "height" (default) brings every picture to the smallest height among them, "width" to the smallest width, so nothing is enlarged.
    /// Row and column put cells side by side or one under the other, grid uses <c>Columns</c> or about √n columns with cells as large as the largest picture
    /// (pictures centred in them). With marks (row only) every figure is scaled to the smallest figure's height and set on one feet line. When the longer side
    /// would pass <paramref name="deviceMax"/> (the graphics device's limit, 0 for none) everything, gaps and margin included, shrinks evenly.
    /// </summary>
    public static ComposeResult Layout(ComposeSpec spec, IReadOnlyList<(int Width, int Height)> sizes, int deviceMax)
    {
        if (Problem(spec) is { } problem) throw new ArgumentException(problem);
        if (sizes.Count != spec.Cells.Count || sizes.Any(s => s.Width <= 0 || s.Height <= 0)) throw new ArgumentException("每张图片都需要有效的像素尺寸");
        var n = sizes.Count;
        var (w, h) = (new double[n], new double[n]);
        var byHeight = spec.Fit == "height";
        var common = byHeight ? sizes.Min(s => s.Height) : sizes.Min(s => s.Width);
        for (var i = 0; i < n; i++)
        {
            var k = (double)common / (byHeight ? sizes[i].Height : sizes[i].Width);
            (w[i], h[i]) = (sizes[i].Width * k, sizes[i].Height * k);
        }
        // Two-line alignment: the figures (bottom - top of the image height) all become as tall as the smallest, feet on one line.
        var drop = new double[n];
        var aligned = spec.Cells[0].Top is not null;
        if (aligned)
        {
            var figure = Enumerable.Range(0, n).Select(i => (spec.Cells[i].Bottom!.Value - spec.Cells[i].Top!.Value) * h[i]).ToArray();
            var target = figure.Min();
            for (var i = 0; i < n; i++) (w[i], h[i]) = (w[i] * target / figure[i], h[i] * target / figure[i]);
            var feet = Enumerable.Range(0, n).Max(i => spec.Cells[i].Bottom!.Value * h[i]);
            for (var i = 0; i < n; i++) drop[i] = feet - spec.Cells[i].Bottom!.Value * h[i];
        }

        var cells = new List<(double X, double Y, double W, double H, double ImageX, double ImageY)>(n);
        double width, height;
        double gap = spec.Gap, margin = spec.Margin;
        switch (spec.Layout)
        {
            case "row":
            {
                var cellHeight = Enumerable.Range(0, n).Max(i => drop[i] + h[i]);
                var x = margin;
                for (var i = 0; i < n; i++)
                {
                    cells.Add((x, margin, w[i], cellHeight, x, margin + (aligned ? drop[i] : (cellHeight - h[i]) / 2)));
                    x += w[i] + gap;
                }
                (width, height) = (x - gap + margin, cellHeight + 2 * margin);
                break;
            }
            case "column":
            {
                var cellWidth = w.Max();
                var y = margin;
                for (var i = 0; i < n; i++)
                {
                    cells.Add((margin, y, cellWidth, h[i], margin + (cellWidth - w[i]) / 2, y));
                    y += h[i] + gap;
                }
                (width, height) = (cellWidth + 2 * margin, y - gap + margin);
                break;
            }
            default:
            {
                var columns = spec.Columns > 0 ? Math.Min(spec.Columns, n) : (int)Math.Ceiling(Math.Sqrt(n));
                var rows = (n + columns - 1) / columns;
                var (cellWidth, cellHeight) = (w.Max(), h.Max());
                for (var i = 0; i < n; i++)
                {
                    var (x, y) = (margin + i % columns * (cellWidth + gap), margin + i / columns * (cellHeight + gap));
                    cells.Add((x, y, cellWidth, cellHeight, x + (cellWidth - w[i]) / 2, y + (cellHeight - h[i]) / 2));
                }
                (width, height) = (columns * cellWidth + (columns - 1) * gap + 2 * margin, rows * cellHeight + (rows - 1) * gap + 2 * margin);
                break;
            }
        }

        var scale = deviceMax > 0 ? Math.Min(1, deviceMax / Math.Max(width, height)) : 1;
        var placements = new List<ComposePlacement>(n);
        for (var i = 0; i < n; i++)
        {
            var (x, y, cw, ch, ix, iy) = cells[i];
            // Edges are rounded, not sizes, so neighbouring cells never leave a gap or overlap by a pixel.
            var (left, top) = ((int)Math.Round(x * scale), (int)Math.Round(y * scale));
            placements.Add(new(new(left, top, (int)Math.Round((x + cw) * scale) - left, (int)Math.Round((y + ch) * scale) - top), ix * scale, iy * scale, w[i] * scale, h[i] * scale));
        }
        return new((int)Math.Round(width * scale), (int)Math.Round(height * scale), scale, placements);
    }
}
