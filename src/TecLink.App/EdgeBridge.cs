using TecLink.App.Input;
using TecLink.Core.Networking;
using TecLink.Core.Protocol;

namespace TecLink.App;

public enum BridgeMode { Local, Remote }

/// <summary>
/// Ties the local hooks, the peer connection and the clipboard watcher together and
/// implements the edge-crossing state machine: while in <see cref="BridgeMode.Local"/>
/// input behaves normally; crossing the configured screen edge swallows local input,
/// tracks displacement from the crossing point, and forwards mapped coordinates to the
/// peer until the (virtual) cursor crosses back.
/// </summary>
public sealed class EdgeBridge : IDisposable
{
    private readonly AppConfig _config;
    private readonly MouseHook _mouseHook = new();
    private readonly KeyboardHook _keyboardHook = new();
    private readonly ClipboardWatcher _clipboard;
    private readonly int _localWidth;
    private readonly int _localHeight;

    private PeerConnection? _connection;
    private int _remoteWidth;
    private int _remoteHeight;
    private bool _remoteKnown;

    private BridgeMode _mode = BridgeMode.Local;
    private int _anchorX, _anchorY;
    private int _entryX, _entryY;

    // Tracks keys/buttons we've injected on THIS machine while being remote-controlled,
    // so a dropped connection mid-keypress doesn't leave them stuck down.
    private readonly Dictionary<int, bool> _heldKeys = new();
    private readonly HashSet<MouseButtonKind> _heldButtons = new();

    public event Action<string>? StatusChanged;

    public EdgeBridge(AppConfig config, ClipboardWatcher clipboard)
    {
        _config = config;
        _clipboard = clipboard;

        var bounds = Screen.PrimaryScreen!.Bounds;
        _localWidth = bounds.Width;
        _localHeight = bounds.Height;

        _mouseHook.Move += OnMouseMove;
        _mouseHook.ButtonChanged += OnMouseButton;
        _mouseHook.Wheel += OnMouseWheel;
        _keyboardHook.KeyEvent += OnKeyEvent;
        _clipboard.TextCopied += OnLocalClipboardCopied;
    }

    public void InstallHooks()
    {
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
            await connection.SendAsync(WireMessage.Hello(_config.MachineName, _localWidth, _localHeight));

            await Task.WhenAny(disconnectedTcs.Task, Task.Delay(Timeout.Infinite, ct));

            StatusChanged?.Invoke("Conexión perdida. Reintentando...");
            _connection = null;
            SwitchToLocal(force: true);
            ReleaseAllInjectedInput();
            await connection.DisposeAsync();

            if (ct.IsCancellationRequested) return;
            await Task.Delay(2000, ct);
        }
    }

    private void OnMessageReceived(WireMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.Hello:
                _remoteWidth = msg.ScreenWidth;
                _remoteHeight = msg.ScreenHeight;
                _remoteKnown = true;
                StatusChanged?.Invoke($"Vinculado con {msg.MachineName} ({_remoteWidth}x{_remoteHeight})");
                break;
            case MessageType.MouseMove:
                InputInjector.MoveTo(msg.X, msg.Y);
                break;
            case MessageType.MouseButton:
                if (msg.IsDown) _heldButtons.Add(msg.Button); else _heldButtons.Remove(msg.Button);
                InputInjector.Button(msg.Button, msg.IsDown);
                break;
            case MessageType.MouseWheel:
                InputInjector.Wheel(msg.WheelDelta);
                break;
            case MessageType.KeyEvent:
                if (msg.IsDown) _heldKeys[msg.VkCode] = msg.IsExtendedKey; else _heldKeys.Remove(msg.VkCode);
                InputInjector.Key(msg.VkCode, msg.IsDown, msg.IsExtendedKey);
                break;
            case MessageType.ClipboardText:
                _clipboard.ApplyRemoteText(msg.Text ?? "");
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
            if (_connection is null || !_remoteKnown) return;

            var atRightEdge = _config.EdgeToPeer == EdgeDirection.Right && e.X >= _localWidth - 1;
            var atLeftEdge = _config.EdgeToPeer == EdgeDirection.Left && e.X <= 0;
            if (!atRightEdge && !atLeftEdge) return;

            SwitchToRemote(e.X, e.Y);
        }
        else
        {
            var dx = e.X - _anchorX;
            var dy = e.Y - _anchorY;
            var targetX = _entryX + dx;
            var targetY = Math.Clamp(_entryY + dy, 0, _remoteHeight - 1);

            var crossedBackLeft = _config.EdgeToPeer == EdgeDirection.Right && targetX < 0;
            var crossedBackRight = _config.EdgeToPeer == EdgeDirection.Left && targetX > _remoteWidth - 1;

            if (crossedBackLeft || crossedBackRight)
            {
                var excess = crossedBackLeft ? -targetX : targetX - (_remoteWidth - 1);
                var newLocalX = _config.EdgeToPeer == EdgeDirection.Right
                    ? Math.Clamp(_localWidth - 1 - excess, 0, _localWidth - 1)
                    : Math.Clamp(excess, 0, _localWidth - 1);
                SwitchToLocal(newLocalX, targetY);
                return;
            }

            _ = _connection!.SendAsync(WireMessage.MouseMove(Math.Clamp(targetX, 0, _remoteWidth - 1), targetY));
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
        _ = _connection.SendAsync(WireMessage.KeyEventMsg(e.VkCode, e.ScanCode, e.IsDown, e.IsExtended));
    }

    private void SwitchToRemote(int atX, int atY)
    {
        _mode = BridgeMode.Remote;
        _anchorX = atX;
        _anchorY = atY;
        _entryX = _config.EdgeToPeer == EdgeDirection.Right ? 0 : _remoteWidth - 1;
        _entryY = Math.Clamp(atY, 0, _remoteHeight - 1);

        _mouseHook.Swallow = true;
        _keyboardHook.Swallow = true;

        _ = _connection!.SendAsync(WireMessage.MouseMove(_entryX, _entryY));
        StatusChanged?.Invoke("Controlando la PC remota");
    }

    private void SwitchToLocal(int? resumeX = null, int? resumeY = null, bool force = false)
    {
        if (_mode == BridgeMode.Local && !force) return;
        _mode = BridgeMode.Local;
        _mouseHook.Swallow = false;
        _keyboardHook.Swallow = false;

        if (resumeX is int rx && resumeY is int ry)
            InputInjector.MoveTo(rx, ry);

        StatusChanged?.Invoke("Control local");
    }

    private void ReleaseAllInjectedInput()
    {
        foreach (var (vk, extended) in _heldKeys) InputInjector.Key(vk, false, extended);
        _heldKeys.Clear();

        foreach (var button in _heldButtons) InputInjector.Button(button, false);
        _heldButtons.Clear();
    }

    public void Dispose()
    {
        _mouseHook.Dispose();
        _keyboardHook.Dispose();
    }
}
