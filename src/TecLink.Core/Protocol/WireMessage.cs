using System.Text;

namespace TecLink.Core.Protocol;

/// <summary>
/// One protocol message. Only the fields relevant to <see cref="Type"/> are populated.
/// Wire format (payload only, framing/length is handled by FramedStream): [1 byte Type][fields...].
/// </summary>
public sealed class WireMessage
{
    public MessageType Type { get; init; }

    // Hello — describes the sender's FULL virtual desktop (all monitors combined),
    // not just the primary one. Left/Top are the virtual-desktop origin, which is
    // frequently negative or non-zero once more than one monitor is involved (Windows
    // places the primary monitor at (0,0) and everything else relative to it).
    public string? MachineName { get; init; }
    public int ScreenLeft { get; init; }
    public int ScreenTop { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }

    // MouseMove (absolute coordinates in the receiver's screen space)
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

    public static WireMessage Hello(string machineName, int screenLeft, int screenTop, int screenWidth, int screenHeight) => new()
    {
        Type = MessageType.Hello,
        MachineName = machineName,
        ScreenLeft = screenLeft,
        ScreenTop = screenTop,
        ScreenWidth = screenWidth,
        ScreenHeight = screenHeight,
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

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write((byte)Type);

        switch (Type)
        {
            case MessageType.Hello:
                w.Write(MachineName ?? "");
                w.Write(ScreenLeft);
                w.Write(ScreenTop);
                w.Write(ScreenWidth);
                w.Write(ScreenHeight);
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
            MessageType.Hello => new WireMessage
            {
                Type = type,
                MachineName = r.ReadString(),
                ScreenLeft = r.ReadInt32(),
                ScreenTop = r.ReadInt32(),
                ScreenWidth = r.ReadInt32(),
                ScreenHeight = r.ReadInt32(),
            },
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
            _ => throw new InvalidDataException($"Unknown message type: {type}"),
        };
    }
}
