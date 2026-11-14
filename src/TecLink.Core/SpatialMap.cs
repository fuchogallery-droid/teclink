using System.Drawing;

namespace TecLink.Core;

/// <summary>Hidden = the screen was removed from the map: it has no routes and is not counted.</summary>
public sealed record ScreenPlacement(string Id, bool Remote, int X, int Y, bool Hidden = false);

public static class SpatialMap
{
    public static List<ScreenRoute> Build(DisplayInfo[] local, DisplayInfo[] remote, IReadOnlyList<ScreenPlacement> allPositions)
    {
        var hidden = allPositions.Where(p => p.Hidden).Select(p => (p.Id, p.Remote)).ToHashSet();
        var positions = allPositions.Where(p => !p.Hidden).ToList();
        var screens = local.Select(d => (Display: d, Remote: false)).Concat(remote.Select(d => (Display: d, Remote: true)))
            .Where(s => !hidden.Contains((s.Display.Id, s.Remote))).ToArray();
        if (positions.Count != screens.Length || positions.Select(p => (p.Id, p.Remote)).Distinct().Count() != positions.Count)
            throw new ArgumentException("Coloca una vez cada pantalla en el mapa.");
        var placed = screens.Select(s =>
        {
            var p = positions.FirstOrDefault(p => p.Id == s.Display.Id && p.Remote == s.Remote)
                ?? throw new ArgumentException("Las pantallas cambiaron. Actualiza el mapa.");
            if (Math.Abs((long)p.X) > 1000000 || Math.Abs((long)p.Y) > 1000000)
                throw new ArgumentException("Acerca las pantallas al centro del mapa.");
            return (s.Display, s.Remote, World: new Rectangle(p.X, p.Y, s.Display.Width, s.Display.Height));
        }).ToArray();
        for (var i = 0; i < placed.Length; i++)
        for (var j = i + 1; j < placed.Length; j++)
            if (placed[i].World.IntersectsWith(placed[j].World))
                throw new ArgumentException("Las pantallas se superponen. Sepáralas o une sus bordes.");

        var routes = new List<ScreenRoute>();
        foreach (var source in placed.Where(s => !s.Remote))
        foreach (var target in placed.Where(s => s.Remote || s.Display.Id != source.Display.Id))
        foreach (var side in Enum.GetValues<ScreenEdge>())
        {
            var a = source.World; var b = target.World;
            var touches = side switch
            {
                ScreenEdge.Left => a.Left == b.Right,
                ScreenEdge.Right => a.Right == b.Left,
                ScreenEdge.Top => a.Top == b.Bottom,
                _ => a.Bottom == b.Top,
            };
            if (!touches) continue;
            var vertical = ScreenRouting.Vertical(side);
            var start = Math.Max(vertical ? a.Top : a.Left, vertical ? b.Top : b.Left);
            var end = Math.Min(vertical ? a.Bottom : a.Right, vertical ? b.Bottom : b.Right);
            if (end <= start) continue; // touching only at a corner is not a route
            var segments = new List<(int Start, int End)> { (start, end) };
            var entry = ScreenRouting.Opposite(side);
            var sourceOrigin = vertical ? a.Top : a.Left; var targetOrigin = vertical ? b.Top : b.Left;
            var sourceLength = Math.Max(1, (vertical ? a.Height : a.Width) - 1);
            var targetLength = Math.Max(1, (vertical ? b.Height : b.Width) - 1);
            foreach (var segment in segments)
                routes.Add(new ScreenRoute
                {
                    SourceId = source.Display.Id, Exit = side, TargetId = target.Display.Id, TargetRemote = target.Remote, Entry = entry,
                    Start = (double)(segment.Start - sourceOrigin) / sourceLength,
                    End = (double)(segment.End - 1 - sourceOrigin) / sourceLength,
                    TargetStart = (double)(segment.Start - targetOrigin) / targetLength,
                    TargetEnd = (double)(segment.End - 1 - targetOrigin) / targetLength,
                });
        }
        return routes;
    }

    private static void SubtractInternalBorders(List<(int Start, int End)> segments, Rectangle source,
        ScreenEdge side, DisplayInfo[] siblings, int worldOrigin)
    {
        var vertical = ScreenRouting.Vertical(side);
        foreach (var sibling in siblings)
        {
            var other = sibling.Bounds;
            if (other == source) continue;
            var blocks = side switch
            {
                ScreenEdge.Left => other.Left <= source.Left - 1 && other.Right > source.Left - 1,
                ScreenEdge.Right => other.Left <= source.Right && other.Right > source.Right,
                ScreenEdge.Top => other.Top <= source.Top - 1 && other.Bottom > source.Top - 1,
                _ => other.Top <= source.Bottom && other.Bottom > source.Bottom,
            };
            if (!blocks) continue;
            var offset = worldOrigin - (vertical ? source.Top : source.Left);
            var first = (vertical ? other.Top : other.Left) + offset;
            var last = (vertical ? other.Bottom : other.Right) + offset;
            var result = new List<(int, int)>();
            foreach (var segment in segments)
            {
                if (last <= segment.Start || first >= segment.End) { result.Add(segment); continue; }
                if (first > segment.Start) result.Add((segment.Start, first));
                if (last < segment.End) result.Add((last, segment.End));
            }
            segments.Clear(); segments.AddRange(result);
        }
    }
}
