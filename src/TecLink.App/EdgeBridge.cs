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

    public async Task RunNetworkingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PeerConnection connection;
            try
            {
                StatusChanged?.Invoke(_config.Role == PeerRole.Server
                    ? $"Esperando conexión en el puerto {_config.Port}..."
                    : $"Conectando a {_config.PeerHost}:{_config.Port}...");

                connection = _config.Role == PeerRole.Server
                    ? await PeerServer.AcceptOneAsync(_config.Port, ct)
                    : await PeerServer.ConnectAsync(_config.PeerHost, _config.Port, ct);
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
            var disconnectedTcs = new TaskCompletionSource();

            connection.MessageReceived += OnMessageReceived;
            connection.Disconnected += () => disconnectedTcs.TrySetResult();
            connection.StartReceiving();

            StatusChanged?.Invoke($"Conectado con {_config.PeerHost} — enviando identificación...");
            await SendDesktopAsync(connection);

            await Task.WhenAny(disconnectedTcs.Task, Task.Delay(Timeout.Infinite, ct));

            StatusChanged?.Invoke("Conexión perdida. Reintentando...");
            _connection = null;
            _peerControlling = false;
            _clipboard.BeginInvoke(() =>
            {
                SwitchToLocal(force: true);
                ReleaseAllInjectedInput();
            });
            await connection.DisposeAsync();

            if (ct.IsCancellationRequested) return;
            await Task.Delay(2000, ct);
        }
    }

    private void OnMessageReceived(WireMessage msg)
    {
        if (_clipboard.IsDisposed) return;
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
                StatusChanged?.Invoke($"Vinculado con {msg.MachineName} ({_remoteWidth}x{_remoteHeight})");
                break;
            case MessageType.MouseMove:
                var localPoint = _desktop.FromWire(new Point(msg.X, msg.Y));
                InputInjector.MoveTo(localPoint.X, localPoint.Y);
                break;
            case MessageType.MouseButton:
                if (msg.IsDown) _heldButtons.Add(msg.Button); else _heldButtons.Remove(msg.Button);
                InputInjector.Button(msg.Button, msg.IsDown);
                break;
            case MessageType.MouseWheel:
                InputInjector.Wheel(msg.WheelDelta);
                break;
            case MessageType.KeyEvent:
                if (msg.IsDown) _heldKeys[msg.VkCode] = (msg.ScanCode, msg.IsExtendedKey); else _heldKeys.Remove(msg.VkCode);
                if (!InputInjector.Key(msg.VkCode, msg.IsDown, msg.IsExtendedKey, msg.ScanCode))
                    StatusChanged?.Invoke("Windows rechazó entrada de teclado; revisa error.log");
                break;
            case MessageType.ClipboardText:
                _clipboard.ApplyRemoteText(msg.Text ?? "");
                break;
            case MessageType.SwitchToRemote:
                // The peer just started controlling us. If we were ALSO currently
                // controlling them (both pressed the hotkey near-simultaneously), we
                // yield — whoever's "take control" message arrives wins, the other side
                // backs off automatically instead of both ends fighting over input.
                if (_mode == BridgeMode.Remote) SwitchToLocal();
                _peerControlling = true;
                StatusChanged?.Invoke("La otra PC tomó el mando");
                ControlChanged?.Invoke("La otra PC tiene el mando");
                break;
            case MessageType.SwitchToLocal:
                _peerControlling = false;
                ReleaseAllInjectedInput(); // defensive: peer says it's done, make sure nothing stayed held here
                StatusChanged?.Invoke("La otra PC soltó el mando");
                ControlChanged?.Invoke("Tienes el mando local");
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
        if (_mode == BridgeMode.Local)
        {
            if (_connection is null || !_remoteKnown || _peerControlling) return;
            var desktop = _desktop;
            if (!desktop.IsExitEdge(new Point(e.X, e.Y), _config.EdgeToPeer == EdgeDirection.Right)) return;

            var entryX = _config.EdgeToPeer == EdgeDirection.Right ? 0 : _remoteWidth - 1;
            var localY = desktop.ToWire(new Point(e.X, e.Y)).Y;
            var entryY = (int)((long)localY * (_remoteHeight - 1) / Math.Max(1, desktop.Bounds.Height - 1));
            EngageRemote(e.X, e.Y, entryX, entryY);
        }
    }

    private void OnRawMouseMove(int dx, int dy)
    {
        if (_mode == BridgeMode.Remote && _connection is { } connection && _remotePointer is { } pointer)
        {
            var target = pointer.Advance(dx, dy);
            var targetX = target.X;
            var targetY = target.Y;

            var crossedBackLeft = _config.EdgeToPeer == EdgeDirection.Right && targetX < 0;
            var crossedBackRight = _config.EdgeToPeer == EdgeDirection.Left && targetX > _remoteWidth - 1;

            if (crossedBackLeft || crossedBackRight)
            {
                var excess = crossedBackLeft ? -targetX : targetX - (_remoteWidth - 1);
                var newLocalX = _anchorX + (_config.EdgeToPeer == EdgeDirection.Right ? -excess : excess);
                var bounds = _desktop.Bounds;
                var localY = bounds.Top + (int)((long)targetY * (bounds.Height - 1) / Math.Max(1, _remoteHeight - 1));
                var resume = _desktop.Clamp(new Point(newLocalX, localY));
                SwitchToLocal(resume.X, resume.Y);
                return;
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
        if (_mode != BridgeMode.Remote || _connection is null) return;
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
        if (_connection is null || !_remoteKnown)
        {
            StatusChanged?.Invoke("Atajo presionado, pero no hay conexión con la otra PC todavía");
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
        _peerControlling = false;
        _mode = BridgeMode.Remote;
        _anchorX = anchorX;
        _anchorY = anchorY;
        _entryX = entryX;
        _entryY = Math.Clamp(entryY, 0, _remoteHeight - 1);
        _remotePointer = new RemotePointer(new Size(_remoteWidth, _remoteHeight), new Point(_entryX, _entryY));

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
        _mouseHook.Swallow = false;
        _keyboardHook.Swallow = false;

        if (resumeX is int rx && resumeY is int ry)
            InputInjector.MoveTo(rx, ry);

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
        return connection.SendAsync(WireMessage.Hello(_config.MachineName, bounds.Width, bounds.Height));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_clipboard.IsDisposed) return;
        _clipboard.BeginInvoke(() =>
        {
            SwitchToLocal(force: true);
            _desktop = CaptureDesktop();
            if (_connection is { } connection) _ = SendDesktopAsync(connection);
        });
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _mouseHook.Dispose();
        _rawMouse.Dispose();
        _keyboardHook.Dispose();
    }
}
