using System.Drawing;

namespace TecLink.Core;

public enum ScreenEdge { Left, Right, Top, Bottom }
public sealed record DisplayInfo(string Id, int Left, int Top, int Width, int Height)
{
    public Rectangle Bounds => new(Left, Top, Width, Height);
}
public sealed class ScreenRoute
{
    public string SourceId { get; set; } = "";
    public ScreenEdge? Exit { get; set; }
    public string? TargetId { get; set; }
    public ScreenEdge Entry { get; set; } = ScreenEdge.Right;
}

public static class ScreenRouting
{
    public static ScreenEdge Opposite(ScreenEdge side) => side switch
    { ScreenEdge.Left => ScreenEdge.Right, ScreenEdge.Right => ScreenEdge.Left, ScreenEdge.Top => ScreenEdge.Bottom, _ => ScreenEdge.Top };

    public static bool Vertical(ScreenEdge side) => side is ScreenEdge.Left or ScreenEdge.Right;

    public static Point OnEdge(Rectangle screen, ScreenEdge side, int along) => side switch
    {
        ScreenEdge.Left => new(screen.Left, Math.Clamp(along, screen.Top, screen.Bottom - 1)),
        ScreenEdge.Right => new(screen.Right - 1, Math.Clamp(along, screen.Top, screen.Bottom - 1)),
        ScreenEdge.Top => new(Math.Clamp(along, screen.Left, screen.Right - 1), screen.Top),
        _ => new(Math.Clamp(along, screen.Left, screen.Right - 1), screen.Bottom - 1),
    };

    // Find a real exposed part of a side, avoiding a seam with another local monitor.
    public static Point? EntryPoint(DisplayInfo target, ScreenEdge side, double fraction, DisplayInfo[] displays)
    {
        var bounds = target.Bounds;
        var vertical = Vertical(side);
        var start = vertical ? bounds.Top : bounds.Left;
        var end = vertical ? bounds.Bottom - 1 : bounds.Right - 1;
        var wanted = start + (int)Math.Round(Math.Clamp(fraction, 0, 1) * (end - start));
        var candidates = new List<int> { wanted, start, end };
        foreach (var screen in displays)
        {
            candidates.Add((vertical ? screen.Top : screen.Left) - 1);
            candidates.Add(vertical ? screen.Top + screen.Height : screen.Left + screen.Width);
        }
        var layout = new DesktopLayout(displays.Select(d => d.Bounds));
        foreach (var along in candidates.Where(v => v >= start && v <= end).Distinct().OrderBy(v => Math.Abs((long)v - wanted)))
        {
            var point = OnEdge(bounds, side, along);
            if (layout.IsExitEdge(point, side)) return point;
        }
        return null;
    }

    public static double Fraction(Rectangle screen, Point point, ScreenEdge side) => Vertical(side)
        ? (double)(point.Y - screen.Top) / Math.Max(1, screen.Height - 1)
        : (double)(point.X - screen.Left) / Math.Max(1, screen.Width - 1);

    public static bool Crossed(Point previous, Point proposed, Rectangle screen, ScreenEdge entry) => entry switch
    {
        ScreenEdge.Left => previous.X >= screen.Left && proposed.X < screen.Left && previous.Y >= screen.Top && previous.Y < screen.Bottom,
        ScreenEdge.Right => previous.X < screen.Right && proposed.X >= screen.Right && previous.Y >= screen.Top && previous.Y < screen.Bottom,
        ScreenEdge.Top => previous.Y >= screen.Top && proposed.Y < screen.Top && previous.X >= screen.Left && previous.X < screen.Right,
        _ => previous.Y < screen.Bottom && proposed.Y >= screen.Bottom && previous.X >= screen.Left && previous.X < screen.Right,
    };
}
