using System.Text;

namespace TecLink.Core.Protocol;

/// <summary>
/// One protocol message. Only the fields relevant to <see cref="Type"/> are populated.
/// Wire format (payload only, framing/length is handled by FramedStream): [1 byte Type][fields...].
/// </summary>
public sealed class WireMessage
{
    public MessageType Type { get; init; }

    // Hello — the sender's FULL virtual desktop size (all monitors combined, not just
    // the primary one). Only width/height travel on the wire: X/Y coordinates are
    // normalized to each side's own origin by DesktopLayout.ToWire/FromWire before
    // being sent, so the receiver never needs to know the sender's origin.
    public string? MachineName { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public DisplayInfo[] Displays { get; init; } = Array.Empty<DisplayInfo>();
    public bool SupportsSpatialMap { get; init; }
    public bool HasSpatialMap { get; init; }
    public string? RequestId { get; init; }
    public ScreenPlacement[] Placements { get; init; } = Array.Empty<ScreenPlacement>();
    public bool Accepted { get; init; }

    // MouseMove (coordinates relative to the receiver's virtual desktop origin).
    // A single monitor at (0,0) retains the v0.2.0 representation.
    public int X { get; init; }
    public int Y { get; init; }

    // MouseButton
    public MouseButtonKind Button { get; init; }
    public bool IsDown { get; init; }

    // MouseWheel
    public int WheelDelta { get; init; }

    // KeyEvent
    public int VkCode { get; init; }
    public int ScanCode { get; init; }
    public bool IsExtendedKey { get; init; }

    // ClipboardText
    public string? Text { get; init; }

    public static WireMessage Hello(string machineName, int screenWidth, int screenHeight, DisplayInfo[]? displays = null, bool supportsSpatialMap = false, bool hasSpatialMap = false) => new()
    {
        Type = MessageType.Hello,
        MachineName = machineName,
        ScreenWidth = screenWidth,
        ScreenHeight = screenHeight,
        Displays = displays ?? Array.Empty<DisplayInfo>(),
        SupportsSpatialMap = supportsSpatialMap, HasSpatialMap = hasSpatialMap,
    };

    public static WireMessage MouseMove(int x, int y) => new() { Type = MessageType.MouseMove, X = x, Y = y };

    public static WireMessage MouseButtonEvent(MouseButtonKind button, bool isDown) => new()
    {
        Type = MessageType.MouseButton,
        Button = button,
        IsDown = isDown,
    };

    public static WireMessage MouseWheelEvent(int delta) => new() { Type = MessageType.MouseWheel, WheelDelta = delta };

    public static WireMessage KeyEventMsg(int vkCode, int scanCode, bool isDown, bool isExtended) => new()
    {
        Type = MessageType.KeyEvent,
        VkCode = vkCode,
        ScanCode = scanCode,
        IsDown = isDown,
        IsExtendedKey = isExtended,
    };

    public static WireMessage ClipboardTextMsg(string text) => new() { Type = MessageType.ClipboardText, Text = text };

    public static WireMessage Heartbeat() => new() { Type = MessageType.Heartbeat };

    public static WireMessage SwitchToRemote() => new() { Type = MessageType.SwitchToRemote };

    public static WireMessage SwitchToLocal() => new() { Type = MessageType.SwitchToLocal };
    public static WireMessage Map(string requestId, ScreenPlacement[] positions) => new() { Type = MessageType.SpatialMap, RequestId = requestId, Placements = positions };
    public static WireMessage MapAck(string requestId, bool accepted, string? error = null) => new() { Type = MessageType.SpatialMapAck, RequestId = requestId, Accepted = accepted, Text = error };

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write((byte)Type);

        switch (Type)
        {
            case MessageType.Hello:
                w.Write(MachineName ?? "");
                w.Write(ScreenWidth);
                w.Write(ScreenHeight);
                if (Displays.Length > 0)
                {
                    w.Write(0x54434C4D); // optional monitor metadata; older decoders ignore the tail
                    w.Write(Displays.Length);
                    foreach (var display in Displays)
                    {
                        w.Write(display.Id); w.Write(display.Left); w.Write(display.Top);
                        w.Write(display.Width); w.Write(display.Height);
                    }
                    w.Write((SupportsSpatialMap ? 1 : 0) | (HasSpatialMap ? 2 : 0));
                }
                break;
            case MessageType.MouseMove:
                w.Write(X);
                w.Write(Y);
                break;
            case MessageType.MouseButton:
                w.Write((byte)Button);
                w.Write(IsDown);
                break;
            case MessageType.MouseWheel:
                w.Write(WheelDelta);
                break;
            case MessageType.KeyEvent:
                w.Write(VkCode);
                w.Write(ScanCode);
                w.Write(IsDown);
                w.Write(IsExtendedKey);
                break;
            case MessageType.ClipboardText:
                w.Write(Text ?? "");
                break;
            case MessageType.Heartbeat:
            case MessageType.SwitchToRemote:
            case MessageType.SwitchToLocal:
                break;
            case MessageType.SpatialMap:
                w.Write(RequestId ?? ""); w.Write(Placements.Length);
                foreach (var p in Placements) { w.Write(p.Id); w.Write(p.Remote); w.Write(p.X); w.Write(p.Y); }
                break;
            case MessageType.SpatialMapAck:
                w.Write(RequestId ?? ""); w.Write(Accepted); w.Write(Text ?? "");
                break;
        }

        w.Flush();
        return ms.ToArray();
    }

    public static WireMessage Decode(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var type = (MessageType)r.ReadByte();

        return type switch
        {
            MessageType.Hello => ReadHello(r),
            MessageType.MouseMove => new WireMessage { Type = type, X = r.ReadInt32(), Y = r.ReadInt32() },
            MessageType.MouseButton => new WireMessage
            {
                Type = type,
                Button = (MouseButtonKind)r.ReadByte(),
                IsDown = r.ReadBoolean(),
            },
            MessageType.MouseWheel => new WireMessage { Type = type, WheelDelta = r.ReadInt32() },
            MessageType.KeyEvent => new WireMessage
            {
                Type = type,
                VkCode = r.ReadInt32(),
                ScanCode = r.ReadInt32(),
                IsDown = r.ReadBoolean(),
                IsExtendedKey = r.ReadBoolean(),
            },
            MessageType.ClipboardText => new WireMessage { Type = type, Text = r.ReadString() },
            MessageType.Heartbeat => new WireMessage { Type = type },
            MessageType.SwitchToRemote => new WireMessage { Type = type },
            MessageType.SwitchToLocal => new WireMessage { Type = type },
            MessageType.SpatialMap => ReadMap(r),
            MessageType.SpatialMapAck => new WireMessage { Type = type, RequestId = r.ReadString(), Accepted = r.ReadBoolean(), Text = r.ReadString() },
            _ => throw new InvalidDataException($"Unknown message type: {type}"),
        };
    }

    private static WireMessage ReadHello(BinaryReader reader)
    {
        var name = reader.ReadString(); var width = reader.ReadInt32(); var height = reader.ReadInt32();
        var displays = new List<DisplayInfo>();
        var capabilities = 0;
        if (reader.BaseStream.Length - reader.BaseStream.Position >= 8 && reader.ReadInt32() == 0x54434C4D)
        {
            var count = reader.ReadInt32();
            if (count is < 0 or > 64) throw new InvalidDataException("Número de pantallas inválido.");
            for (var n = 0; n < count; n++)
            {
                var display = new DisplayInfo(reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                if (display.Width <= 0 || display.Height <= 0 || display.Left < 0 || display.Top < 0 ||
                    (long)display.Left + display.Width > width || (long)display.Top + display.Height > height)
                    throw new InvalidDataException("Geometría de pantalla inválida.");
                if (displays.Any(d => d.Id == display.Id)) throw new InvalidDataException("Identificador de pantalla repetido.");
                displays.Add(display);
            }
            if (reader.BaseStream.Length - reader.BaseStream.Position >= 4) capabilities = reader.ReadInt32();
        }
        return new WireMessage { Type = MessageType.Hello, MachineName = name, ScreenWidth = width, ScreenHeight = height, Displays = displays.ToArray(), SupportsSpatialMap = (capabilities & 1) != 0, HasSpatialMap = (capabilities & 2) != 0 };
    }

    private static WireMessage ReadMap(BinaryReader reader)
    {
        var request = reader.ReadString(); var count = reader.ReadInt32();
        if (count is < 0 or > 128) throw new InvalidDataException("Número de pantallas inválido en el mapa.");
        var positions = new ScreenPlacement[count];
        for (var n = 0; n < count; n++) positions[n] = new ScreenPlacement(reader.ReadString(), reader.ReadBoolean(), reader.ReadInt32(), reader.ReadInt32());
        return new WireMessage { Type = MessageType.SpatialMap, RequestId = request, Placements = positions };
    }
}
