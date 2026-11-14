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
    SpatialMap = 10,
    SpatialMapAck = 11,
    PointerSettings = 12,
    ScreenStart = 13,
    ScreenStop = 14,
    ScreenFrame = 15,
    ScreenAck = 16,
    FileStart = 17,
    FileData = 18,
    FileEnd = 19,
    TransferDone = 20,
    Join = 21,         // guest -> host: session code
    JoinResult = 22,   // host -> guest: accepted or refused (+ reason)
    Management = 23,   // host -> guest: screens may be viewed/controlled (Accepted = on)
    CameraFrame = 24,  // video-call bubble: one frame of the sender's camera
    FileListRequest = 25, // ask the peer for the files in its TecLink folder
    FileList = 26,        // JSON list of that folder
    FileCommand = 27,     // "fetch:name" (send it to me) or "desktop:name" (put it on your desktop)
    Chat = 28,            // live chat text
    Voice = 29,           // microphone audio: 16 kHz mono 16-bit PCM
    Probe = 31,           // guest -> host: 'do you have this code?' (answered with JoinResult, never admitted)
    Yield = 32,           // "let go of my PC": the receiver leaves Remote mode (no control changes hands)
    ScreenRemoval = 33,   // host <-> guest: "ask:<id>" / "ok:<id>" / "no:<id>" (a guest must consent before its screen is removed)
    ScreenWatch = 34,     // "<maxWidth>|<index>,<index>" = send me small live pictures of those monitors; empty list = stop
    ScreenTile = 35,      // one small live picture of a monitor (Index, Width/Height of the real monitor, JPEG)
    ShareOffer = 36,      // JSON {id,name,size,isDirectory}: a file was shared with the session
    ShareFetch = 37,      // "<id>": send me that shared file now
    Leave = 30,          // the sender is leaving the session on purpose (not a dropped link)
}

public enum MouseButtonKind : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
}
