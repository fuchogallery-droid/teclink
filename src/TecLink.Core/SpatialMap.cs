using System.Drawing;

namespace TecLink.Core;

/// <summary>Hidden = the screen was removed from the map: it has no routes and is not counted.</summary>
public sealed record ScreenPlacement(string Id, bool Remote, int X, int Y, bool Hidden = false);

public static class SpatialMap
{
    /// <summary>
    /// Who may remove a screen from the map: ONLY the session host, and ONLY screens that came from a guest.
    /// Nobody removes their own screens; a guest can remove nothing (it keeps what the host decided).
    /// `positions` are from this PC's point of view (Remote = the other PC's screens); `existing` is what is stored now.
    /// </summary>
    public static List<ScreenPlacement> ApplyHiddenRules(IEnumerable<ScreenPlacement> positions, IReadOnlyList<ScreenPlacement> existing, bool isHost, bool fromPeer)
    {
        bool WasHidden(ScreenPlacement p) => existing.Any(e => e.Id == p.Id && e.Remote == p.Remote && e.Hidden);
        return positions.Select(p => isHost
            ? (fromPeer ? p with { Hidden = WasHidden(p) }          // a guest cannot remove anything
                        : p with { Hidden = p.Remote && p.Hidden })   // the host: only guest screens, never its own
            : (fromPeer ? p with { Hidden = !p.Remote && p.Hidden }   // the host's decision about this PC's screens (mirrored: Remote=false is ours)
                        : p with { Hidden = WasHidden(p) }))          // a guest cannot change it
            .ToList();
    }

    /// <summary>
    /// The session owner's map must always be usable: every connected screen placed exactly once and none overlapping.
    /// Keeps what the owner already arranged; a screen that is new goes to the right of everything else
    /// (a guest's monitors keep their own relative arrangement). If the result is still not valid it starts over:
    /// the owner's monitors as Windows has them and the guest's to their right.
    /// </summary>
    public static List<ScreenPlacement> Complete(DisplayInfo[] local, DisplayInfo[] remote, IReadOnlyList<ScreenPlacement> existing)
    {
        void AppendRemote(List<ScreenPlacement> list, bool keepHidden)
        {
            if (remote.Length == 0) return;
            var visible = list.Where(p => !p.Hidden).Select(p => (p, d: (p.Remote ? remote : local).First(d => d.Id == p.Id))).ToArray();
            var right = visible.Length == 0 ? 0 : visible.Max(v => v.p.X + v.d.Width);
            var top = visible.Length == 0 ? 0 : visible.Where(v => v.p.X + v.d.Width == right).Min(v => v.p.Y);
            var originX = remote.Min(d => d.Left); var originY = remote.Min(d => d.Top);
            // First choice: right beside the owner's main monitor (the one at 0,0 in Windows), where people expect it.
            // If something is already there, to the right of everything.
            var main = visible.Where(v => !v.p.Remote).Select(v => (v.p, v.d)).Cast<(ScreenPlacement p, DisplayInfo d)?>()
                .FirstOrDefault(v => v!.Value.d.Left == 0 && v.Value.d.Top == 0) ?? visible.Where(v => !v.p.Remote).Cast<(ScreenPlacement p, DisplayInfo d)?>().FirstOrDefault();
            var at = (X: right, Y: top);
            if (main is { } m)
            {
                var candidate = (X: m.p.X + m.d.Width, Y: m.p.Y);
                bool Free((int X, int Y) c) => remote.All(d => visible.All(v =>
                    !new Rectangle(c.X + d.Left - originX, c.Y + d.Top - originY, d.Width, d.Height).IntersectsWith(new Rectangle(v.p.X, v.p.Y, v.d.Width, v.d.Height))));
                if (Free(candidate)) at = candidate;
            }
            foreach (var d in remote)
                list.Add(new ScreenPlacement(d.Id, true, at.X + d.Left - originX, at.Y + d.Top - originY,
                    keepHidden && existing.Any(e => e.Remote && e.Id == d.Id && e.Hidden)));
        }
        List<ScreenPlacement> Fresh(bool keepHidden)
        {
            var list = local.Select(d => new ScreenPlacement(d.Id, false, d.Left, d.Top)).ToList();
            AppendRemote(list, keepHidden);
            return list;
        }
        bool Valid(List<ScreenPlacement> list)
        {
            try { Build(local, remote, list); return true; } catch (ArgumentException) { return false; }
        }

        var localKept = existing.Where(p => !p.Remote && local.Any(d => d.Id == p.Id)).Select(p => p with { Hidden = false })
            .GroupBy(p => p.Id).Select(g => g.First()).ToList();
        if (localKept.Count != local.Length) return Fresh(keepHidden: true);
        var remoteKept = existing.Where(p => p.Remote && remote.Any(d => d.Id == p.Id)).GroupBy(p => p.Id).Select(g => g.First()).ToList();
        var result = new List<ScreenPlacement>(localKept);
        if (remoteKept.Count == remote.Length) result.AddRange(remoteKept); else AppendRemote(result, keepHidden: true);
        if (Valid(result)) return result;
        var fresh = Fresh(keepHidden: true);
        return Valid(fresh) ? fresh : Fresh(keepHidden: false);
    }

    /// <summary>Which side of this PC's desktop a guest's screens were placed on (null when they touch none of the four sides).</summary>
    public static ScreenEdge? SideOf(DisplayInfo[] local, DisplayInfo[] remote, IReadOnlyList<ScreenPlacement> positions)
    {
        var mine = positions.Where(p => !p.Remote && !p.Hidden).Select(p => (p, d: local.FirstOrDefault(d => d.Id == p.Id))).Where(v => v.d is not null).ToArray();
        var theirs = positions.Where(p => p.Remote && !p.Hidden).Select(p => (p, d: remote.FirstOrDefault(d => d.Id == p.Id))).Where(v => v.d is not null).ToArray();
        if (mine.Length == 0 || theirs.Length == 0) return null;
        var lLeft = mine.Min(v => v.p.X); var lRight = mine.Max(v => v.p.X + v.d!.Width);
        var lTop = mine.Min(v => v.p.Y); var lBottom = mine.Max(v => v.p.Y + v.d!.Height);
        var rLeft = theirs.Min(v => v.p.X); var rRight = theirs.Max(v => v.p.X + v.d!.Width);
        var rTop = theirs.Min(v => v.p.Y); var rBottom = theirs.Max(v => v.p.Y + v.d!.Height);
        if (rLeft >= lRight) return ScreenEdge.Right;
        if (rRight <= lLeft) return ScreenEdge.Left;
        if (rTop >= lBottom) return ScreenEdge.Bottom;
        if (rBottom <= lTop) return ScreenEdge.Top;
        return null;
    }

    /// <summary>This PC's screens where Windows has them and a guest's screens glued to one side of them, so that the mouse can cross there.
    /// The guest keeps its own monitors' arrangement. It is aligned with the owner's monitor that touches that side (its top, or its left).</summary>
    public static List<ScreenPlacement> PlaceBeside(DisplayInfo[] local, DisplayInfo[] remote, ScreenEdge side)
    {
        var list = local.Select(d => new ScreenPlacement(d.Id, false, d.Left, d.Top)).ToList();
        if (remote.Length == 0 || local.Length == 0) return list;
        var left = local.Min(d => d.Left); var top = local.Min(d => d.Top);
        var right = local.Max(d => d.Left + d.Width); var bottom = local.Max(d => d.Top + d.Height);
        var originX = remote.Min(d => d.Left); var originY = remote.Min(d => d.Top);
        var remoteWidth = remote.Max(d => d.Left + d.Width) - originX; var remoteHeight = remote.Max(d => d.Top + d.Height) - originY;
        // the owner's monitor that sits on that side (the first one, if several do)
        var edge = side switch
        {
            ScreenEdge.Right => local.Where(d => d.Left + d.Width == right).OrderBy(d => d.Top).First(),
            ScreenEdge.Left => local.Where(d => d.Left == left).OrderBy(d => d.Top).First(),
            ScreenEdge.Top => local.Where(d => d.Top == top).OrderBy(d => d.Left).First(),
            _ => local.Where(d => d.Top + d.Height == bottom).OrderBy(d => d.Left).First(),
        };
        var (x, y) = side switch
        {
            ScreenEdge.Right => (right, edge.Top),
            ScreenEdge.Left => (left - remoteWidth, edge.Top),
            ScreenEdge.Top => (edge.Left, top - remoteHeight),
            _ => (edge.Left, bottom),
        };
        foreach (var d in remote) list.Add(new ScreenPlacement(d.Id, true, x + d.Left - originX, y + d.Top - originY));
        return list;
    }

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
