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
    public bool SupportsExtras { get; init; }

    // Pointer settings, screen sharing and file transfer (only sent to peers that
    // announced SupportsExtras in their Hello).
    public int SpeedLevel { get; init; }
    public string? CursorStyle { get; init; }
    public int CursorSizePercent { get; init; }
    public int Index { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long Size { get; init; }
    public long Total { get; init; }
    public bool IsDirectory { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();

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

    public static WireMessage Hello(string machineName, int screenWidth, int screenHeight, DisplayInfo[]? displays = null, bool supportsSpatialMap = false, bool hasSpatialMap = false, bool supportsExtras = false) => new()
    {
        Type = MessageType.Hello,
        MachineName = machineName,
        ScreenWidth = screenWidth,
        ScreenHeight = screenHeight,
        Displays = displays ?? Array.Empty<DisplayInfo>(),
        SupportsSpatialMap = supportsSpatialMap, HasSpatialMap = hasSpatialMap, SupportsExtras = supportsExtras,
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

    /// <summary>First byte = 1 when the person is already cut out (transparent background); the rest is a WebP image.</summary>
    public static WireMessage CameraFrame(byte[] data) => new() { Type = MessageType.CameraFrame, Data = data };
    public static WireMessage ProbeMsg(string code) => new() { Type = MessageType.Probe, Text = code };
    public static WireMessage Leave() => new() { Type = MessageType.Leave };
    public static WireMessage ChatText(string text) => new() { Type = MessageType.Chat, Text = text };
    public static WireMessage VoiceChunk(byte[] pcm) => new() { Type = MessageType.Voice, Data = pcm };
    public static WireMessage FileListRequest() => new() { Type = MessageType.FileListRequest };
    public static WireMessage FileList(string json) => new() { Type = MessageType.FileList, Text = json };
    public static WireMessage FileCommand(string command) => new() { Type = MessageType.FileCommand, Text = command };
    public static WireMessage Join(string code) => new() { Type = MessageType.Join, Text = code };
    public static WireMessage JoinResult(bool accepted, string? reason = null) => new() { Type = MessageType.JoinResult, Accepted = accepted, Text = reason };
    public static WireMessage Management(bool on) => new() { Type = MessageType.Management, Accepted = on };

    public static WireMessage Heartbeat() => new() { Type = MessageType.Heartbeat };

    public static WireMessage SwitchToRemote() => new() { Type = MessageType.SwitchToRemote };

    public static WireMessage SwitchToLocal() => new() { Type = MessageType.SwitchToLocal };
    public static WireMessage Map(string requestId, ScreenPlacement[] positions) => new() { Type = MessageType.SpatialMap, RequestId = requestId, Placements = positions };
    public static WireMessage MapAck(string requestId, bool accepted, string? error = null) => new() { Type = MessageType.SpatialMapAck, RequestId = requestId, Accepted = accepted, Text = error };

    public static WireMessage Pointer(int speedLevel, string cursorStyle, int cursorSizePercent) => new() { Type = MessageType.PointerSettings, SpeedLevel = speedLevel, CursorStyle = cursorStyle, CursorSizePercent = cursorSizePercent };
    public static WireMessage ScreenStart(int index, int maxWidth, int maxHeight) => new() { Type = MessageType.ScreenStart, Index = index, Width = maxWidth, Height = maxHeight };
    public static WireMessage ScreenStop() => new() { Type = MessageType.ScreenStop };
    public static WireMessage ScreenAck() => new() { Type = MessageType.ScreenAck };
    /// <summary>Left/Top/Width/Height describe the captured monitor in the sender's wire coordinates.</summary>
    public static WireMessage ScreenFrame(int index, int left, int top, int width, int height, byte[] jpeg) => new() { Type = MessageType.ScreenFrame, Index = index, Left = left, Top = top, Width = width, Height = height, Data = jpeg };
    public static WireMessage FileStart(string transferId, string relativePath, long size, long total, bool isDirectory) => new() { Type = MessageType.FileStart, RequestId = transferId, Text = relativePath, Size = size, Total = total, IsDirectory = isDirectory };
    public static WireMessage FileData(string transferId, byte[] data) => new() { Type = MessageType.FileData, RequestId = transferId, Data = data };
    public static WireMessage FileEnd(string transferId) => new() { Type = MessageType.FileEnd, RequestId = transferId };
    public static WireMessage TransferDone(string transferId, bool ok, string? text = null) => new() { Type = MessageType.TransferDone, RequestId = transferId, Accepted = ok, Text = text };

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
                    w.Write((SupportsSpatialMap ? 1 : 0) | (HasSpatialMap ? 2 : 0) | (SupportsExtras ? 4 : 0));
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
            case MessageType.ScreenStop:
            case MessageType.ScreenAck:
                break;
            case MessageType.PointerSettings:
                w.Write(SpeedLevel); w.Write(CursorStyle ?? "system"); w.Write(CursorSizePercent);
                break;
            case MessageType.ScreenStart:
                w.Write(Index); w.Write(Width); w.Write(Height);
                break;
            case MessageType.ScreenFrame:
                w.Write(Index); w.Write(Left); w.Write(Top); w.Write(Width); w.Write(Height); w.Write(Data.Length); w.Write(Data);
                break;
            case MessageType.FileStart:
                w.Write(RequestId ?? ""); w.Write(Text ?? ""); w.Write(Size); w.Write(Total); w.Write(IsDirectory);
                break;
            case MessageType.FileData:
                w.Write(RequestId ?? ""); w.Write(Data.Length); w.Write(Data);
                break;
            case MessageType.FileEnd:
                w.Write(RequestId ?? "");
                break;
            case MessageType.TransferDone:
                w.Write(RequestId ?? ""); w.Write(Accepted); w.Write(Text ?? "");
                break;
            case MessageType.SpatialMap:
                w.Write(RequestId ?? ""); w.Write(Placements.Length);
                foreach (var p in Placements) { w.Write(p.Id); w.Write(p.Remote); w.Write(p.X); w.Write(p.Y); }
                // Optional tail (older decoders ignore it): the screens removed from the map.
                var hiddenPlacements = Placements.Where(p => p.Hidden).ToArray();
                w.Write(hiddenPlacements.Length);
                foreach (var p in hiddenPlacements) { w.Write(p.Id); w.Write(p.Remote); }
                break;
            case MessageType.CameraFrame:
            case MessageType.Voice:
                w.Write(Data.Length); w.Write(Data);
                break;
            case MessageType.Chat:
            case MessageType.Probe:
            case MessageType.FileList:
            case MessageType.FileCommand:
                w.Write(Text ?? "");
                break;
            case MessageType.SpatialMapAck:
                w.Write(RequestId ?? ""); w.Write(Accepted); w.Write(Text ?? "");
                break;
            case MessageType.Join:
                w.Write(Text ?? "");
                break;
            case MessageType.JoinResult:
                w.Write(Accepted); w.Write(Text ?? "");
                break;
            case MessageType.Management:
                w.Write(Accepted);
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
            MessageType.ScreenStop or MessageType.ScreenAck => new WireMessage { Type = type },
            MessageType.CameraFrame or MessageType.Voice => new WireMessage { Type = type, Data = ReadBytes(r) },
            MessageType.Chat or MessageType.Probe or MessageType.FileList or MessageType.FileCommand => new WireMessage { Type = type, Text = r.ReadString() },
            MessageType.Join => new WireMessage { Type = type, Text = r.ReadString() },
            MessageType.JoinResult => new WireMessage { Type = type, Accepted = r.ReadBoolean(), Text = r.ReadString() },
            MessageType.Management => new WireMessage { Type = type, Accepted = r.ReadBoolean() },
            MessageType.PointerSettings => new WireMessage { Type = type, SpeedLevel = r.ReadInt32(), CursorStyle = r.ReadString(), CursorSizePercent = r.ReadInt32() },
            MessageType.ScreenStart => new WireMessage { Type = type, Index = r.ReadInt32(), Width = r.ReadInt32(), Height = r.ReadInt32() },
            MessageType.ScreenFrame => new WireMessage { Type = type, Index = r.ReadInt32(), Left = r.ReadInt32(), Top = r.ReadInt32(), Width = r.ReadInt32(), Height = r.ReadInt32(), Data = ReadBytes(r) },
            MessageType.FileStart => new WireMessage { Type = type, RequestId = r.ReadString(), Text = r.ReadString(), Size = r.ReadInt64(), Total = r.ReadInt64(), IsDirectory = r.ReadBoolean() },
            MessageType.FileData => new WireMessage { Type = type, RequestId = r.ReadString(), Data = ReadBytes(r) },
            MessageType.FileEnd => new WireMessage { Type = type, RequestId = r.ReadString() },
            MessageType.TransferDone => new WireMessage { Type = type, RequestId = r.ReadString(), Accepted = r.ReadBoolean(), Text = r.ReadString() },
            _ => new WireMessage { Type = type }, // a newer peer's message: ignored instead of breaking the link
        };
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Longitud de datos inválida.");
        return reader.ReadBytes(length);
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
        return new WireMessage { Type = MessageType.Hello, MachineName = name, ScreenWidth = width, ScreenHeight = height, Displays = displays.ToArray(), SupportsSpatialMap = (capabilities & 1) != 0, HasSpatialMap = (capabilities & 2) != 0, SupportsExtras = (capabilities & 4) != 0 };
    }

    private static WireMessage ReadMap(BinaryReader reader)
    {
        var request = reader.ReadString(); var count = reader.ReadInt32();
        if (count is < 0 or > 128) throw new InvalidDataException("Número de pantallas inválido en el mapa.");
        var positions = new ScreenPlacement[count];
        for (var n = 0; n < count; n++) positions[n] = new ScreenPlacement(reader.ReadString(), reader.ReadBoolean(), reader.ReadInt32(), reader.ReadInt32());
        if (reader.BaseStream.Length - reader.BaseStream.Position >= 4)
        {
            var hiddenCount = reader.ReadInt32();
            if (hiddenCount is < 0 or > 128) throw new InvalidDataException("Número de pantallas quitadas inválido.");
            for (var n = 0; n < hiddenCount; n++)
            {
                var id = reader.ReadString(); var remote = reader.ReadBoolean();
                var index = Array.FindIndex(positions, p => p.Id == id && p.Remote == remote);
                if (index >= 0) positions[index] = positions[index] with { Hidden = true };
            }
        }
        return new WireMessage { Type = MessageType.SpatialMap, RequestId = request, Placements = positions };
    }
}
