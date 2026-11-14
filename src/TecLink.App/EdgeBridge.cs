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
public sealed partial class EdgeBridge : IDisposable
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

    /// <summary>False until the user opens a session or joins one. TecLink starts with NO connection of any kind.</summary>
    public volatile bool NetworkActive;

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
        // Repairs an older state where the host had removed its own screens: they always come back.
        if (_config.Role == PeerRole.Server && _config.ScreenPositions.Any(p => !p.Remote && p.Hidden))
        {
            _config.ScreenPositions = ApplyHiddenRules(_config.ScreenPositions, fromPeer: false);
            try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        }
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Files = new FileTransfers(() => _peerExtras ? _connection : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TecLink"));
        Files.Status += text => StatusChanged?.Invoke(text);
        InitSuite();
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

    /// <summary>The "I just want my mouse and keyboard back" button: leaves Remote mode and releases every hook and held key.</summary>
    public void FreeInput()
    {
        var wasControlled = _peerControlling;
        _viewInput = false; _peerControlling = false;
        _mouseHook.Swallow = false; _keyboardHook.Swallow = false;
        SwitchToLocal(force: true);
        ReleaseAllRemoteHeldInput(); ReleaseAllInjectedInput();
        // Ask the PC that was controlling this one to let go. (Never SwitchToRemote: that would tell it WE are
        // controlling IT, and both PCs would end up believing the opposite of what is happening.)
        if (wasControlled && _connection is { } peer) _ = peer.SendAsync(WireMessage.Yield());
        StatusChanged?.Invoke("Mouse y teclado liberados en esta PC.");
    }

    private long _beat = Environment.TickCount64;
    private volatile bool _watchdogTripped;
    private System.Threading.Timer? _watchdog;

    /// <summary>The global hooks live on the UI thread. If it ever stalls while TecLink is holding the mouse and keyboard,
    /// the whole PC would feel dead: this thread-pool timer lets go after 1.5 s without a heartbeat.</summary>
    private void StartWatchdog()
    {
        _watchdog = new System.Threading.Timer(_ =>
        {
            if (_clipboard.IsDisposed) return;
            var late = Environment.TickCount64 - Interlocked.Read(ref _beat);
            if (late > 1500 && (_mouseHook.Swallow || _keyboardHook.Swallow))
            {
                _mouseHook.Swallow = false; _keyboardHook.Swallow = false; _watchdogTripped = true;
                Logger.Info($"La interfaz tardó {late} ms en responder: se soltaron el mouse y el teclado por seguridad.");
            }
            try
            {
                _clipboard.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref _beat, Environment.TickCount64);
                    if (_watchdogTripped) { _watchdogTripped = false; FreeInput(); }
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }, null, 500, 500);
    }

    public void InstallHooks()
    {
        StartWatchdog();
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
    private volatile bool _joinRefused; // the host said no (wrong code, replaced by another PC): stop retrying until the user asks again
    private Action? _dropCurrent;

    /// <summary>Without a code-based session (legacy direct link) nothing is restricted.</summary>
    public bool ManagementAllowed => !_sessionMode || _management;
    public string SessionCode => _config.SessionCode;

    public void StartHosting()
    {
        _config.Role = PeerRole.Server; _config.JoinCode = ""; _joinNotice = "";
        _config.Save();
    }

    public void JoinSession(string? code, string? address = null)
    {
        var clean = SessionDiscovery.Normalize(code) ?? throw new ArgumentException("El código tiene 6 letras o números. Revísalo.");
        if (SessionDiscovery.Normalize(_config.SessionCode) == clean) throw new ArgumentException("Ese es el código de esta PC. Escribe el de la otra PC.");
        address = (address ?? "").Trim();
        if (address.Length > 0 && (address.Length > 253 || !address.All(c => char.IsLetterOrDigit(c) || c is '.' or '-')))
            throw new ArgumentException("La dirección solo puede tener números, letras, puntos y guiones. Ejemplo: 192.168.1.20");
        _config.Role = PeerRole.Client; _config.JoinCode = clean; _joinNotice = ""; _joinRefused = false;
        if (address.Length > 0) _config.PeerHost = address; // fallback when the network blocks the automatic search
        _config.Save();
        _joinNotice = "";
    }

    /// <summary>IPv4 addresses of this PC on real networks, to show next to the code.</summary>
    public static string[] LocalAddresses() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."))
        .Select(a => a.ToString()).Distinct().ToArray();

    /// <summary>A plain-Spanish report of what this PC can and cannot do on the network, to copy and send for support.</summary>
    public async Task<List<string>> DiagnoseAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        string Yes(bool v) => v ? "sí" : "NO";
        lines.Add($"TecLink {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} — {_config.MachineName}");
        var addresses = LocalAddresses();
        lines.Add("Direcciones de esta PC: " + (addresses.Length == 0 ? "NINGUNA (sin red)" : string.Join(", ", addresses)));
        var host = _config.Role == PeerRole.Server;
        lines.Add(host ? $"Papel: anfitrión, código {SessionDiscovery.Pretty(SessionDiscovery.Normalize(_config.SessionCode) ?? "")}" : $"Papel: invitado de la sesión {SessionDiscovery.Pretty(SessionDiscovery.Normalize(_config.JoinCode) ?? "")}");
        lines.Add($"Windows permite conexiones entrantes a TecLink: {Yes(CheckFirewall())}");
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell", "-NoProfile -Command \"(Get-NetConnectionProfile | ForEach-Object { $_.Name + ' = ' + $_.NetworkCategory }) -join ', '\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            var text = await p!.StandardOutput.ReadToEndAsync(ct); p.WaitForExit(6000);
            lines.Add("Tipo de red (Privada es lo normal; Pública bloquea más): " + text.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        if (host)
        {
            var tcp = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == _config.Port);
            var udp = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == SessionDiscovery.DiscoveryPort);
            lines.Add($"Escuchando conexiones (TCP {_config.Port}): {Yes(tcp)}; respondiendo búsquedas (UDP {SessionDiscovery.DiscoveryPort}): {Yes(udp)}");
            lines.Add(tcp && udp ? "Esta PC está lista para recibir invitados. Si no entran, el bloqueo está en la otra PC o en la red." : "Esta PC NO está lista: reinicia TecLink o pulsa «Permitir en el Firewall».");
        }
        else
        {
            var code = SessionDiscovery.Normalize(_config.JoinCode);
            if (code is not null)
            {
                var udp = await SessionDiscovery.FindAsync(code, TimeSpan.FromSeconds(3), ct);
                lines.Add("Búsqueda por UDP: " + (udp is null ? "no encontró la sesión" : $"encontró {udp.Address}"));
                var sweep = await SessionDiscovery.SweepAsync(SessionDiscovery.LocalSubnetCandidates(), _config.Port, code, TimeSpan.FromMilliseconds(700), ct);
                lines.Add("Búsqueda por TCP (todas las direcciones de tu red): " + (sweep is null ? "no encontró la sesión" : $"encontró {sweep.Address}"));
                lines.Add(udp is null && sweep is null
                    ? "No se encontró. Revisa: 1) el código; 2) que la otra PC tenga TecLink abierto y en esta misma red Wi‑Fi (no en otra, ni con «aislamiento de clientes»); 3) que en la otra PC se pulsó «Permitir en el Firewall»."
                    : "La sesión es alcanzable desde esta PC.");
            }
            else lines.Add("Esta PC no está intentando unirse a ninguna sesión.");
        }
        try { lines.Add("— Registro reciente —"); var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecLink", "connection.log"); if (File.Exists(log)) lines.AddRange(File.ReadLines(log).TakeLast(8)); }
        catch (IOException) { }
        return lines;
    }

    private const string FirewallRuleName = "TecLink (sesiones)";
    private static (DateTime At, bool Ok) _firewallCache;

    private static volatile bool _firewallOk = true; // unknown = do not nag
    private static long _firewallAt;
    private static int _firewallBusy;

    /// <summary>Last known answer, instantly. The check itself runs in the background: this is called from the
    /// UI thread, which also serves the global mouse hook, so it must NEVER wait for Windows.</summary>
    public static bool FirewallAllowed()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _firewallAt) > 30_000 && Interlocked.Exchange(ref _firewallBusy, 1) == 0)
            Task.Run(() => { try { _firewallOk = CheckFirewall(); } catch (Exception ex) { Logger.LogError(ex); } finally { Interlocked.Exchange(ref _firewallAt, Environment.TickCount64); Interlocked.Exchange(ref _firewallBusy, 0); } });
        return _firewallOk;
    }

    /// <summary>The real (slow) check: an enabled inbound "allow" rule for this program or for TecLink's own rule.</summary>
    public static bool CheckFirewall()
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is not null)
            {
                dynamic policy = Activator.CreateInstance(type)!;
                var exe = Application.ExecutablePath;
                foreach (dynamic rule in policy.Rules)
                {
                    try
                    {
                        if (!(bool)rule.Enabled || (int)rule.Direction != 1 || (int)rule.Action != 1) continue;
                        if (string.Equals((string?)rule.Name, FirewallRuleName, StringComparison.OrdinalIgnoreCase)) return true;
                        if (string.Equals((string?)rule.ApplicationName, exe, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch (Exception) { /* a rule we cannot read */ }
                }
                return false;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRuleName}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            p!.StandardOutput.ReadToEnd(); p.WaitForExit(4000); return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return true; }
    }

    private static string[] _addresses = Array.Empty<string>();
    private static long _addressesAt;
    private static string[] CachedAddresses()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _addressesAt) > 15_000)
        {
            Interlocked.Exchange(ref _addressesAt, Environment.TickCount64);
            Task.Run(() => { try { _addresses = LocalAddresses(); } catch (Exception) { } });
        }
        return _addresses;
    }
    /// <summary>Asks Windows (one UAC prompt) to allow TecLink on any network profile (without it a "Public" Wi-Fi drops everything). Nothing is admitted without the invitation code.</summary>
    public void AllowFirewall()
    {
        var exe = Application.ExecutablePath;
        var args = $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh", args)
            { Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            p?.WaitForExit(15000);
        }
        catch (System.ComponentModel.Win32Exception) { StatusChanged?.Invoke("No se concedió el permiso de Windows. Vuelve a intentarlo y pulsa Sí."); return; }
        var ok = CheckFirewall(); _firewallOk = ok; Interlocked.Exchange(ref _firewallAt, Environment.TickCount64);
        StatusChanged?.Invoke(ok ? "Firewall listo: TecLink puede recibir conexiones." : "No se pudo crear la regla del Firewall.");
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
            _screenHost.Stop(); _tilesForPeer.Stop();
            ViewerCloseRequested?.Invoke();
        });
    }

    // ---- Leaving a session: the one who leaves gets an empty suite, the other sees "disconnected" ----
    private volatile string _peerStatus = "none"; // none | connected | left | lost

    /// <summary>Tells the peer we are leaving on purpose (so it shows "left", not "link lost").</summary>
    public void AnnounceLeave()
    {
        try { if (_connection is { } conn && _remoteKnown) conn.SendAsync(WireMessage.Leave()).Wait(300); }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException) { }
    }

    /// <summary>Forgets the other PC completely: no screens, name, map entries or camera. Used when WE leave.</summary>
    public void ClearRemote()
    {
        _remoteDisplays = Array.Empty<DisplayInfo>();
        _remoteName = "Otra PC"; _remoteKnown = false; _peerMapSupported = false; _peerExtras = false; _peerStatus = "none";
        _spatialValid = false; _spatialLocal = new(); _spatialRemote = new(); _activeRoute = null;
        ClearSuite();
        if (_config.ScreenPositions.Any(p => p.Remote))
        {
            _config.ScreenPositions = _config.ScreenPositions.Where(p => !p.Remote).ToList();
            try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        }
        _clipboard.BeginInvoke(() => { SwitchToLocal(force: true); ReleaseAllInjectedInput(); ViewerCloseRequested?.Invoke(); });
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
            _ = SessionDiscovery.RunResponderAsync(() => SessionDiscovery.Normalize(_config.SessionCode) ?? "", _config.Port, ct,
                ex => { Logger.LogError(ex); StatusChanged?.Invoke("Otra PC no podrá encontrarte: " + ex.Message); });
        _joinRefused = false;
        HostAdmission? admission = null;
        AdmittedConnection? guest = null;
        try
        {
        while (!ct.IsCancellationRequested)
        {
            PeerConnection connection;
            guest = null;
            var guestCode = _config.Role == PeerRole.Client ? SessionDiscovery.Normalize(_config.JoinCode) : null;
            try
            {
                if (_config.Role == PeerRole.Server) StatusChanged?.Invoke($"Sesión abierta — código {SessionDiscovery.Pretty(_config.SessionCode)}");
                else StatusChanged?.Invoke(guestCode is not null ? "Buscando la sesión en tu red..." : $"Conectando a {_config.PeerHost}:{_config.Port}...");

                if (_config.Role == PeerRole.Server)
                {
                    admission ??= CreateAdmission(ct);
                    guest = await admission.Guests.ReadAsync(ct);
                    connection = guest.Connection;
                }
                else if (guestCode is not null)
                {
                    var (host, how) = await SessionDiscovery.FindHostAsync(guestCode, _config.Port, TimeSpan.FromSeconds(6), ct);
                    if (host is not null) Logger.Info($"Sesión encontrada en {host.Address} (búsqueda {how}).");
                    var hostAddress = host?.Address.ToString();
                    var hostPort = host?.Port ?? _config.Port;
                    if (host is null && !string.IsNullOrWhiteSpace(_config.PeerHost))
                    {
                        // The network blocked the automatic search: try the address typed by hand.
                        hostAddress = _config.PeerHost.Trim();
                        StatusChanged?.Invoke($"No la encontré sola. Probando con {hostAddress}...");
                    }
                    if (hostAddress is null)
                    {
                        _joinNotice = "No encontré esa sesión. Revisa el código, que la otra PC tenga TecLink abierto en la misma red, y que su Firewall lo permita (botón «Permitir en el Firewall»).";
                        StatusChanged?.Invoke(_joinNotice);
                        await Task.Delay(2500, ct);
                        continue;
                    }
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attempt.CancelAfter(TimeSpan.FromSeconds(6));
                    try { connection = await PeerServer.ConnectAsync(hostAddress, hostPort, attempt.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("la otra PC no respondió (¿Firewall?)"); }
                }
                else connection = await PeerServer.ConnectAsync(_config.PeerHost, _config.Port, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (admission is not null) { try { await admission.DisposeAsync(); } catch (Exception) { } admission = null; }
                if (guestCode is not null) _joinNotice = $"No pude conectar: {ex.Message}. Reintentando...";
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

            connection.Disconnected += () => disconnectedTcs.TrySetResult();
            if (guest is not null) guest.Attach(OnMessageReceived); // the host's admission is already receiving; this replays the code and the Hello
            else { connection.MessageReceived += OnMessageReceived; connection.StartReceiving(); }
            if (connection.IsDisconnected) disconnectedTcs.TrySetResult();

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

            // A host keeps listening while a guest is connected: a valid newcomer replaces a link that died silently.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var newcomer = admission is not null ? admission.Guests.WaitToReadAsync(waitCts.Token).AsTask() : Task.Delay(Timeout.Infinite, waitCts.Token);
            await Task.WhenAny(disconnectedTcs.Task, newcomer, Task.Delay(Timeout.Infinite, ct));
            var replaced = admission is not null && newcomer.IsCompletedSuccessfully && !disconnectedTcs.Task.IsCompleted && !ct.IsCancellationRequested;
            waitCts.Cancel();
            if (replaced) { Logger.Info("Entró un invitado nuevo con el código: reemplaza la conexión anterior."); StatusChanged?.Invoke("Un invitado nuevo reemplaza la conexión anterior..."); }
            _dropCurrent = null;
            _sessionMode = false; _joined = false;
            if (_config.Role == PeerRole.Client) _management = false;

            if (_peerStatus == "connected") _peerStatus = "lost";
            StatusChanged?.Invoke(ct.IsCancellationRequested ? "TecLink apagado" : _peerStatus == "left" ? $"{_remoteName} salió de la sesión." : _peerStatus == "lost" ? $"{_remoteName} se desconectó." : "Conexión perdida. Reintentando...");
            _connection = null;
            _remoteKnown = false;
            _peerControlling = false;
            _peerExtras = false;
            _screenHost.Stop(); _tilesForPeer.Stop();
            Files.Abort();
            _clipboard.BeginInvoke(() =>
            {
                _viewInput = false;
                SwitchToLocal(force: true);
                ReleaseAllInjectedInput();
                ViewerCloseRequested?.Invoke();
            });
            if (replaced) { try { await connection.SendAsync(WireMessage.JoinResult(false, "Otra PC entró con el código y tomó la sesión.")).WaitAsync(TimeSpan.FromMilliseconds(600)); } catch (Exception) { } }
            await connection.DisposeAsync();

            if (ct.IsCancellationRequested) return;
            if (_joinRefused && _config.Role == PeerRole.Client)
            {
                // Retrying would only repeat the refusal (or fight another PC for the session): wait for the user.
                StatusChanged?.Invoke(_joinNotice);
                await Task.Delay(Timeout.Infinite, ct);
            }
            if (_config.Role == PeerRole.Server) continue; // the next guest, if any, is already waiting in the queue
            await Task.Delay(string.IsNullOrEmpty(_joinNotice) ? 2000 : 5000, ct); // refused: don't hammer the host
        }
        }
        finally { if (admission is not null) await admission.DisposeAsync(); }
    }

    private HostAdmission CreateAdmission(CancellationToken ct)
    {
        var admission = new HostAdmission(_config.Port, () => _config.SessionCode, TimeSpan.FromSeconds(5), ct);
        admission.Refused += (who, why) => { Logger.Info($"Conexión de {who} rechazada: {why}."); if (why.StartsWith("código")) StatusChanged?.Invoke($"Alguien en {who} intentó entrar con un código incorrecto."); };
        return admission;
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
        if (msg.Type == MessageType.ScreenTile)
        {
            var shown = _remoteDisplays;
            if (msg.Index >= 0 && msg.Index < shown.Length) TileReceived?.Invoke(true, shown[msg.Index].Id, msg.Data);
            return;
        }
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
                _remoteKnown = true; _peerStatus = "connected";
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
                // In a session the owner's map rules: it is completed here (every screen placed, edges joined) and
                // sent to the guest right away, so the mouse crosses without anyone having to press "save".
                if (_sessionMode && _config.Role == PeerRole.Server) AdoptOwnerMap(); else RefreshSpatialRoutes();
                if (_peerMapSupported && _config.UseSpatialMap && _spatialValid &&
                    (_config.Role == PeerRole.Server || (!_sessionMode && !msg.HasSpatialMap)))
                    _ = _connection!.SendAsync(WireMessage.Map(Guid.NewGuid().ToString("N"), _config.ScreenPositions.ToArray()));
                StatusChanged?.Invoke($"Vinculado con {msg.MachineName} ({_remoteWidth}x{_remoteHeight})");
                break;
            case MessageType.JoinResult:
                _joinAnswered = true;
                if (msg.Accepted) { _joinNotice = ""; _sessionMode = true; }
                else
                {
                    _joinNotice = string.IsNullOrWhiteSpace(msg.Text) ? "La sesión no te aceptó." : msg.Text!;
                    _joinRefused = true;
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
            case MessageType.Leave:
                _peerStatus = "left"; _remoteKnown = false;
                StatusChanged?.Invoke(_config.Role == PeerRole.Server ? $"{_remoteName} salió de la sesión." : $"{_remoteName} cerró la sesión.");
                ControlChanged?.Invoke(_config.Role == PeerRole.Server ? $"{_remoteName} salió de la sesión" : $"{_remoteName} cerró la sesión");
                RevokeManagement();
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
            case MessageType.Yield:
                // The other PC wants its own mouse and keyboard back: stop controlling it. Nobody takes control of us.
                if (_viewInput) { ViewerCloseRequested?.Invoke(); SetViewInput(false); }
                if (_mode == BridgeMode.Remote) SwitchToLocal();
                StatusChanged?.Invoke($"{_remoteName} recuperó su mouse y su teclado");
                break;
            case MessageType.ScreenRemoval: HandleRemoval(msg.Text ?? ""); break;
            case MessageType.ScreenWatch: HandleWatch(msg.Text ?? ""); break;
            case MessageType.ShareOffer: HandleShareOffer(msg.Text ?? ""); break;
            case MessageType.ShareFetch: HandleShareFetch(msg.Text ?? ""); break;
            case MessageType.SwitchToLocal:
                _lastLocalPoint = null;
                _peerControlling = false;
                ReleaseAllInjectedInput(); // defensive: peer says it's done, make sure nothing stayed held here
                StatusChanged?.Invoke("La otra PC soltó el mando");
                ControlChanged?.Invoke("Tienes el mando local");
                break;
            case MessageType.SpatialMap:
                if (!_peerMapSupported) break;
                if (_sessionMode && _config.Role == PeerRole.Server)
                {
                    // Only the owner of the session arranges the screens: a guest's map is refused and the owner's is sent back.
                    _ = _connection?.SendAsync(WireMessage.MapAck(msg.RequestId ?? "", false, "El orden de las pantallas lo decide el dueño de la sala."));
                    if (_spatialValid) _ = _connection?.SendAsync(WireMessage.Map(Guid.NewGuid().ToString("N"), _config.ScreenPositions.ToArray()));
                    break;
                }
                try
                {
                    StoreSpatialMap(msg.Placements.Select(p => p with { Remote = !p.Remote }).ToList(),
                        _config.AutomaticSwitching, _config.Theme, fromPeer: true);
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
            if (_config.UseSpatialMap && _spatialValid && _connection is not null && _remoteKnown)
            {
                // While the other PC drives this one, its moves are injected (and skipped by the hook). The person
                // sitting here can still use their own mouse and cross to the other PC: that takes the control back.
                if (_peerControlling) _lastLocalPoint = null;
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
            peerStatus = _peerStatus,
            netMode = !NetworkActive ? "idle" : _config.Role == PeerRole.Server ? "host" : "guest",
            sessionCode = SessionDiscovery.Pretty(SessionDiscovery.Normalize(_config.SessionCode) ?? ""),
            joinCode = SessionDiscovery.Pretty(SessionDiscovery.Normalize(_config.JoinCode) ?? ""),
            sessionMode = _sessionMode, management = _management, joinNotice = _joinNotice, maxScreens = MaxScreens,
            addresses = CachedAddresses(), firewall = FirewallAllowed(), peerAddress = _config.PeerHost,
            pointerSpeed = CurrentSpeedLevel(), cursorStyle = CursorStyler.Normalize(_config.CursorStyle),
            cursorSize = CursorStyler.ClampSize(_config.CursorSizePercent), extras = CanUseExtras,
            transfer = Files.LastStatus, receiveFolder = Files.ReceiveFolder,
            canArrange = CanArrange, shares = SharesView(), autoReceive = _config.AutoReceive, controlling = _viewInput,
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
            _ = peer.SendAsync(WireMessage.Yield()); // ask an active remote controller to let go first
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
            Logger.Info("Cambió el número o tamaño de pantallas: " + string.Join("; ", LocalDisplays().Select(d => $"{d.Id} {d.Width}x{d.Height} en {d.Left},{d.Top}")));
            var owner = _sessionMode && _config.Role == PeerRole.Server && _remoteKnown;
            if (owner) AdoptOwnerMap(); else RefreshSpatialRoutes();
            if (_connection is { } connection)
            {
                _ = SendDesktopAsync(connection);
                if (owner && _peerMapSupported && _spatialValid) _ = connection.SendAsync(WireMessage.Map(Guid.NewGuid().ToString("N"), _config.ScreenPositions.ToArray()));
            }
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
        _watchdog?.Dispose();
        _screenHost.Dispose(); _tilesForPeer.Dispose(); _tilesLocal.Dispose();
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

    /// <summary>
    /// Who may remove a screen from the map: ONLY the session host, and ONLY screens that came from a guest.
    /// Nobody can remove their own screens, and a guest cannot remove anything (it keeps what the host decided).
    /// </summary>
    public List<ScreenPlacement> ApplyHiddenRules(IEnumerable<ScreenPlacement> positions, bool fromPeer) =>
        SpatialMap.ApplyHiddenRules(positions, _config.ScreenPositions, _config.Role == PeerRole.Server, fromPeer);

    private void StoreSpatialMap(List<ScreenPlacement> positions, bool automatic, string theme, bool fromPeer = false)
    {
        positions = ApplyHiddenRules(positions, fromPeer);
        _ = SpatialMap.Build(LocalDisplays(), _remoteDisplays, positions);
        var previous = (_config.ScreenPositions, _config.UseSpatialMap, _config.AutomaticSwitching, _config.Theme);
        _config.ScreenPositions = positions; _config.UseSpatialMap = true; _config.AutomaticSwitching = automatic;
        _config.Theme = theme is "dark" or "light" ? theme : "system";
        try { _config.Save(); }
        catch { (_config.ScreenPositions, _config.UseSpatialMap, _config.AutomaticSwitching, _config.Theme) = previous; throw; }
        if (_peerControlling && _connection is { } peer) { _peerControlling = false; _ = peer.SendAsync(WireMessage.Yield()); }
        SwitchToLocal(force: true); ReleaseAllInjectedInput(); _lastLocalPoint = null;
        RefreshSpatialRoutes();
    }

    /// <summary>A guest of a session cannot arrange screens: the owner of the session decides and this PC follows.</summary>
    public bool CanArrange => !(_config.Role == PeerRole.Client && !string.IsNullOrEmpty(_config.JoinCode));

    private void AdoptOwnerMap()
    {
        var completed = SpatialMap.Complete(LocalDisplays(), _remoteDisplays, _config.ScreenPositions);
        if (!_config.UseSpatialMap || !completed.SequenceEqual(_config.ScreenPositions))
        {
            _config.ScreenPositions = completed; _config.UseSpatialMap = true;
            try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        }
        RefreshSpatialRoutes();
    }

    public async Task<string> ApplySpatialMapAsync(List<ScreenPlacement> positions, bool automatic, string theme)
    {
        if (!CanArrange)
        {
            _config.AutomaticSwitching = automatic; _config.Theme = theme is "dark" or "light" ? theme : "system";
            _config.Save();
            return "Ajustes guardados. El orden de las pantallas lo decide el dueño de la sala.";
        }
        // Removing a guest's screen needs that guest's consent (RequestRemoveScreen): saving the map can only bring screens back.
        positions = positions.Select(p => p.Hidden && !_config.ScreenPositions.Any(e => e.Id == p.Id && e.Remote == p.Remote && e.Hidden) ? p with { Hidden = false } : p).ToList();
        StoreSpatialMap(positions, automatic, theme);
        if (!_peerMapSupported || _connection is null || !_remoteKnown)
            return "Mapa guardado en esta PC. Actualiza la otra PC para sincronizarlo.";
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mapRequests[id] = completion;
        try
        {
            await _connection.SendAsync(WireMessage.Map(id, _config.ScreenPositions.ToArray())); // the version after the hidden rules
            var answer = await completion.Task.WaitAsync(TimeSpan.FromSeconds(4));
            return answer.Accepted ? "Mapa guardado en ambas PCs." : "Guardado aquí. La otra PC no pudo aplicarlo: " + answer.Text;
        }
        catch (TimeoutException) { return "Mapa guardado aquí. La otra PC no confirmó a tiempo."; }
        finally { _mapRequests.Remove(id); }
    }
}
