namespace TecLink.Core.Protocol;

public enum MessageType : byte
{
    Hello = 1,
    MouseMove = 2,
    MouseButton = 3,
    MouseWheel = 4,
    KeyEvent = 5,
    ClipboardText = 6,
    Heartbeat = 7,
    SwitchToRemote = 8,
    SwitchToLocal = 9,
}

public enum MouseButtonKind : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
}
