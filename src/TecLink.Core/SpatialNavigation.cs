using System.Drawing;

namespace TecLink.Core;

public sealed record ScreenTransition(ScreenRoute Route, DisplayInfo Target, Point Position);
public static class SpatialNavigation
{
    /// <summary>The cursor lands this many pixels inside the new screen, not on its very edge: on the edge the smallest jitter
    /// of the hand would send it straight back and the two PCs would keep swapping control.</summary>
    public const int LandingInset = 24;

    public static ScreenTransition? Find(DisplayInfo source, Point previous, Point candidate,
        IEnumerable<ScreenRoute> routes, DisplayInfo[] sameHost, DisplayInfo[] otherHost)
    {
        foreach (var route in routes.Where(r => r.SourceId == source.Id && r.Exit is not null))
        {
            var side = route.Exit!.Value;
            var along = ScreenRouting.Vertical(side) ? candidate.Y : candidate.X;
            if (ScreenRouting.OnEdge(source.Bounds, side, along) != candidate &&
                !ScreenRouting.Crossed(previous, candidate, source.Bounds, side)) continue;
            var fraction = Math.Clamp(ScreenRouting.Fraction(source.Bounds, candidate, side), 0, 1);
            if (fraction < route.Start - 1e-8 || fraction > route.End + 1e-8) continue;
            var target = (route.TargetRemote ? otherHost : sameHost).FirstOrDefault(d => d.Id == route.TargetId);
            if (target is null) continue;
            var u = (fraction - route.Start) / Math.Max(1e-8, route.End - route.Start);
            var targetFraction = route.TargetStart + u * (route.TargetEnd - route.TargetStart);
            var targetAlong = ScreenRouting.Vertical(route.Entry)
                ? target.Top + (int)Math.Round(targetFraction * (target.Height - 1))
                : target.Left + (int)Math.Round(targetFraction * (target.Width - 1));
            var position = ScreenRouting.OnEdge(target.Bounds, route.Entry, targetAlong);
            var travelled = side switch
            {
                ScreenEdge.Left => Math.Max(1, source.Left - candidate.X),
                ScreenEdge.Right => Math.Max(1, candidate.X - (source.Left + source.Width - 1)),
                ScreenEdge.Top => Math.Max(1, source.Top - candidate.Y),
                _ => Math.Max(1, candidate.Y - (source.Top + source.Height - 1)),
            };
            var excess = route.TargetRemote ? Math.Max(LandingInset, travelled) : travelled; // only between PCs; a seam between this PC's own monitors stays continuous
            position = route.Entry switch
            {
                ScreenEdge.Left => position with { X = position.X + excess },
                ScreenEdge.Right => position with { X = position.X - excess },
                ScreenEdge.Top => position with { Y = position.Y + excess },
                _ => position with { Y = position.Y - excess },
            };
            position = new Point(Math.Clamp(position.X, target.Left, target.Left + target.Width - 1),
                Math.Clamp(position.Y, target.Top, target.Top + target.Height - 1));
            return new ScreenTransition(route, target, position);
        }
        return null;
    }
}
