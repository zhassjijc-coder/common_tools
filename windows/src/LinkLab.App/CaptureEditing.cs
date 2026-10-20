using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LinkLab.App;

[Flags]
internal enum CaptureEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
internal enum CaptureTool { Select, Text, Rectangle, Square }
internal sealed record CaptureMark(Rect Bounds, string? Text, Color Color, double FontSize = 20, double WrapWidth = 0)
{
    internal bool IsText => Text is not null;
}

internal static class CaptureEditing
{
    internal static readonly Typeface Typeface = new("Microsoft YaHei");
    internal static Rect Move(Rect rect, Vector delta, Size bounds) => new(
        Math.Clamp(rect.X + delta.X, 0, Math.Max(0, bounds.Width - rect.Width)),
        Math.Clamp(rect.Y + delta.Y, 0, Math.Max(0, bounds.Height - rect.Height)), rect.Width, rect.Height);

    internal static Rect Resize(Rect rect, Vector delta, CaptureEdge edge, Size bounds)
    {
        double l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;
        if (edge.HasFlag(CaptureEdge.Left)) l = Math.Clamp(l + delta.X, 0, r - 2);
        if (edge.HasFlag(CaptureEdge.Right)) r = Math.Clamp(r + delta.X, l + 2, bounds.Width);
        if (edge.HasFlag(CaptureEdge.Top)) t = Math.Clamp(t + delta.Y, 0, b - 2);
        if (edge.HasFlag(CaptureEdge.Bottom)) b = Math.Clamp(b + delta.Y, t + 2, bounds.Height);
        return new(new Point(l, t), new Point(r, b));
    }

    internal static Rect Box(Point anchor, Point end, Size bounds, bool square)
    {
        end = new(Math.Clamp(end.X, 0, bounds.Width), Math.Clamp(end.Y, 0, bounds.Height));
        if (square)
        {
            double side = Math.Min(Math.Abs(end.X - anchor.X), Math.Abs(end.Y - anchor.Y));
            end = new(anchor.X + Math.Sign(end.X - anchor.X) * side, anchor.Y + Math.Sign(end.Y - anchor.Y) * side);
        }
        return new(anchor, end);
    }

    internal static FormattedText Text(CaptureMark mark)
    {
        var text = new FormattedText(mark.Text ?? "", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            Typeface, mark.FontSize, new SolidColorBrush(mark.Color), 1)
        { MaxTextWidth = Math.Max(2, mark.WrapWidth > 0 ? mark.WrapWidth : mark.Bounds.Width), Trimming = TextTrimming.None };
        return text;
    }

    internal static CaptureMark Label(Point point, string text, Color color, double fontSize, double maxWidth)
    {
        var mark = new CaptureMark(new Rect(point.X, point.Y, Math.Max(2, maxWidth), 1), text, color, fontSize, Math.Max(2, maxWidth));
        var formatted = Text(mark);
        return mark with { Bounds = new Rect(point.X, point.Y, Math.Max(2, formatted.WidthIncludingTrailingWhitespace), Math.Max(fontSize, formatted.Height)) };
    }

    internal static void Draw(DrawingContext dc, CaptureMark mark)
    {
        if (mark.IsText) dc.DrawText(Text(mark), mark.Bounds.TopLeft);
        else dc.DrawRectangle(null, new Pen(new SolidColorBrush(mark.Color), 2), mark.Bounds);
    }

    internal static BitmapSource Composite(BitmapSource raw, Vector pixelScale, Vector pixelOffset, IReadOnlyList<CaptureMark> marks)
    {
        if (marks.Count == 0) return raw;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(raw, new Rect(0, 0, raw.PixelWidth, raw.PixelHeight));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, raw.PixelWidth, raw.PixelHeight)));
            dc.PushTransform(new TranslateTransform(pixelOffset.X, pixelOffset.Y));
            dc.PushTransform(new ScaleTransform(pixelScale.X, pixelScale.Y));
            foreach (var mark in marks) Draw(dc, mark);
            dc.Pop(); dc.Pop(); dc.Pop();
        }
        var result = new RenderTargetBitmap(raw.PixelWidth, raw.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
}

internal sealed class CaptureMarkLayer : FrameworkElement
{
    internal IReadOnlyList<CaptureMark> Marks { get; set; } = [];
    internal CaptureMark? Pending { get; set; }
    internal int Selected { get; set; } = -1;
    internal int Editing { get; set; } = -1;
    internal double Zoom { get; set; } = 1;
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight))));
        for (int n = 0; n < Marks.Count; n++) if (n != Editing) CaptureEditing.Draw(dc, Marks[n]);
        if (Pending is not null) CaptureEditing.Draw(dc, Pending);
        if (Selected >= 0 && Selected < Marks.Count && Selected != Editing)
        {
            Rect box = Marks[Selected].Bounds; box.Inflate(4 / Zoom, 4 / Zoom);
            dc.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1 / Zoom) { DashStyle = DashStyles.Dash }, box);
        }
        dc.Pop();
    }
}
