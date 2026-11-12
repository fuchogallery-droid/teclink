using System.Drawing;
using TecLink.Core;
using TecLink.Core.Protocol;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}

// Exact physical layout reported by Windows on jenav (mixed scaling).
var monitors = new[] { new Rectangle(0, 0, 2240, 1400), new Rectangle(-2160, -2160, 3840, 2160) };
var desktop = new DesktopLayout(monitors);
Check(desktop.Bounds == new Rectangle(-2160, -2160, 4400, 3560), "Full desktop includes negative origin");
foreach (var point in new[] { new Point(-1000, -1000), new Point(0, -1000), new Point(1679, -2159) })
    Check(!desktop.IsExitEdge(point, false), $"HDMI interior must remain local: {point}");
Check(desktop.IsExitEdge(new Point(-2160, -1000), false), "Actual left HDMI edge can hand off");
Check(desktop.IsExitEdge(new Point(0, 500), false), "Exposed main-monitor left edge can hand off");
Check(!desktop.IsExitEdge(new Point(2000, -1000), false), "Empty desktop area is not a screen edge");
Check(desktop.ToWire(new Point(-2160, -2160)) == Point.Empty, "Normalize negative top-left");
Check(desktop.FromWire(Point.Empty) == new Point(-2160, -2160), "Restore negative top-left");
Check(desktop.FromWire(new Point(4399, 3559)) == new Point(2239, 1399), "Restore main bottom-right");
var gap = desktop.FromWire(new Point(4360, 1160));
Check(monitors.Any(r => r.Contains(gap)), "Incoming cursor in a gap projects onto a real monitor");

var random = new Random(20261112);
foreach (var screen in monitors)
for (var n = 0; n < 100; n++)
{
    var point = new Point(random.Next(screen.Left, screen.Right), random.Next(screen.Top, screen.Bottom));
    Check(desktop.FromWire(desktop.ToWire(point)) == point, "Round trip preserves physical monitor position");
}

var adjacent = new DesktopLayout(new[] { new Rectangle(-1920, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080) });
Check(!adjacent.IsExitEdge(new Point(0, 500), false), "Left seam between local monitors is not remote edge");
Check(!adjacent.IsExitEdge(new Point(-1, 500), true), "Right seam between local monitors is not remote edge");
Check(adjacent.IsExitEdge(new Point(-1920, 500), false), "Outer left edge is remote edge");
Check(adjacent.IsExitEdge(new Point(1919, 500), true), "Outer right edge is remote edge");

var single = new DesktopLayout(new[] { new Rectangle(0, 0, 1920, 1080) });
Check(single.FromWire(new Point(123, 456)) == new Point(123, 456), "Legacy single-screen coordinates unchanged");
Check(single.IsExitEdge(new Point(0, 500), false), "Legacy left edge still works");
Check(!single.IsExitEdge(new Point(2, 500), false), "Screen interior is not edge");
var hello = WireMessage.Decode(WireMessage.Hello("jenav", desktop.Bounds.Width, desktop.Bounds.Height).Encode());
Check(hello.ScreenWidth == 4400 && hello.ScreenHeight == 3560, "Existing Hello protocol carries virtual dimensions");
var mouse = WireMessage.Decode(WireMessage.MouseMove(2159, 1159).Encode());
Check(mouse.X == 2159 && mouse.Y == 1159, "Mouse protocol remains byte-compatible");
Console.WriteLine($"PASS: {checks} geometry and protocol regression checks");
