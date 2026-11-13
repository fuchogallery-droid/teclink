using System.Drawing;
using TecLink.Core;
using TecLink.Core.Protocol;
using TecLink.Core.Networking;
using TecLink.App.Native;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;

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
var pointer = new RemotePointer(new Size(1920, 1080), new Point(500, 500));
for (var n = 0; n < 200; n++) pointer.Advance(1, 0);
Check(pointer.Position == new Point(700, 500), "Hardware deltas accumulate instead of snapping back to entry");
for (var n = 0; n < 100; n++) pointer.Advance(-1, 1);
Check(pointer.Position == new Point(600, 600), "Diagonal direction changes preserve accumulated motion");
Check(pointer.Advance(0, 0) == new Point(600, 600), "Idle packet cannot reset pointer");
pointer.Advance(0, -10000);
Check(pointer.Advance(0, 1).Y == 1, "No invisible vertical overshoot accumulates at desktop boundary");
var boundaryPointer = new RemotePointer(new Size(100, 100), Point.Empty);
Check(boundaryPointer.Advance(-2, 0).X == -2, "Raw crossing keeps horizontal excess for handoff");
Check(boundaryPointer.Advance(1, 0).X == 1, "Stored pointer remains bounded after overshoot");

var physicalKey = KeyboardStroke.Create(0x41, 0x1E, true, false);
Check(physicalKey == new KeyboardStroke(0, 0x1E, 0x8), "Physical key uses scan code, not a conflicting virtual key");
Check(KeyboardStroke.Create(0x41, 0x1E, false, false).Flags == 0xA, "Key release preserves scan-code flag");
Check(KeyboardStroke.Create(0xA3, 0x1D, false, true).Flags == 0xB, "Extended key release preserves all flags");
Check(KeyboardStroke.Create(0xAF, 0, true, false) == new KeyboardStroke(0xAF, 0, 0), "Keys without scan code retain VK fallback");
var released = WireMessage.Decode(WireMessage.KeyEventMsg(0x41, 0x1E, false, false).Encode());
Check(released.ScanCode == 0x1E && !released.IsDown, "Disconnect release can carry original scan code");
Check(Marshal.SizeOf<Win32.INPUT>() == (IntPtr.Size == 8 ? 40 : 28), "Native INPUT layout has correct size");
Check(Marshal.SizeOf<Win32.RAWINPUTHEADER>() == (IntPtr.Size == 8 ? 24 : 16), "Raw Input header layout");
Check(Marshal.SizeOf<Win32.RAWMOUSE>() == 24, "Raw mouse layout size");
Check(Marshal.OffsetOf<Win32.RAWMOUSE>(nameof(Win32.RAWMOUSE.LastX)).ToInt32() == 12, "Raw mouse X offset matches Windows ABI");
Check(Marshal.OffsetOf<Win32.RAWMOUSE>(nameof(Win32.RAWMOUSE.LastY)).ToInt32() == 16, "Raw mouse Y offset matches Windows ABI");

// Real TCP framing and a burst much faster than the display refresh rate.
// Verify latest motion survives while click/key/handoff ordering remains exact.
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
using var receiving = await listener.AcceptTcpClientAsync();
await using var connection = new PeerConnection(client);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
async Task<List<WireMessage>> ReadFrames()
{
    var received = new List<WireMessage>();
    var stream = receiving.GetStream();
    var header = new byte[4];
    while (true)
    {
        await stream.ReadExactlyAsync(header, timeout.Token);
        var frame = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await stream.ReadExactlyAsync(frame, timeout.Token);
        var message = WireMessage.Decode(frame);
        received.Add(message);
        if (message.Type == MessageType.SwitchToLocal) return received;
    }
}
var readFrames = ReadFrames();
var sends = new List<Task>();
for (var n = 0; n < 1000; n++) sends.Add(connection.SendAsync(WireMessage.MouseMove(n, 100)));
sends.Add(connection.SendAsync(WireMessage.KeyEventMsg(0x41, 0x1E, true, false)));
sends.Add(connection.SendAsync(WireMessage.MouseButtonEvent(MouseButtonKind.Left, true)));
for (var n = 1000; n < 2000; n++) sends.Add(connection.SendAsync(WireMessage.MouseMove(n, 200)));
sends.Add(connection.SendAsync(WireMessage.MouseButtonEvent(MouseButtonKind.Left, false)));
sends.Add(connection.SendAsync(WireMessage.KeyEventMsg(0x41, 0x1E, false, false)));
sends.Add(connection.SendAsync(WireMessage.SwitchToLocal()));
await Task.WhenAll(sends).WaitAsync(timeout.Token);
var frames = await readFrames;
var downIndex = frames.FindIndex(m => m.Type == MessageType.KeyEvent && m.IsDown);
var upIndex = frames.FindIndex(m => m.Type == MessageType.KeyEvent && !m.IsDown);
Check(downIndex > 0 && frames[downIndex - 1].X == 999, "Latest pre-key movement delivered before key down");
Check(upIndex > downIndex && frames[upIndex - 2].Type == MessageType.MouseMove && frames[upIndex - 2].X == 1999,
    "Latest drag movement delivered before button/key releases");
Check(frames.Count(m => m.Type == MessageType.KeyEvent) == 2, "Keyboard events never coalesced or lost");
Check(frames.Count(m => m.Type == MessageType.MouseButton) == 2, "Mouse button transitions never coalesced or lost");
Check(frames[^1].Type == MessageType.SwitchToLocal, "No old movement appears after handoff");
var movementFrames = frames.Count(m => m.Type == MessageType.MouseMove);
Check(movementFrames < 1000, "High-rate movement is coalesced rather than backlogged");
Console.WriteLine($"TCP burst: 2000 movement proposals -> {movementFrames} movement frames; all keys/buttons ordered");
Console.WriteLine($"PASS: {checks} geometry, input and TCP regression checks");
