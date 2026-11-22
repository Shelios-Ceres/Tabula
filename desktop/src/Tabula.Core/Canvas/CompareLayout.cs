namespace Tabula.Core.Canvas;

/// <summary>How many pictures the side-by-side compare view shows (the user's limit), and how it lays them out.</summary>
public static class CompareLayout
{
    public const int MaxPictures = 4;

    /// <summary>
    /// The grid (columns × rows, filled left to right, top to bottom) that shows the pictures largest in a window of <paramref name="width"/> × <paramref name="height"/>:
    /// every picture is fitted into its cell and the grid with the most picture area wins; a tie goes to fewer columns. <paramref name="captionHeight"/> is reserved under each cell.
    /// </summary>
    public static (int Columns, int Rows) Choose(double width, double height, IReadOnlyList<(double Width, double Height)> pictures, double captionHeight = 0)
    {
        var count = pictures.Count;
        if (count <= 1) return (1, 1);
        (int Columns, int Rows) best = (count, 1);
        var bestArea = -1d;
        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (count + columns - 1) / columns;
            // Skip grids that leave a ragged last row when fewer columns fit the same rows (4 pictures as 3 + 1: 2 × 2 or 4 × 1 cover that).
            if ((columns - 1) * rows >= count) continue;
            var (cellWidth, cellHeight) = (width / columns, Math.Max(1, height / rows - captionHeight));
            var area = 0d;
            foreach (var (w, h) in pictures)
            {
                if (w <= 0 || h <= 0) continue;
                var scale = Math.Min(cellWidth / w, cellHeight / h);
                area += w * scale * h * scale;
            }
            if (area > bestArea * 1.0000001) (best, bestArea) = ((columns, rows), area);
        }
        return best;
    }
}
