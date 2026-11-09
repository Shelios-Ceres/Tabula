namespace InfiniteCanvas.Core.Canvas;

/// <summary>A box as fractions (0-1) of the compared picture's width and height.</summary>
public sealed record UnitBox(double X, double Y, double Width, double Height);

/// <summary>
/// How two pictures differ: the mean and the largest per-channel difference (0-1), the share of compared pixels that changed by more than the threshold,
/// and the box around them. <see cref="Compared"/> is the number of pixels both pictures cover.
/// </summary>
public sealed record ImageDifference(double MeanDifference, double MaxDifference, double ChangedFraction, UnitBox? ChangedBox, double Threshold, int Compared);

/// <summary>The statistics behind image_compare: pure arithmetic on two same-sized BGRA pixel buffers.</summary>
public static class ImageDifferenceMath
{
    /// <summary>
    /// Compare picture <paramref name="a"/> with <paramref name="b"/> (both BGRA, <c>width × height</c>). Pixels where <paramref name="b"/> is fully transparent
    /// are not covered by it and are left out. A pixel counts as changed when any colour channel differs by more than <paramref name="threshold"/> (0-1).
    /// </summary>
    public static ImageDifference Measure(byte[] a, byte[] b, int width, int height, double threshold = 0.05)
    {
        if (width <= 0 || height <= 0 || a.Length != width * height * 4 || b.Length != a.Length) throw new ArgumentException("两张图的像素数据大小不一致");
        double sum = 0, max = 0;
        int compared = 0, changed = 0, left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            if (b[i + 3] == 0) continue;
            int d0 = Math.Abs(a[i] - b[i]), d1 = Math.Abs(a[i + 1] - b[i + 1]), d2 = Math.Abs(a[i + 2] - b[i + 2]);
            var largest = Math.Max(d0, Math.Max(d1, d2)) / 255d;
            sum += (d0 + d1 + d2) / (3 * 255d);
            max = Math.Max(max, largest);
            compared++;
            if (largest <= threshold) continue;
            changed++;
            (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
        }
        UnitBox? box = changed == 0 ? null : new((double)left / width, (double)top / height, (right - left + 1d) / width, (bottom - top + 1d) / height);
        return new(compared == 0 ? 0 : sum / compared, max, compared == 0 ? 0 : (double)changed / compared, box, threshold, compared);
    }
}
