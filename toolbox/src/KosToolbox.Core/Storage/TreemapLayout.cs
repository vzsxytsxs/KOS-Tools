namespace KosToolbox.Core.Storage;

/// <summary>A rectangle of the treemap: where, and which weight (by its index in the list given) it belongs to.</summary>
public readonly record struct TreemapCell(double X, double Y, double Width, double Height, int Index);

/// <summary>
/// Squarified treemap (Bruls, Huizing and van Wijk): lays weights out as rectangles that fill a rectangle, each with an area in
/// proportion to its weight and as close to a square as the rows allow. Pure, no UI. Weights of zero or less get no cell.
/// </summary>
public static class TreemapLayout
{
    /// <summary>Cells for <paramref name="weights"/> (largest first, as the folders of a scan are) inside the rectangle.</summary>
    public static List<TreemapCell> Squarify(IReadOnlyList<double> weights, double x, double y, double width, double height)
    {
        var cells = new List<TreemapCell>();
        var items = Enumerable.Range(0, weights.Count).Where(i => weights[i] > 0).ToList();
        var total = items.Sum(i => weights[i]);
        if (items.Count == 0 || total <= 0 || width <= 0 || height <= 0) return cells;
        var scale = width * height / total; // weight → area
        var next = 0;
        while (next < items.Count)
        {
            var side = Math.Min(width, height);
            var row = new List<int> { items[next] };
            var rowArea = weights[items[next]] * scale;
            var worst = Worst(rowArea, weights[items[next]] * scale, weights[items[next]] * scale, side);
            var end = next + 1;
            while (end < items.Count)
            {
                var area = weights[items[end]] * scale;
                var sum = rowArea + area;
                var largest = weights[row[0]] * scale;
                var smallest = area; // sorted: each new item is the smallest so far
                var candidate = Worst(sum, largest, smallest, side);
                if (candidate > worst) break;
                row.Add(items[end]);
                rowArea = sum;
                worst = candidate;
                end++;
            }
            // The row fills a strip along the short side.
            if (width >= height)
            {
                var strip = rowArea / height;
                var top = y;
                foreach (var i in row) { var h = weights[i] * scale / strip; cells.Add(new TreemapCell(x, top, strip, h, i)); top += h; }
                x += strip;
                width -= strip;
            }
            else
            {
                var strip = rowArea / width;
                var left = x;
                foreach (var i in row) { var w = weights[i] * scale / strip; cells.Add(new TreemapCell(left, y, w, strip, i)); left += w; }
                y += strip;
                height -= strip;
            }
            next = end;
            if (width <= 0 || height <= 0) break;
        }
        return cells;
    }

    /// <summary>The worst aspect ratio of a row with this total area, largest and smallest cell area, laid along a side.</summary>
    static double Worst(double sum, double largest, double smallest, double side) =>
        Math.Max(side * side * largest / (sum * sum), sum * sum / (side * side * smallest));
}
