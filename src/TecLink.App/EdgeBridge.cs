using TecLink.App.Input;
using TecLink.App.Native;
using TecLink.Core.Networking;
using TecLink.Core.Protocol;
using TecLink.Core;
using Microsoft.Win32;

namespace TecLink.App;

public enum BridgeMode { Local, Remote }

/// <summary>
/// Ties the local hooks, the peer connection and the clipboard watcher together and
/// implements the control-handoff state machine. Control can change hands two ways:
/// crossing the configured screen edge with the mouse (continuous, position-based), or
/// pressing the global hotkey (Ctrl+Alt+Space) to jump straight to the center of the
/// other screen. Either side can always take control back with the hotkey, even while
/// the peer currently holds it — see <see cref="OnMessageReceived"/>'s handling of
/// <see cref="MessageType.SwitchToRemote"/> for how that handoff is negotiated.
/// </summary>
public sealed class EdgeBridge : IDisposable
{
    private readonly AppConfig _config;
    private readonly MouseHook _mouseHook = new();
    private readonly KeyboardHook _keyboardHook = new();
    private readonly RawMouseInput _rawMouse = new();
    private readonly ClipboardWatcher _clipboard;
    private volatile DesktopLayout _desktop;

    private PeerConnection? _connection;
    private int _remoteWidth;
    private int _remoteHeight;
    private bool _remoteKnown;
    private volatile bool _peerControlling;

    private BridgeMode _mode = BridgeMode.Local;
    private int _anchorX, _anchorY;
    private int _entryX, _entryY;
    private RemotePointer? _remotePointer;
    private string _remoteName = "Otra PC";
    private DisplayInfo[] _remoteDisplays = Array.Empty<DisplayInfo>();
    private (DisplayInfo Source, ScreenEdge Exit, DisplayInfo Target, ScreenEdge Entry)? _activeRoute;
    private List<ScreenRoute> _spatialLocal = new(), _spatialRemote = new();
    private bool _spatialValid, _peerMapSupported;
    private Point? _lastLocalPoint;
    private readonly Dictionary<string, TaskCompletionSource<WireMessage>> _mapRequests = new();

    // Extras negotiated in Hello: pointer settings, remote screen and file transfer.
    private volatile bool _peerExtras;
    private bool _viewInput;
    private readonly PointerSpeed _speed = new();
    private readonly ScreenShareHost _screenHost = new();
    public FileTransfers Files { get; }

    /// <summary>Raised on the network thread with each frame of the peer's screen.</summary>
    public event Action<WireMessage>? ScreenFrameReceived;
    public event Action? ViewerCloseRequested;

    /// <summary>Raised on the network thread with each camera frame (video-call bubble) of the peer.</summary>
    public event Action<byte[]>? CameraFrameReceived;
    private Task _cameraSend = Task.CompletedTask;

    /// <summary>Live chat and voice. Both arrive on the network thread.</summary>
    public event Action<string, string>? ChatReceived;   // (sender name, text)
    public event Action<byte[]>? VoiceReceived;
    private int _voicePending;

    public bool SendChat(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || _connection is not { } conn || !_remoteKnown) return false;
        _ = conn.SendAsync(WireMessage.ChatText(text.Length > 2000 ? text[..2000] : text));
        return true;
    }

    /// <summary>Voice is sent as it is captured; if the link stalls, old audio is dropped so the call never lags behind.</summary>
    public void SendVoice(byte[] pcm)
    {
        if (_connection is not { } conn || !_remoteKnown || pcm.Length == 0 || pcm.Length > 64 * 1024) return;
        if (Volatile.Read(ref _voicePending) > 6) return;
        Interlocked.Increment(ref _voicePending);
        _ = conn.SendAsync(WireMessage.VoiceChunk(pcm)).ContinueWith(_ => Interlocked.Decrement(ref _voicePending));
    }

    /// <summary>Raised with the JSON list of files the peer holds in its TecLink folder.</summary>
    public event Action<string>? PeerFilesReceived;
    public static readonly System.Text.Json.JsonSerializerOptions PeerFilesJson = new(System.Text.Json.JsonSerializerDefaults.Web);
    public void RequestPeerFiles() { if (CanUseExtras) _ = _connection!.SendAsync(WireMessage.FileListRequest()); }
    public void PeerFileCommand(string verb, string name)
    {
        if (!CanUseExtras || verb is not ("fetch" or "desktop") || string.IsNullOrEmpty(name)) return;
        _ = _connection!.SendAsync(WireMessage.FileCommand(verb + ":" + name));
    }

    /// <summary>Sends one camera frame; frames are dropped while the previous one is still going out.</summary>
    public void SendCameraFrame(byte[] frame)
    {
        if (_connection is not { } conn || !_remoteKnown || frame.Length == 0 || frame.Length > 512 * 1024) return;
        if (!_cameraSend.IsCompleted) return;
        _cameraSend = conn.SendAsync(WireMessage.CameraFrame(frame));
    }

    // Tracks keys/buttons we've injected on THIS machine because the peer is controlling
    // us, so a dropped connection (or the peer handing control back) mid-keypress doesn't
    // leave them stuck down.
    private readonly Dictionary<int, (int ScanCode, bool Extended)> _heldKeys = new();
    private readonly HashSet<MouseButtonKind> _heldButtons = new();

    // The mirror image: keys/buttons WE'VE forwarded to the peer while controlling them,
    // so handing control back (voluntarily or because the peer took it) releases them on
    // the peer's end instead of leaving them stuck there.
    private readonly Dictionary<int, (int ScanCode, bool Extended)> _sentHeldKeys = new();
    private readonly HashSet<MouseButtonKind> _sentHeldButtons = new();

    /// <summary>Master power switch state (tray menu / panel). Off = no connection, no hotkey handoff.</summary>
    public bool Powered { get; set; } = true;

    public event Action<string>? StatusChanged;

    /// <summary>Fires only on an actual control handoff (not on every connection status
    /// update) — meant for a brief, user-visible notice (tray balloon).</summary>
    public event Action<string>? ControlChanged;

    public EdgeBridge(AppConfig config, ClipboardWatcher clipboard)
    {
        _config = config;
        _clipboard = clipboard;

        _desktop = CaptureDesktop();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Files = new FileTransfers(() => _peerExtras ? _connection : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TecLink"));
        Files.Status += text => StatusChanged?.Invoke(text);
        Files.Received += (items, folder) => ControlChanged?.Invoke($"Recibido de la otra PC: {items} archivo(s) en Descargas\\TecLink");
        CursorStyler.Apply(_config.CursorStyle, _config.CursorSizePercent);
        ApplyWindowsSpeed();

        _mouseHook.Move += OnMouseMove;
        _rawMouse.Move += OnRawMouseMove;
        _mouseHook.ButtonChanged += OnMouseButton;
        _mouseHook.Wheel += OnMouseWheel;
        _keyboardHook.KeyEvent += OnKeyEvent;
        _keyboardHook.HotkeyToggle += ToggleControl;
        _clipboard.TextCopied += OnLocalClipboardCopied;
    }

    public void InstallHooks()
    {
        _rawMouse.Install();
        _mouseHook.Install();
        _keyboardHook.Install();
    }

    // ---- Sessions: invitation code, 10-screen limit and the "management" switch ----
    public const int MaxScreens = 10;
    private volatile bool _joined;        // host: the guest sent the right code
    private volatile bool _sessionMode;   // this link was made with a code (not a legacy direct IP)
    private volatile bool _management;    // host's switch: may the screens be viewed/controlled?
    private volatile bool _joinAnswered;
    private string _joinNotice = "";
    private Action? _dropCurrent;

    /// <summary>Without a code-based session (legacy direct link) nothing is restricted.</summary>
    public bool ManagementAllowed => !_sessionMode || _management;
    public string SessionCode => _config.SessionCode;

    public void StartHosting()
    {
        _config.Role = PeerRole.Server; _config.JoinCode = ""; _joinNotice = "";
        _config.Save();
    }

    public void JoinSession(string? code)
    {
        var clean = SessionDiscovery.Normalize(code) ?? throw new ArgumentException("El código tiene 6 letras o números. Revísalo.");
        _config.Role = PeerRole.Client; _config.JoinCode = clean; _joinNotice = "";
        _config.Save();
    }

    /// <summary>Host only: lets both PCs see and control each other's screens, or closes that again.</summary>
    public void SetManagement(bool on)
    {
        if (_config.Role != PeerRole.Server || on == _management) return;
        _management = on;
        if (_connection is { } conn) _ = conn.SendAsync(WireMessage.Management(on));
        if (!on) RevokeManagement();
        StatusChanged?.Invoke(on ? "Gestión de pantallas activada" : "Gestión de pantallas apagada");
    }

    private void RevokeManagement()
    {
        _clipboard.BeginInvoke(() =>
        {
            if (_viewInput) { _viewInput = false; _keyboardHook.Swallow = false; ReleaseAllRemoteHeldInput(); }
            _peerControlling = false;
            SwitchToLocal(force: true);
            ReleaseAllInjectedInput();
            _screenHost.Stop();
            ViewerCloseRequested?.Invoke();
        });
    }

    private void HandleJoin(WireMessage msg)
    {
        if (_connection is not { } conn) return;
        if (SessionDiscovery.Normalize(msg.Text) != SessionDiscovery.Normalize(_config.SessionCode))
        {
            _ = conn.SendAsync(WireMessage.JoinResult(false, "Código incorrecto"));
            _ = Task.Delay(400).ContinueWith(_ => _dropCurrent?.Invoke());
            return;
        }
        _joined = true; _sessionMode = true;
        _ = conn.SendAsync(WireMessage.JoinResult(true));
        _ = conn.SendAsync(WireMessage.Management(_management));
        _ = SendDesktopAsync(conn);
        StatusChanged?.Invoke("Invitado conectado — enviando identificación...");
    }

    private void Reject(string reason)
    {
        if (_connection is { } conn) _ = conn.SendAsync(WireMessage.JoinResult(false, reason));
        _ = Task.Delay(400).ContinueWith(_ => _dropCurrent?.Invoke());
        StatusChanged?.Invoke(reason);
    }

    public async Task RunNetworkingAsync(CancellationToken ct)
    {
        if (_config.Role == PeerRole.Server)
            _ = SessionDiscovery.RunResponderAsync(() => SessionDiscovery.Normalize(_config.SessionCode) ?? "", _config.Port, ct);
        while (!ct.IsCancellationRequested)
        {
            PeerConnection connection;
            var guestCode = _config.Role == PeerRole.Client ? SessionDiscovery.Normalize(_config.JoinCode) : null;
            try
            {
                if (_config.Role == PeerRole.Server) StatusChanged?.Invoke($"Sesión abierta — código {SessionDiscovery.Pretty(_config.SessionCode)}");
                else StatusChanged?.Invoke(guestCode is not null ? "Buscando la sesión en tu red..." : $"Conectando a {_config.PeerHost}:{_config.Port}...");

                if (_config.Role == PeerRole.Server) connection = await PeerServer.AcceptOneAsync(_config.Port, ct);
                else if (guestCode is not null)
                {
                    var host = await SessionDiscovery.FindAsync(guestCode, TimeSpan.FromSeconds(5), ct);
                    if (host is null)
                    {
                        _joinNotice = "No encontré esa sesión. Revisa el código y que la otra PC esté abierta en la misma red.";
                        StatusChanged?.Invoke(_joinNotice);
                        await Task.Delay(2500, ct);
                        continue;
                    }
                    connection = await PeerServer.ConnectAsync(host.Address.ToString(), host.Port, ct);
                }
                else connection = await PeerServer.ConnectAsync(_config.PeerHost, _config.Port, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Error de red: {ex.Message}. Reintentando en 3s...");
                await Task.Delay(3000, ct);
                continue;
            }

            _connection = connection;
            _remoteKnown = false;
            _joined = false; _joinAnswered = false;
            if (_config.Role == PeerRole.Client) _management = false; // a host's switch survives guests coming and going
            _sessionMode = _config.Role == PeerRole.Server || guestCode is not null;
            var disconnectedTcs = new TaskCompletionSource();
            _dropCurrent = () => disconnectedTcs.TrySetResult();

            connection.MessageReceived += OnMessageReceived;
            connection.Disconnected += () => disconnectedTcs.TrySetResult();
            connection.StartReceiving();

            if (_config.Role == PeerRole.Client)
            {
                StatusChanged?.Invoke("Conectado — enviando identificación...");
                if (guestCode is not null)
                {
                    await connection.SendAsync(WireMessage.Join(guestCode));
                    // An older host never answers: treat the link as a legacy direct one.
                    _ = Task.Delay(2500).ContinueWith(_ => { if (_connection == connection && !_joinAnswered) _sessionMode = false; });
                }
                await SendDesktopAsync(connection);
            }
            else
            {
                StatusChanged?.Invoke("Alguien se está conectando...");
                // The guest must send the code right away.
                _ = Task.Delay(5000).ContinueWith(_ => { if (_connection == connection && !_joined) _dropCurrent?.Invoke(); });
            }

            await Task.WhenAny(disconnectedTcs.Task, Task.Delay(Timeout.Infinite, ct));
            _dropCurrent = null;
            _sessionMode = false; _joined = false;
            if (_config.Role == PeerRole.Client) _management = false;

            StatusChanged?.Invoke(ct.IsCancellationRequested ? "TecLink apagado" : "Conexión perdida. Reintentando...");
            _connection = null;
            _remoteKnown = false;
            _peerControlling = false;
            _peerExtras = false;
            _screenHost.Stop();
            Files.Abort();
            _clipboard.BeginInvoke(() =>
            {
                _viewInput = false;
                SwitchToLocal(force: true);
                ReleaseAllInjectedInput();
                ViewerCloseRequested?.Invoke();
            });
            await connection.DisposeAsync();

            if (ct.IsCancellationRequested) return;
            await Task.Delay(string.IsNullOrEmpty(_joinNotice) ? 2000 : 5000, ct); // refused: don't hammer the host
        }
    }

    private void OnMessageReceived(WireMessage msg)
    {
        if (_clipboard.IsDisposed) return;
        // A host talks to nobody who has not shown the invitation code.
        if (msg.Type == MessageType.Join) { if (_config.Role == PeerRole.Server) HandleJoin(msg); return; }
        if (_config.Role == PeerRole.Server && !_joined) return;
        // Bulk data stays on the network thread: writing to disk here is what
        // throttles the sender, and frames are decoded off the UI thread.
        if (Files.Handle(msg)) return;
        if (msg.Type == MessageType.CameraFrame) { CameraFrameReceived?.Invoke(msg.Data); return; }
        if (msg.Type == MessageType.Voice) { VoiceReceived?.Invoke(msg.Data); return; }
        if (msg.Type == MessageType.Chat)
        {
            if (!string.IsNullOrWhiteSpace(msg.Text)) ChatReceived?.Invoke(_remoteName, msg.Text!.Length > 2000 ? msg.Text[..2000] : msg.Text);
            return;
        }
        if (msg.Type == MessageType.FileListRequest)
        {
            _ = _connection?.SendAsync(WireMessage.FileList(System.Text.Json.JsonSerializer.Serialize(Files.List(), PeerFilesJson)));
            return;
        }
        if (msg.Type == MessageType.FileList) { PeerFilesReceived?.Invoke(msg.Text ?? "[]"); return; }
        if (msg.Type == MessageType.FileCommand) { Files.RunCommand(msg.Text ?? ""); return; }
        if (msg.Type == MessageType.ScreenFrame) { ScreenFrameReceived?.Invoke(msg); return; }
        if (msg.Type == MessageType.ScreenAck) { _screenHost.Ack(); return; }
        if (_clipboard.InvokeRequired)
        {
            _clipboard.BeginInvoke(() => OnMessageReceived(msg));
            return;
        }
        switch (msg.Type)
        {
            case MessageType.Hello:
                if (msg.ScreenWidth <= 0 || msg.ScreenHeight <= 0) break;
                _remoteWidth = msg.ScreenWidth;
                _remoteHeight = msg.ScreenHeight;
                _remoteKnown = true;
                _remoteName = msg.MachineName ?? "Otra PC";
                _remoteDisplays = msg.Displays.Length > 0 ? msg.Displays
                    : new[] { new DisplayInfo("desktop", 0, 0, _remoteWidth, _remoteHeight) };
                _peerMapSupported = msg.SupportsSpatialMap;
                _peerExtras = msg.SupportsExtras;
                if (_config.Role == PeerRole.Server && LocalDisplays().Length + _remoteDisplays.Length > MaxScreens)
                {
                    _remoteKnown = false;
                    Reject($"Límite de {MaxScreens} pantallas por sesión: esta sesión ya tiene {LocalDisplays().Length} y la PC invitada trae {_remoteDisplays.Length}.");
                    break;
                }
                if (_peerExtras && _config.Role == PeerRole.Server)
                    _ = _connection!.SendAsync(WireMessage.Pointer(_config.PointerSpeedLevel, _config.CursorStyle, _config.CursorSizePercent));
                RefreshSpatialRoutes();
                if (_peerMapSupported && _config.UseSpatialMap && _spatialValid &&
                    (_config.Role == PeerRole.Server || !msg.HasSpatialMap))
                    _ = _connection!.SendAsync(WireMessage.Map(Guid.NewGuid().ToString("N"), _config.ScreenPositions.ToArray()));
                StatusChanged?.Invoke($"Vinculado con {msg.MachineName} ({_remoteWidth}x{_remoteHeight})");
                break;
            case MessageType.JoinResult:
                _joinAnswered = true;
                if (msg.Accepted) { _joinNotice = ""; _sessionMode = true; }
                else
                {
                    _joinNotice = string.IsNullOrWhiteSpace(msg.Text) ? "La sesión no te aceptó." : msg.Text!;
                    StatusChanged?.Invoke(_joinNotice);
                    _dropCurrent?.Invoke();
                }
                break;
            case MessageType.Management:
                if (_config.Role != PeerRole.Client || !_sessionMode) break;
                _management = msg.Accepted;
                if (!_management) RevokeManagement();
                StatusChanged?.Invoke(_management ? "El anfitrión activó la gestión de pantallas" : "Gestión de pantallas apagada");
                break;
            case MessageType.MouseMove:
                if (!ManagementAllowed) break;
                var localPoint = _desktop.FromWire(new Point(msg.X, msg.Y));
                InputInjector.MoveTo(localPoint.X, localPoint.Y);
                break;
            case MessageType.MouseButton:
                if (!ManagementAllowed) break;
                if (msg.IsDown) _heldButtons.Add(msg.Button); else _heldButtons.Remove(msg.Button);
                InputInjector.Button(msg.Button, msg.IsDown);
                break;
            case MessageType.MouseWheel:
                if (!ManagementAllowed) break;
                InputInjector.Wheel(msg.WheelDelta);
                break;
            case MessageType.KeyEvent:
                if (!ManagementAllowed) break;
                if (msg.IsDown) _heldKeys[msg.VkCode] = (msg.ScanCode, msg.IsExtendedKey); else _heldKeys.Remove(msg.VkCode);
                if (!InputInjector.Key(msg.VkCode, msg.IsDown, msg.IsExtendedKey, msg.ScanCode))
                    StatusChanged?.Invoke("Windows rechazó entrada de teclado; revisa error.log");
                break;
            case MessageType.ClipboardText:
                _clipboard.ApplyRemoteText(msg.Text ?? "");
                break;
            case MessageType.SwitchToRemote:
                if (!ManagementAllowed) break;
                _lastLocalPoint = null;
                // The peer just started controlling us. If we were ALSO currently
                // controlling them (both pressed the hotkey near-simultaneously), we
                // yield — whoever's "take control" message arrives wins, the other side
                // backs off automatically instead of both ends fighting over input.
                if (_mode == BridgeMode.Remote) SwitchToLocal();
                if (_viewInput)
                {
                    _viewInput = false;
                    _keyboardHook.Swallow = false;
                    ReleaseAllRemoteHeldInput();
                }
                _peerControlling = true;
                StatusChanged?.Invoke("La otra PC tomó el mando");
                ControlChanged?.Invoke("La otra PC tiene el mando");
                break;
            case MessageType.SwitchToLocal:
                _lastLocalPoint = null;
                _peerControlling = false;
                ReleaseAllInjectedInput(); // defensive: peer says it's done, make sure nothing stayed held here
                StatusChanged?.Invoke("La otra PC soltó el mando");
                ControlChanged?.Invoke("Tienes el mando local");
                break;
            case MessageType.SpatialMap:
                if (!_peerMapSupported) break;
                try
                {
                    StoreSpatialMap(msg.Placements.Select(p => p with { Remote = !p.Remote }).ToList(),
                        _config.AutomaticSwitching, _config.Theme);
                    _ = _connection?.SendAsync(WireMessage.MapAck(msg.RequestId ?? "", true));
                    StatusChanged?.Invoke("Mapa recibido de la otra PC y guardado.");
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                { _ = _connection?.SendAsync(WireMessage.MapAck(msg.RequestId ?? "", false, ex.Message)); }
                break;
            case MessageType.SpatialMapAck:
                if (_mapRequests.TryGetValue(msg.RequestId ?? "", out var request)) request.TrySetResult(msg);
                break;
            case MessageType.PointerSettings:
                try { ApplyPointerSettings(msg.SpeedLevel, msg.CursorStyle, msg.CursorSizePercent, fromPeer: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
                break;
            case MessageType.ScreenStart:
                if (ManagementAllowed && _connection is { } viewer)
                {
                    _screenHost.Start(viewer, msg.Index, msg.Width, msg.Height);
                    ControlChanged?.Invoke("La otra PC está viendo esta pantalla");
                }
                break;
            case MessageType.ScreenStop:
                _screenHost.Stop();
                break;
        }
    }

    private void OnLocalClipboardCopied(string text)
    {
        if (_connection is { } conn)
            _ = conn.SendAsync(WireMessage.ClipboardTextMsg(text));
    }

    private void OnMouseMove(HookMouseEventArgs e)
    {
        if (_viewInput) return; // the remote-screen window owns the mouse; edges stay closed
        if (_mode == BridgeMode.Local)
        {
            if (_config.UseSpatialMap && _spatialValid && _connection is not null && _remoteKnown && !_peerControlling)
            {
                HandleSpatialLocalMove(e);
                return;
            }
            if (_config.UseSpatialMap) return; // An unavailable map must not reactivate legacy exits.
            if (_connection is null || !_remoteKnown || _peerControlling || !_config.AutomaticSwitching || !ManagementAllowed) return;
            var desktop = _desktop;
            var point = new Point(e.X, e.Y);
            var source = LocalDisplays().FirstOrDefault(s => s.Bounds.Contains(point));
            if (source is null) return;
            var route = RouteFor(source.Id);
            if (route.Exit is not { } exit || !desktop.IsExitEdge(point, exit)) return;
            var target = _remoteDisplays.FirstOrDefault(s => s.Id == route.TargetId)
                ?? new DisplayInfo("desktop", 0, 0, _remoteWidth, _remoteHeight);
            var entry = ScreenRouting.EntryPoint(target, route.Entry,
                ScreenRouting.Fraction(source.Bounds, point, exit), _remoteDisplays);
            if (entry is null) return;
            EngageRemote(e.X, e.Y, entry.Value.X, entry.Value.Y);
            _activeRoute = (source, exit, target, route.Entry);
        }
    }

    private void OnRawMouseMove(int dx, int dy)
    {
        if (_mode == BridgeMode.Remote && _connection is { } connection && _remotePointer is { } pointer)
        {
            var previous = pointer.Position;
            (dx, dy) = _speed.Scale(dx, dy, CurrentSpeedLevel());
            var target = pointer.Advance(dx, dy);
            if (_config.UseSpatialMap && _spatialValid)
            {
                var source = _remoteDisplays.FirstOrDefault(d => d.Bounds.Contains(previous));
                if (source is not null)
                {
                    var transition = SpatialNavigation.Find(source, previous, target, _spatialRemote, _remoteDisplays, LocalDisplays());
                    if (transition is not null && (!transition.Route.TargetRemote || _config.AutomaticSwitching))
                    {
                        if (transition.Route.TargetRemote)
                        {
                            _lastLocalPoint = transition.Position;
                            SwitchToLocal(transition.Position.X, transition.Position.Y);
                            return;
                        }
                        pointer.SetPosition(transition.Position);
                    }
                    else pointer.SetPosition(ClampTo(source.Bounds, target));
                }
                _ = connection.SendAsync(WireMessage.MouseMove(pointer.Position.X, pointer.Position.Y));
                return;
            }

            if (_activeRoute is { } route && ScreenRouting.Crossed(previous, target, route.Target.Bounds, route.Entry))
            {
                var along = ScreenRouting.Vertical(route.Entry) ? previous.Y : previous.X;
                var edge = ScreenRouting.OnEdge(route.Target.Bounds, route.Entry, along);
                if (new DesktopLayout(_remoteDisplays.Select(d => d.Bounds)).IsExitEdge(edge, route.Entry))
                {
                    var resume = ScreenRouting.EntryPoint(route.Source, route.Exit,
                        ScreenRouting.Fraction(route.Target.Bounds, previous, route.Entry), LocalDisplays())
                        ?? _desktop.Clamp(new Point(_anchorX, _anchorY));
                    resume = route.Exit switch
                    {
                        ScreenEdge.Left => new Point(resume.X + 1, resume.Y),
                        ScreenEdge.Right => new Point(resume.X - 1, resume.Y),
                        ScreenEdge.Top => new Point(resume.X, resume.Y + 1),
                        _ => new Point(resume.X, resume.Y - 1),
                    };
                    resume = _desktop.Clamp(resume);
                    SwitchToLocal(resume.X, resume.Y);
                    return;
                }
            }

            _ = connection.SendAsync(WireMessage.MouseMove(pointer.Position.X, pointer.Position.Y));
        }
    }

    private void OnMouseButton(HookMouseEventArgs e)
    {
        if (_mode != BridgeMode.Remote || _connection is null) return;
        var kind = e.Button switch
        {
            MouseButton.Left => MouseButtonKind.Left,
            MouseButton.Right => MouseButtonKind.Right,
            _ => MouseButtonKind.Middle,
        };
        if (e.IsDown) _sentHeldButtons.Add(kind); else _sentHeldButtons.Remove(kind);
        _ = _connection.SendAsync(WireMessage.MouseButtonEvent(kind, e.IsDown));
    }

    private void OnMouseWheel(HookMouseEventArgs e)
    {
        if (_mode != BridgeMode.Remote || _connection is null) return;
        _ = _connection.SendAsync(WireMessage.MouseWheelEvent(e.WheelDelta));
    }

    private void OnKeyEvent(KeyEventArgsLite e)
    {
        if ((_mode != BridgeMode.Remote && !_viewInput) || _connection is null) return;
        if (e.IsDown) _sentHeldKeys[e.VkCode] = (e.ScanCode, e.IsExtended); else _sentHeldKeys.Remove(e.VkCode);
        _ = _connection.SendAsync(WireMessage.KeyEventMsg(e.VkCode, e.ScanCode, e.IsDown, e.IsExtended));
    }

    /// <summary>Flips who's in control. Driven by the global hotkey (Ctrl+Alt+Space,
    /// recognized and fully consumed inside <see cref="KeyboardHook"/> itself — it never
    /// reaches a local app and is never forwarded as a KeyEvent, so the two PCs can't
    /// bounce control back and forth on one press) and by the tray menu's two entries.
    /// Works even if the peer currently controls us: taking control always wins, the
    /// peer is forced to yield (see the <see cref="MessageType.SwitchToRemote"/> case in
    /// <see cref="OnMessageReceived"/>).</summary>
    public void ToggleControl()
    {
        if (_viewInput)
        {
            // Inside the remote-screen window the hotkey is the way out.
            _clipboard.BeginInvoke(() => ViewerCloseRequested?.Invoke());
            return;
        }
        if (!Powered)
        {
            StatusChanged?.Invoke("TecLink está apagado");
            return;
        }
        if (_connection is null || !_remoteKnown)
        {
            StatusChanged?.Invoke("Atajo presionado, pero no hay conexión con la otra PC todavía");
            return;
        }
        if (_mode != BridgeMode.Remote && !ManagementAllowed)
        {
            StatusChanged?.Invoke("La gestión de pantallas está apagada: el anfitrión debe activarla.");
            return;
        }

        if (_mode == BridgeMode.Remote)
        {
            SwitchToLocal();
        }
        else
        {
            Win32.GetCursorPos(out var pt);
            EngageRemote(pt.x, pt.y, _remoteWidth / 2, _remoteHeight / 2);
        }
    }

    /// <summary>Core entry into Remote mode: anchor is where OUR cursor currently is (used
    /// to translate further movement into deltas), entry is where the peer's cursor should
    /// land. Edge-crossing passes an edge-aligned entry; the hotkey passes the peer's
    /// screen center.</summary>
    private void EngageRemote(int anchorX, int anchorY, int entryX, int entryY)
    {
        if (!ManagementAllowed)
        {
            StatusChanged?.Invoke("La gestión de pantallas está apagada: el anfitrión debe activarla.");
            return;
        }
        _peerControlling = false;
        _speed.Reset();
        _mode = BridgeMode.Remote;
        _anchorX = anchorX;
        _anchorY = anchorY;
        _entryX = entryX;
        _entryY = Math.Clamp(entryY, 0, _remoteHeight - 1);
        _remotePointer = new RemotePointer(new Size(_remoteWidth, _remoteHeight), new Point(_entryX, _entryY));
        var source = LocalDisplays().FirstOrDefault(d => d.Bounds.Contains(new Point(anchorX, anchorY))) ?? LocalDisplays()[0];
        var legacyExit = _config.EdgeToPeer == EdgeDirection.Left ? ScreenEdge.Left : ScreenEdge.Right;
        _activeRoute = (source, legacyExit, new DisplayInfo("desktop", 0, 0, _remoteWidth, _remoteHeight), ScreenRouting.Opposite(legacyExit));

        _mouseHook.Swallow = true;
        _keyboardHook.Swallow = true;

        _ = _connection!.SendAsync(WireMessage.SwitchToRemote());
        _ = _connection!.SendAsync(WireMessage.MouseMove(_entryX, _entryY));
        StatusChanged?.Invoke("Controlando la PC remota");
        ControlChanged?.Invoke("Tomaste el mando de la otra PC");
    }

    private void SwitchToLocal(int? resumeX = null, int? resumeY = null, bool force = false)
    {
        if (_mode == BridgeMode.Local && !force) return;
        var wasRemote = _mode == BridgeMode.Remote;
        _mode = BridgeMode.Local;
        _remotePointer = null;
        _activeRoute = null;
        _mouseHook.Swallow = false;
        _keyboardHook.Swallow = _viewInput;

        if (resumeX is int rx && resumeY is int ry)
        {
            _lastLocalPoint = new Point(rx, ry);
            InputInjector.MoveTo(rx, ry);
        }

        if (wasRemote)
            ReleaseAllRemoteHeldInput();

        if (_connection is { } conn)
            _ = conn.SendAsync(WireMessage.SwitchToLocal());

        StatusChanged?.Invoke("Control local");
        ControlChanged?.Invoke("Tienes el mando local");
    }

    /// <summary>Releases (on ourselves) whatever the peer had pushed into us and never
    /// matched with an up — called on disconnect and when the peer tells us it let go.</summary>
    private void ReleaseAllInjectedInput()
    {
        foreach (var (vk, key) in _heldKeys) InputInjector.Key(vk, false, key.Extended, key.ScanCode);
        _heldKeys.Clear();

        foreach (var button in _heldButtons) InputInjector.Button(button, false);
        _heldButtons.Clear();
    }

    /// <summary>Releases (on the PEER) whatever we'd forwarded as held while we were
    /// controlling them — called whenever WE leave Remote mode, voluntarily or because
    /// the peer took control back.</summary>
    private void ReleaseAllRemoteHeldInput()
    {
        if (_connection is { } conn)
        {
            foreach (var (vk, key) in _sentHeldKeys)
                _ = conn.SendAsync(WireMessage.KeyEventMsg(vk, key.ScanCode, false, key.Extended));
            foreach (var button in _sentHeldButtons)
                _ = conn.SendAsync(WireMessage.MouseButtonEvent(button, false));
        }
        _sentHeldKeys.Clear();
        _sentHeldButtons.Clear();
    }

    private static DesktopLayout CaptureDesktop() => new(Screen.AllScreens.Select(s => s.Bounds));

    private Task SendDesktopAsync(PeerConnection connection)
    {
        var bounds = _desktop.Bounds;
        var displays = LocalDisplays().Select(d => d with { Left = d.Left - bounds.Left, Top = d.Top - bounds.Top }).ToArray();
        return connection.SendAsync(WireMessage.Hello(_config.MachineName, bounds.Width, bounds.Height, displays, true, _config.UseSpatialMap, true));
    }

    private static DisplayInfo[] LocalDisplays() => Screen.AllScreens.Select(s =>
        new DisplayInfo(s.DeviceName, s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height)).ToArray();

    private ScreenRoute RouteFor(string id) => _config.ScreenRoutes.FirstOrDefault(r => r.SourceId == id)
        ?? new ScreenRoute { SourceId = id, Exit = _config.RoutesConfigured ? null :
            _config.EdgeToPeer == EdgeDirection.Left ? ScreenEdge.Left : ScreenEdge.Right,
            Entry = _config.EdgeToPeer == EdgeDirection.Left ? ScreenEdge.Right : ScreenEdge.Left };

    public object ViewState(string status)
    {
        var local = LocalDisplays();
        var order = _config.ScreenOrder;
        return new
        {
            machineName = _config.MachineName, remoteName = _remoteName,
            connected = _connection is not null && _remoteKnown,
            mode = _mode == BridgeMode.Remote ? "remote" : _peerControlling ? "peer" : "local", status,
            automatic = _config.AutomaticSwitching, theme = _config.Theme, powered = Powered,
            placements = _config.ScreenPositions, spatialEnabled = _config.UseSpatialMap, peerMapSupported = _peerMapSupported,
            host = _config.PeerHost, port = _config.Port, role = _config.Role.ToString(),
            sessionCode = SessionDiscovery.Pretty(SessionDiscovery.Normalize(_config.SessionCode) ?? ""),
            joinCode = SessionDiscovery.Pretty(SessionDiscovery.Normalize(_config.JoinCode) ?? ""),
            sessionMode = _sessionMode, management = _management, joinNotice = _joinNotice, maxScreens = MaxScreens,
            pointerSpeed = CurrentSpeedLevel(), cursorStyle = CursorStyler.Normalize(_config.CursorStyle),
            cursorSize = CursorStyler.ClampSize(_config.CursorSizePercent), extras = CanUseExtras,
            transfer = Files.LastStatus, receiveFolder = Files.ReceiveFolder,
            local = local.OrderBy(d => order.Contains(d.Id) ? order.IndexOf(d.Id) : 1000 + Array.IndexOf(local, d)).Select(d => new
            {
                id = d.Id, label = "Pantalla " + (Array.IndexOf(local, d) + 1), width = d.Width, height = d.Height,
                left = d.Left, top = d.Top,
                primary = Screen.PrimaryScreen!.DeviceName == d.Id, route = RouteFor(d.Id),
                availableEdges = Enum.GetValues<ScreenEdge>().Where(side => ScreenRouting.EntryPoint(d, side, .5, local) is not null),
            }),
            remote = _remoteDisplays.Select((d, n) => new
            {
                id = d.Id, label = d.Id == "desktop" ? "Escritorio remoto" : "Pantalla " + (n + 1), width = d.Width, height = d.Height,
                left = d.Left, top = d.Top,
                availableEdges = Enum.GetValues<ScreenEdge>().Where(side => ScreenRouting.EntryPoint(d, side, .5, _remoteDisplays) is not null),
            }),
        };
    }

    public void ApplyMap(List<ScreenRoute> routes, List<string> order, bool automatic, string theme)
    {
        var local = LocalDisplays();
        if (routes.GroupBy(r => r.SourceId).Any(g => g.Count() > 1)) throw new ArgumentException("Una pantalla no puede tener dos configuraciones.");
        foreach (var route in routes)
        {
            var source = local.FirstOrDefault(s => s.Id == route.SourceId) ?? throw new ArgumentException("La pantalla cambió. Actualiza el mapa.");
            if (!Enum.IsDefined(route.Entry) || route.Exit is { } invalid && !Enum.IsDefined(invalid))
                throw new ArgumentException("El borde elegido no es válido.");
            if (route.Exit is not { } exit) continue;
            if (ScreenRouting.EntryPoint(source, exit, .5, local) is null)
                throw new ArgumentException("Ese borde une pantallas locales. Elige un borde exterior.");
            if (_remoteKnown)
            {
                var target = _remoteDisplays.FirstOrDefault(s => s.Id == route.TargetId)
                    ?? new DisplayInfo("desktop", 0, 0, _remoteWidth, _remoteHeight);
                if (ScreenRouting.EntryPoint(target, route.Entry, .5, _remoteDisplays) is null)
                    throw new ArgumentException("La entrada elegida no tiene un borde exterior disponible.");
            }
        }
        var previous = (_config.ScreenRoutes, _config.ScreenOrder, _config.AutomaticSwitching, _config.RoutesConfigured, _config.Theme);
        _config.ScreenRoutes = _config.ScreenRoutes.Where(r => !local.Any(d => d.Id == r.SourceId)).Concat(routes).ToList();
        _config.ScreenOrder = order.Where(id => local.Any(d => d.Id == id)).Distinct()
            .Concat(_config.ScreenOrder.Where(id => !local.Any(d => d.Id == id))).ToList();
        _config.AutomaticSwitching = automatic; _config.RoutesConfigured = true;
        _config.Theme = theme is "dark" or "light" ? theme : "system";
        try { _config.Save(); }
        catch
        {
            (_config.ScreenRoutes, _config.ScreenOrder, _config.AutomaticSwitching, _config.RoutesConfigured, _config.Theme) = previous;
            throw;
        }
        if (_peerControlling && _connection is { } peer)
        {
            _peerControlling = false;
            _ = peer.SendAsync(WireMessage.SwitchToRemote()); // ask an active remote controller to yield first
        }
        SwitchToLocal(force: true);
        ReleaseAllInjectedInput();
        StatusChanged?.Invoke("Mapa guardado. Los cambios ya están activos.");
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_clipboard.IsDisposed) return;
        _clipboard.BeginInvoke(() =>
        {
            SwitchToLocal(force: true);
            _desktop = CaptureDesktop();
            RefreshSpatialRoutes();
            if (_connection is { } connection) _ = SendDesktopAsync(connection);
        });
    }

    public bool CanUseExtras => _connection is not null && _remoteKnown && _peerExtras;
    public string RemoteName => _remoteName;
    public IReadOnlyList<DisplayInfo> RemoteDisplays => _remoteDisplays;

    /// <summary>The level chosen in TecLink, or whatever Windows currently uses.</summary>
    private int CurrentSpeedLevel()
    {
        if (_config.PointerSpeedLevel > 0) return PointerSpeed.Clamp(_config.PointerSpeedLevel);
        var level = PointerSpeed.Default;
        return Win32.SystemParametersInfo(Win32.SPI_GETMOUSESPEED, 0, ref level, 0) ? PointerSpeed.Clamp(level) : PointerSpeed.Default;
    }

    private void ApplyWindowsSpeed()
    {
        if (_config.PointerSpeedLevel <= 0) return;
        var level = 0;
        if (Win32.SystemParametersInfo(Win32.SPI_GETMOUSESPEED, 0, ref level, 0) && level == _config.PointerSpeedLevel) return;
        Win32.SystemParametersInfo(Win32.SPI_SETMOUSESPEED, 0, new IntPtr(_config.PointerSpeedLevel), 3); // persist + notify
    }

    /// <summary>One speed and one pointer design for both PCs: stored and applied here
    /// (Windows pointer speed, arrow design) and mirrored on the peer.</summary>
    public void ApplyPointerSettings(int speedLevel, string? cursorStyle, int cursorSizePercent, bool fromPeer = false)
    {
        var previous = (_config.PointerSpeedLevel, _config.CursorStyle, _config.CursorSizePercent);
        if (speedLevel > 0) _config.PointerSpeedLevel = PointerSpeed.Clamp(speedLevel); // 0 = the peer never chose one
        _config.CursorStyle = CursorStyler.Normalize(cursorStyle);
        _config.CursorSizePercent = CursorStyler.ClampSize(cursorSizePercent);
        try { _config.Save(); }
        catch { (_config.PointerSpeedLevel, _config.CursorStyle, _config.CursorSizePercent) = previous; throw; }
        CursorStyler.Apply(_config.CursorStyle, _config.CursorSizePercent);
        ApplyWindowsSpeed();
        if (!fromPeer && CanUseExtras)
            _ = _connection!.SendAsync(WireMessage.Pointer(_config.PointerSpeedLevel, _config.CursorStyle, _config.CursorSizePercent));
        if (fromPeer) StatusChanged?.Invoke("Ajustes del ratón recibidos de la otra PC.");
    }

    public void StartViewing(int displayIndex, int maxWidth, int maxHeight)
    {
        if (CanUseExtras && ManagementAllowed) _ = _connection!.SendAsync(WireMessage.ScreenStart(displayIndex, maxWidth, maxHeight));
    }

    public void StopViewing()
    {
        SetViewInput(false);
        if (_connection is { } connection && _peerExtras) _ = connection.SendAsync(WireMessage.ScreenStop());
    }

    public void AckFrame() => _ = _connection?.SendAsync(WireMessage.ScreenAck());

    /// <summary>While the remote-screen window is active, the keyboard goes to the peer
    /// and the window forwards its own mouse events.</summary>
    public void SetViewInput(bool active)
    {
        if (active == _viewInput) return;
        if (active)
        {
            if (!CanUseExtras || !ManagementAllowed) return;
            if (_mode == BridgeMode.Remote) SwitchToLocal();
            _viewInput = true;
            _peerControlling = false;
            _keyboardHook.Swallow = true;
            _ = _connection!.SendAsync(WireMessage.SwitchToRemote());
            StatusChanged?.Invoke("Controlando la otra PC desde su pantalla");
        }
        else
        {
            _viewInput = false;
            _keyboardHook.Swallow = _mode == BridgeMode.Remote;
            ReleaseAllRemoteHeldInput();
            if (_connection is { } connection) _ = connection.SendAsync(WireMessage.SwitchToLocal());
            StatusChanged?.Invoke("Control local");
        }
    }

    public void ViewerMove(int wireX, int wireY)
    {
        if (_viewInput && _connection is { } connection) _ = connection.SendAsync(WireMessage.MouseMove(wireX, wireY));
    }

    public void ViewerButton(MouseButtonKind button, bool isDown)
    {
        if (!_viewInput || _connection is not { } connection) return;
        if (isDown) _sentHeldButtons.Add(button); else _sentHeldButtons.Remove(button);
        _ = connection.SendAsync(WireMessage.MouseButtonEvent(button, isDown));
    }

    public void ViewerWheel(int delta)
    {
        if (_viewInput && _connection is { } connection) _ = connection.SendAsync(WireMessage.MouseWheelEvent(delta));
    }

    public void Dispose()
    {
        _screenHost.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _mouseHook.Dispose();
        _rawMouse.Dispose();
        _keyboardHook.Dispose();
    }

    private static Point ClampTo(Rectangle screen, Point point) => new(Math.Clamp(point.X, screen.Left, screen.Right - 1), Math.Clamp(point.Y, screen.Top, screen.Bottom - 1));

    private void HandleSpatialLocalMove(HookMouseEventArgs e)
    {
        var candidate = new Point(e.X, e.Y);
        Win32.GetCursorPos(out var cursor);
        var previous = _lastLocalPoint ?? new Point(cursor.x, cursor.y);
        var local = LocalDisplays();
        var source = local.FirstOrDefault(d => d.Bounds.Contains(previous)) ?? local.FirstOrDefault(d => d.Bounds.Contains(candidate));
        if (source is null) { _lastLocalPoint = candidate; return; }
        var transition = SpatialNavigation.Find(source, previous, candidate, _spatialLocal, local, _remoteDisplays);
        if (transition is not null && (!transition.Route.TargetRemote || (_config.AutomaticSwitching && ManagementAllowed)))
        {
            e.Suppress = true;
            if (transition.Route.TargetRemote)
                EngageRemote(previous.X, previous.Y, transition.Position.X, transition.Position.Y);
            else
            {
                _lastLocalPoint = transition.Position;
                InputInjector.MoveTo(transition.Position.X, transition.Position.Y);
            }
            return;
        }
        _lastLocalPoint = ClampTo(source.Bounds, candidate);
        if (!source.Bounds.Contains(candidate)) { e.Suppress = true; InputInjector.MoveTo(_lastLocalPoint.Value.X, _lastLocalPoint.Value.Y); }
    }

    private void RefreshSpatialRoutes()
    {
        _spatialValid = false;
        if (!_config.UseSpatialMap) return;
        try
        {
            _spatialLocal = SpatialMap.Build(LocalDisplays(), _remoteDisplays, _config.ScreenPositions);
            _spatialRemote = SpatialMap.Build(_remoteDisplays, LocalDisplays(), _config.ScreenPositions.Select(p => p with { Remote = !p.Remote }).ToArray());
            _spatialValid = true;
        }
        catch (ArgumentException) { /* Changed monitors: keep native local movement until the map is revised. */ }
    }

    private void StoreSpatialMap(List<ScreenPlacement> positions, bool automatic, string theme)
    {
        _ = SpatialMap.Build(LocalDisplays(), _remoteDisplays, positions);
        var previous = (_config.ScreenPositions, _config.UseSpatialMap, _config.AutomaticSwitching, _config.Theme);
        _config.ScreenPositions = positions; _config.UseSpatialMap = true; _config.AutomaticSwitching = automatic;
        _config.Theme = theme is "dark" or "light" ? theme : "system";
        try { _config.Save(); }
        catch { (_config.ScreenPositions, _config.UseSpatialMap, _config.AutomaticSwitching, _config.Theme) = previous; throw; }
        if (_peerControlling && _connection is { } peer) { _peerControlling = false; _ = peer.SendAsync(WireMessage.SwitchToRemote()); }
        SwitchToLocal(force: true); ReleaseAllInjectedInput(); _lastLocalPoint = null;
        RefreshSpatialRoutes();
    }

    public async Task<string> ApplySpatialMapAsync(List<ScreenPlacement> positions, bool automatic, string theme)
    {
        StoreSpatialMap(positions, automatic, theme);
        if (!_peerMapSupported || _connection is null || !_remoteKnown)
            return "Mapa guardado en esta PC. Actualiza la otra PC para sincronizarlo.";
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mapRequests[id] = completion;
        try
        {
            await _connection.SendAsync(WireMessage.Map(id, positions.ToArray()));
            var answer = await completion.Task.WaitAsync(TimeSpan.FromSeconds(4));
            return answer.Accepted ? "Mapa guardado en ambas PCs." : "Guardado aquí. La otra PC no pudo aplicarlo: " + answer.Text;
        }
        catch (TimeoutException) { return "Mapa guardado aquí. La otra PC no confirmó a tiempo."; }
        finally { _mapRequests.Remove(id); }
    }
}
