namespace InfiniteCanvas.Core.Canvas;

/// <summary>Whole-pixel rectangle inside an image.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Pixel maths for the image tools: crop boxes, grid splits and the multi-angle prompt.</summary>
public static class ImageGeometry
{
    /// <summary>The largest box of the given aspect (width / height) centred in the image.</summary>
    public static PixelRect CenteredCrop(int width, int height, double aspect)
    {
        if (aspect <= 0) return new(0, 0, width, height);
        var (w, h) = (double)width / height > aspect ? ((int)Math.Round(height * aspect), height) : (width, (int)Math.Round(width / aspect));
        (w, h) = (Math.Clamp(w, 1, width), Math.Clamp(h, 1, height));
        return new((width - w) / 2, (height - h) / 2, w, h);
    }

    /// <summary>Clamp a box to the image, keeping at least one pixel.</summary>
    public static PixelRect Clamp(PixelRect box, int width, int height)
    {
        var x = Math.Clamp(box.X, 0, width - 1);
        var y = Math.Clamp(box.Y, 0, height - 1);
        return new(x, y, Math.Clamp(box.Width, 1, width - x), Math.Clamp(box.Height, 1, height - y));
    }

    /// <summary>An even rows × columns grid; the last row and column take the remainder so the tiles cover every pixel exactly once.</summary>
    public static List<PixelRect> Split(int width, int height, int rows, int columns)
    {
        rows = Math.Clamp(rows, 1, height);
        columns = Math.Clamp(columns, 1, width);
        var (w, h) = (width / columns, height / rows);
        var tiles = new List<PixelRect>(rows * columns);
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < columns; c++)
            tiles.Add(new(c * w, r * h, c == columns - 1 ? width - c * w : w, r == rows - 1 ? height - r * h : h));
        return tiles;
    }

    /// <summary>The camera description for a new view of the same subject, as the web client words it.</summary>
    public static string AnglePrompt(int horizontal, int pitch, double distance, bool wide)
    {
        var turn = horizontal == 0 ? "正面视角" : horizontal > 0 ? $"向右旋转 {horizontal} 度" : $"向左旋转 {-horizontal} 度";
        var tilt = pitch == 0 ? "水平视角" : pitch > 0 ? $"俯视 {pitch} 度" : $"仰视 {-pitch} 度";
        return $"基于参考图重新生成同一主体的新视角，保持主体、颜色、材质和画面风格一致，不要只做透视变形。{turn}，{tilt}，镜头距离 {distance:0.0}，{(wide ? "广角" : "标准")}镜头。";
    }
}
