using System.Drawing;
using TecLink.Core;
using TecLink.Core.Protocol;
using TecLink.Core.Networking;
using TecLink.App.Native;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Text.Json;
using TecLink.App;

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
var screenInfo = monitors.Select((r, n) => new DisplayInfo("display" + n, r.Left, r.Top, r.Width, r.Height)).ToArray();
var topEntry = ScreenRouting.EntryPoint(screenInfo[0], ScreenEdge.Top, .3, screenInfo);
Check(topEntry is not null && topEntry.Value.X >= 1680 && topEntry.Value.Y == 0, "Entry skips the portion of a side covered by another local monitor");
var joinedInfo = new[] { new DisplayInfo("a", 0, 0, 1920, 1080), new DisplayInfo("b", 1920, 0, 1920, 1080) };
Check(ScreenRouting.EntryPoint(joinedInfo[1], ScreenEdge.Left, .5, joinedInfo) is null, "Fully internal border cannot become a remote entry");
foreach (var side in Enum.GetValues<ScreenEdge>())
{
    var info = new DisplayInfo("single", 0, 0, 1920, 1080);
    var entryPoint = ScreenRouting.EntryPoint(info, side, .5, new[] { info });
    Check(entryPoint is not null && single.IsExitEdge(entryPoint.Value, side), "All four real screen edges support routing");
    Check(ScreenRouting.Opposite(ScreenRouting.Opposite(side)) == side, "Opposite edge is reversible");
}
Check(ScreenRouting.Crossed(new Point(20, 0), new Point(20, -1), new Rectangle(0, 0, 100, 100), ScreenEdge.Top), "Upper-border return is detected");
Check(ScreenRouting.Crossed(new Point(20, 99), new Point(20, 101), new Rectangle(0, 0, 100, 100), ScreenEdge.Bottom), "Lower-border return is detected");
var normalized = screenInfo.Select(d => d with { Left = d.Left - desktop.Bounds.Left, Top = d.Top - desktop.Bounds.Top }).ToArray();
var extendedHello = WireMessage.Hello("jenav", 4400, 3560, normalized);
var decodedHello = WireMessage.Decode(extendedHello.Encode());
Check(decodedHello.Displays.SequenceEqual(normalized), "Optional Hello monitor metadata round trips");
Check(WireMessage.Decode(WireMessage.Hello("old", 1920, 1080).Encode()).Displays.Length == 0, "Old Hello without metadata still decodes");
using (var legacyReader = new BinaryReader(new MemoryStream(extendedHello.Encode())))
{
    Check(legacyReader.ReadByte() == (byte)MessageType.Hello && legacyReader.ReadString() == "jenav" && legacyReader.ReadInt32() == 4400 && legacyReader.ReadInt32() == 3560,
        "Old decoder can read unchanged Hello prefix and ignore the optional tail");
}
var oldConfig = JsonSerializer.Deserialize<AppConfig>("{\"Role\":1,\"PeerHost\":\"192.168.12.183\",\"Port\":51777,\"EdgeToPeer\":0}")!;
Check(oldConfig.AutomaticSwitching && !oldConfig.RoutesConfigured && oldConfig.EdgeToPeer == EdgeDirection.Left, "Old client config keeps automatic left-edge routing");
oldConfig.ScreenOrder = new List<string> { "display1", "display0" };
oldConfig.ScreenRoutes.Add(new ScreenRoute { SourceId = "display1", Exit = ScreenEdge.Top, TargetId = "remote1", Entry = ScreenEdge.Bottom });
var roundTripConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(oldConfig))!;
Check(roundTripConfig.ScreenOrder[0] == "display1" && roundTripConfig.ScreenRoutes[0].Exit == ScreenEdge.Top, "Visual order and per-screen route persist in config");

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
var mapLocal = new[] { new DisplayInfo("a", 0, 0, 1000, 800), new DisplayInfo("hdmi", -1200, 0, 1200, 800) };
var mapRemote = new[] { new DisplayInfo("b", 0, 0, 1000, 800) };
var placements = new[] { new ScreenPlacement("a", false, 0, 0), new ScreenPlacement("hdmi", false, -1200, 0), new ScreenPlacement("b", true, 1000, 0) };
var spatial = SpatialMap.Build(mapLocal, mapRemote, placements);
Check(spatial.Count == 3, "Three monitor map contains local seams and remote exits");
var toPeer = SpatialNavigation.Find(mapLocal[0], new Point(998, 400), new Point(999, 400), spatial, mapLocal, mapRemote);
Check(toPeer is { Route.TargetRemote: true } && toPeer.Position == new Point(1, 400), "Right edge enters peer left with inward offset");
var toHdmi = SpatialNavigation.Find(mapLocal[0], new Point(1, 400), new Point(0, 400), spatial, mapLocal, mapRemote);
Check(toHdmi is { Route.TargetRemote: false } && toHdmi.Position == new Point(-2, 400), "Local HDMI follows virtual map");
var mirror = placements.Select(p => p with { Remote = !p.Remote }).ToArray();
var peerRoutes = SpatialMap.Build(mapRemote, mapLocal, mirror);
Check(peerRoutes.Count == 1 && peerRoutes[0].Exit == ScreenEdge.Left, "Shared map reverses ownership consistently");
var vertical = placements.Select(p => p.Id == "b" ? p with { X = 0, Y = -800 } : p).ToArray();
Check(SpatialMap.Build(mapLocal, mapRemote, vertical).Any(r => r.TargetRemote && r.Exit == ScreenEdge.Top && r.Entry == ScreenEdge.Bottom), "Vertical placement derives top and bottom");
var corner = placements.Select(p => p.Id == "b" ? p with { X = 1000, Y = 800 } : p).ToArray();
Check(!SpatialMap.Build(mapLocal, mapRemote, corner).Any(r => r.TargetRemote), "Corner contact creates no route");
var partial = placements.Select(p => p.Id == "b" ? p with { Y = 400 } : p).ToArray();
var partialRoutes = SpatialMap.Build(mapLocal, mapRemote, partial);
Check(SpatialNavigation.Find(mapLocal[0], new Point(998, 100), new Point(999, 100), partialRoutes, mapLocal, mapRemote) is null, "Noncontact edge segment remains closed");
Check(SpatialNavigation.Find(mapLocal[0], new Point(998, 600), new Point(999, 600), partialRoutes, mapLocal, mapRemote)?.Position == new Point(1, 200), "Partial seam preserves global alignment");
try { SpatialMap.Build(mapLocal, mapRemote, placements.Select(p => p.Id == "b" ? p with { X = 500 } : p).ToArray()); Check(false, "Overlap rejected"); } catch (ArgumentException) { Check(true, "Overlap rejected"); }
try { SpatialMap.Build(mapLocal, mapRemote, placements.Take(2).ToArray()); Check(false, "Missing screen rejected"); } catch (ArgumentException) { Check(true, "Missing screen rejected"); }
var mapWire = WireMessage.Decode(WireMessage.Map("request", placements).Encode());
Check(mapWire.RequestId == "request" && mapWire.Placements.SequenceEqual(placements), "Spatial map protocol round trip");
var ackWire = WireMessage.Decode(WireMessage.MapAck("request", false, "error").Encode());
Check(!ackWire.Accepted && ackWire.Text == "error", "Map rejection acknowledgement round trip");
var capable = WireMessage.Decode(WireMessage.Hello("peer", 1000, 800, mapRemote, true, true).Encode());
Check(capable.SupportsSpatialMap && capable.HasSpatialMap, "Capability negotiation round trip");

// Extras: pointer settings, remote screen and file transfer.
var extrasHello = WireMessage.Decode(WireMessage.Hello("peer", 1000, 800, mapRemote, true, false, true).Encode());
Check(extrasHello.SupportsExtras && extrasHello.SupportsSpatialMap && !extrasHello.HasSpatialMap, "Extras capability travels in Hello without disturbing map flags");
Check(!capable.SupportsExtras, "A 0.4 peer is not assumed to support extras");
Check(WireMessage.Decode(new byte[] { 200, 1, 2, 3 }).Type == (MessageType)200, "Unknown message from a newer peer is ignored, not fatal");
var pointerWire = WireMessage.Decode(WireMessage.Pointer(14, "green", 150).Encode());
Check(pointerWire.SpeedLevel == 14 && pointerWire.CursorStyle == "green" && pointerWire.CursorSizePercent == 150, "Pointer settings round trip");
var startWire = WireMessage.Decode(WireMessage.ScreenStart(1, 1920, 1040).Encode());
Check(startWire.Index == 1 && startWire.Width == 1920 && startWire.Height == 1040, "Screen request round trip");
var pixels = Enumerable.Range(0, 70000).Select(n => (byte)(n * 7)).ToArray();
var frameWire = WireMessage.Decode(WireMessage.ScreenFrame(1, 2160, 0, 2240, 1400, pixels).Encode());
Check(frameWire.Left == 2160 && frameWire.Width == 2240 && frameWire.Height == 1400 && frameWire.Data.SequenceEqual(pixels), "Screen frame round trip keeps geometry and bytes");
try { WireMessage.Decode(WireMessage.FileData("id", pixels).Encode()[..500]); Check(false, "Truncated data rejected"); } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { Check(true, "Truncated data rejected"); }

var speed = new PointerSpeed();
var travelled = 0;
for (var n = 0; n < 1000; n++) travelled += speed.Scale(1, 0, 10).X;
Check(travelled == 1000, "Level 10 leaves raw movement untouched");
speed.Reset(); travelled = 0;
for (var n = 0; n < 1000; n++) travelled += speed.Scale(1, 0, 6).X;
Check(travelled == 500, "Slow level keeps the fractional remainder instead of dropping it");
speed.Reset(); travelled = 0;
for (var n = 0; n < 100; n++) travelled += speed.Scale(-1, 0, 14).X;
Check(travelled == -200, "Fast level scales in both directions");
Check(PointerSpeed.Clamp(0) == 10 && PointerSpeed.Clamp(99) == 20 && PointerSpeed.Factor(20) == 3.5, "Speed level is bounded to the Windows scale");

Check(FileTransfers.SafeRelativePath("Fotos/2026/a.jpg") == Path.Combine("Fotos", "2026", "a.jpg"), "Folder structure is preserved");
foreach (var hostile in new[] { "../fuera.txt", "Fotos/../../fuera.txt", "", "/", ".." })
    try { FileTransfers.SafeRelativePath(hostile); Check(false, "Escaping path rejected: " + hostile); } catch (InvalidDataException) { Check(true, "Escaping path rejected"); }
Check(!Path.IsPathRooted(FileTransfers.SafeRelativePath("C:\\Windows\\x.dll")) && !FileTransfers.SafeRelativePath("C:\\Windows\\x.dll").Contains(':'), "Absolute path is confined below the receive folder");

// A real folder sent over real TCP between two FileTransfers instances.
var work = Path.Combine(Path.GetTempPath(), "teclink-test-" + Guid.NewGuid().ToString("N"));
var outbox = Path.Combine(work, "origen", "Proyecto");
var inbox = Path.Combine(work, "destino");
Directory.CreateDirectory(Path.Combine(outbox, "sub", "vacia"));
var big = new byte[3_000_000]; new Random(7).NextBytes(big);
File.WriteAllBytes(Path.Combine(outbox, "grande.bin"), big);
File.WriteAllText(Path.Combine(outbox, "sub", "nota ñ.txt"), "hola");
File.WriteAllBytes(Path.Combine(outbox, "vacio.dat"), Array.Empty<byte>());
var looseFile = Path.Combine(work, "origen", "suelto.txt");
File.WriteAllText(looseFile, "uno");
try
{
    using var fileListener = new TcpListener(IPAddress.Loopback, 0);
    fileListener.Start();
    using var senderSocket = new TcpClient();
    await senderSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)fileListener.LocalEndpoint).Port);
    var receiverSocket = await fileListener.AcceptTcpClientAsync();
    await using var senderLink = new PeerConnection(senderSocket);
    await using var receiverLink = new PeerConnection(receiverSocket);
    var sender = new FileTransfers(() => senderLink, Path.Combine(work, "no-usado"));
    var receiver = new FileTransfers(() => receiverLink, inbox);
    var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var arrivals = 0;
    receiver.Received += (_, _) => arrivals++;
    sender.Status += text => { if (text.StartsWith("Entregado")) delivered.TrySetResult(text); if (text.StartsWith("La otra PC no pudo") || text.StartsWith("No se pudo")) delivered.TrySetException(new Exception(text)); };
    senderLink.MessageReceived += m => sender.Handle(m);
    receiverLink.MessageReceived += m => receiver.Handle(m);
    senderLink.StartReceiving(); receiverLink.StartReceiving();

    await sender.SendAsync(new[] { outbox, looseFile });
    await delivered.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(File.ReadAllBytes(Path.Combine(inbox, "Proyecto", "grande.bin")).SequenceEqual(big), "Large file arrives byte for byte");
    Check(File.ReadAllText(Path.Combine(inbox, "Proyecto", "sub", "nota ñ.txt")) == "hola", "Nested file with accents arrives intact");
    Check(Directory.Exists(Path.Combine(inbox, "Proyecto", "sub", "vacia")), "Empty folders are recreated");
    Check(new FileInfo(Path.Combine(inbox, "Proyecto", "vacio.dat")).Length == 0, "Empty file is created");
    Check(File.ReadAllText(Path.Combine(inbox, "suelto.txt")) == "uno", "Loose file sent together with a folder arrives");
    Check(arrivals == 1, "Receiver announces the finished transfer once");

    delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    await sender.SendAsync(new[] { outbox });
    await delivered.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Check(File.Exists(Path.Combine(inbox, "Proyecto (2)", "grande.bin")) && File.ReadAllBytes(Path.Combine(inbox, "Proyecto", "grande.bin")).SequenceEqual(big),
        "Sending the same folder again never overwrites the first copy");
}
finally { try { Directory.Delete(work, true); } catch (IOException) { } }


// Session codes, protocol and LAN discovery.
{
    var code = TecLink.Core.Networking.SessionDiscovery.NewCode();
    Check(code.Length == 6 && TecLink.Core.Networking.SessionDiscovery.Normalize(code) == code, "A new session code is 6 valid characters");
    Check(TecLink.Core.Networking.SessionDiscovery.Normalize("k7m-4qx") == "K7M4QX", "Codes accept lowercase and a dash");
    Check(TecLink.Core.Networking.SessionDiscovery.Normalize("K7M4Q") is null && TecLink.Core.Networking.SessionDiscovery.Normalize("K7M4Q0") is null, "Short or ambiguous codes are refused");
    Check(WireMessage.Decode(WireMessage.Join("K7M4QX").Encode()).Text == "K7M4QX", "Join carries the code");
    var refused = WireMessage.Decode(WireMessage.JoinResult(false, "Codigo incorrecto").Encode());
    Check(!refused.Accepted && refused.Text == "Codigo incorrecto", "JoinResult carries the reason");
    Check(WireMessage.Decode(WireMessage.Management(true).Encode()).Accepted, "Management switch travels");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var responder = TecLink.Core.Networking.SessionDiscovery.RunResponderAsync(() => "K7M4QX", 51777, cts.Token);
    var found = await TecLink.Core.Networking.SessionDiscovery.FindAsync("K7M4QX", TimeSpan.FromSeconds(4), cts.Token);
    Check(found is { Port: 51777 }, "Guest finds the host on the LAN with only the code");
    var missing = await TecLink.Core.Networking.SessionDiscovery.FindAsync("ZZZZZZ", TimeSpan.FromSeconds(2), cts.Token);
    Check(missing is null, "A wrong code finds nothing");
    cts.Cancel();
    try { await responder; } catch (OperationCanceledException) { }
}

// Chat, voice, camera, file panel and removing a screen from the map.
{
    Check(WireMessage.Decode(WireMessage.ChatText("Hola equipo").Encode()).Text == "Hola equipo", "Chat text travels");
    var pcm = new byte[] { 1, 2, 3, 4, 250, 251 };
    Check(WireMessage.Decode(WireMessage.VoiceChunk(pcm).Encode()).Data.SequenceEqual(pcm), "Voice PCM travels intact");
    Check(WireMessage.Decode(WireMessage.CameraFrame(pcm).Encode()).Data.SequenceEqual(pcm), "Camera frame travels intact");
    Check(WireMessage.Decode(WireMessage.FileList("[{\"name\":\"a\"}]").Encode()).Text == "[{\"name\":\"a\"}]", "File list travels");
    Check(WireMessage.Decode(WireMessage.FileCommand("desktop:Informe.pdf").Encode()).Text == "desktop:Informe.pdf", "File command travels");
    Check(WireMessage.Decode(WireMessage.FileListRequest().Encode()).Type == MessageType.FileListRequest, "File list request travels");

    var a = new DisplayInfo("A", 0, 0, 1000, 800); var b = new DisplayInfo("B", 0, 0, 1000, 800); var c = new DisplayInfo("C", 0, 0, 1000, 800);
    var all = new[] { new ScreenPlacement("A", false, 0, 0), new ScreenPlacement("B", false, 1000, 0), new ScreenPlacement("C", true, 2000, 0) };
    var routes = SpatialMap.Build(new[] { a, b }, new[] { c }, all);
    Check(routes.Any(r => r.SourceId == "B" && r.TargetId == "C" && r.TargetRemote), "Screens next to each other get a route");
    var withoutB = new[] { all[0], all[1] with { Hidden = true }, all[2] with { X = 1000 } };
    var routes2 = SpatialMap.Build(new[] { a, b }, new[] { c }, withoutB);
    Check(routes2.All(r => r.SourceId != "B" && r.TargetId != "B"), "A removed screen has no routes");
    Check(routes2.Any(r => r.SourceId == "A" && r.TargetId == "C"), "The remaining screens still connect");
    var map = WireMessage.Decode(WireMessage.Map("x", withoutB).Encode());
    Check(map.Placements.Single(p => p.Id == "B").Hidden && !map.Placements.Single(p => p.Id == "A").Hidden, "The removed flag survives the wire");
}

// TecLink folder listing and the desktop copy.
{
    var folder = Path.Combine(Path.GetTempPath(), "teclink-lista-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(folder, "Carpeta")); File.WriteAllText(Path.Combine(folder, "nota.txt"), "hola");
    try
    {
        var transfers = new FileTransfers(() => null, folder);
        var list = transfers.List();
        Check(list.Count == 2 && list.Any(e => e.Name == "nota.txt" && e.Size == 4) && list.Any(e => e.Name == "Carpeta" && e.IsDirectory), "The TecLink folder is listed");
        Check(transfers.PathInside("nota.txt") is not null && transfers.PathInside("..\\x") is null && transfers.PathInside("a/b") is null && transfers.PathInside("") is null, "Only names inside the folder are accepted");
    }
    finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
}
Console.WriteLine($"PASS: {checks} geometry, input, map, TCP, pointer and file-transfer regression checks");
