using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CFLDetectools.Desktop;

public readonly record struct PlotPoint(double X, double Y);

public sealed class LinePlot : FrameworkElement
{
    public string XLabel { get; set; } = "X";
    public string YLabel { get; set; } = "Y";
    public double MaximumGapX { get; set; } = double.PositiveInfinity;
    private IReadOnlyList<PlotPoint> _points = Array.Empty<PlotPoint>();

    public void SetPoints(IEnumerable<PlotPoint> points)
    {
        _points = points.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToArray();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 120 || h < 90) return;
        var rect = new Rect(65, 20, w - 90, h - 65);
        drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        DrawText(drawing, YLabel, 8, 0, 11);
        DrawText(drawing, XLabel, Math.Max(65, w / 2 - 50), h - 20, 11);
        if (_points.Count == 0)
        {
            DrawText(drawing, "No valid samples", rect.Left + 20, rect.Top + 25, 14);
            return;
        }
        var xmin = _points.Min(p => p.X);
        var xmax = _points.Max(p => p.X);
        var ymin = Math.Min(0, _points.Min(p => p.Y));
        var ymax = Math.Max(0, _points.Max(p => p.Y));
        if (xmax <= xmin) xmax = xmin + 1;
        if (ymax <= ymin) ymax = ymin + 1;
        var padding = (ymax - ymin) * 0.08;
        ymin -= padding;
        ymax += padding;
        Point Map(PlotPoint p) => new(rect.Left + (p.X - xmin) / (xmax - xmin) * rect.Width,
            rect.Bottom - (p.Y - ymin) / (ymax - ymin) * rect.Height);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1);
        for (var i = 0; i <= 4; i++)
        {
            var fraction = i / 4.0;
            var x = rect.Left + fraction * rect.Width;
            var y = rect.Bottom - fraction * rect.Height;
            drawing.DrawLine(grid, new Point(x, rect.Top), new Point(x, rect.Bottom));
            drawing.DrawLine(grid, new Point(rect.Left, y), new Point(rect.Right, y));
            DrawText(drawing, (xmin + fraction * (xmax - xmin)).ToString("0.##", CultureInfo.InvariantCulture), x - 12, rect.Bottom + 5, 10);
            DrawText(drawing, (ymin + fraction * (ymax - ymin)).ToString("0.##", CultureInfo.InvariantCulture), 8, y - 7, 10);
        }
        var line = new Pen(new SolidColorBrush(Color.FromRgb(8, 145, 178)), 1.7);
        for (var i = 1; i < _points.Count; i++)
            if (_points[i].X - _points[i - 1].X <= MaximumGapX)
                drawing.DrawLine(line, Map(_points[i - 1]), Map(_points[i]));
        if (_points.Count < 30)
            foreach (var p in _points) drawing.DrawEllipse(Brushes.DarkCyan, null, Map(p), 3, 3);
    }

    private void DrawText(DrawingContext drawing, string value, double x, double y, double size)
    {
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(text, new Point(x, y));
    }
}
