using System.Drawing;

namespace TecLink.Core;

/// <summary>Physical monitor rectangles; wire coordinates are relative to Bounds.Location.</summary>
public sealed class DesktopLayout
{
    private readonly Rectangle[] _screens;
    public Rectangle Bounds { get; }

    public DesktopLayout(IEnumerable<Rectangle> screens)
    {
        _screens = screens.ToArray();
        if (_screens.Length == 0 || _screens.Any(r => r.Width <= 0 || r.Height <= 0))
            throw new ArgumentException("Se necesita al menos una pantalla válida.", nameof(screens));
        Bounds = _screens.Aggregate(Rectangle.Union);
    }

    public bool IsExitEdge(Point point, bool right) => IsExitEdge(point, right ? ScreenEdge.Right : ScreenEdge.Left);

    public bool IsExitEdge(Point point, ScreenEdge side)
    {
        foreach (var screen in _screens)
        {
            if (!screen.Contains(point)) continue;
            var along = ScreenRouting.Vertical(side) ? point.Y : point.X;
            if (ScreenRouting.OnEdge(screen, side, along) != point) return false;
            var next = side switch
            {
                ScreenEdge.Left => new Point(point.X - 1, point.Y),
                ScreenEdge.Right => new Point(point.X + 1, point.Y),
                ScreenEdge.Top => new Point(point.X, point.Y - 1),
                _ => new Point(point.X, point.Y + 1),
            };
            return !_screens.Any(r => r.Contains(next));
        }
        return false;
    }

    public Point Clamp(Point point)
    {
        if (_screens.Any(r => r.Contains(point))) return point;
        return _screens.Select(r => new Point(
                Math.Clamp(point.X, r.Left, r.Right - 1),
                Math.Clamp(point.Y, r.Top, r.Bottom - 1)))
            .MinBy(p => Square((long)p.X - point.X) + Square((long)p.Y - point.Y));
    }

    public Point ToWire(Point point)
    {
        point = Clamp(point);
        return new Point(point.X - Bounds.Left, point.Y - Bounds.Top);
    }

    public Point FromWire(Point point) => Clamp(new Point(
        Bounds.Left + Math.Clamp(point.X, 0, Bounds.Width - 1),
        Bounds.Top + Math.Clamp(point.Y, 0, Bounds.Height - 1)));

    private static long Square(long value) => value * value;
}
