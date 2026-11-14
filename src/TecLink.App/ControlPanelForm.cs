using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using TecLink.Core;

namespace TecLink.App;

public sealed class ControlPanelForm : Form
{
    private const string Origin = "https://panel.teclink.local";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(8, 8, 8) };
    private readonly EdgeBridge _bridge;
    private readonly Func<string> _status;
    private readonly Action _openViewer;
    private bool _ready;
    private bool _shutdown;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private sealed class MapRequest
    {
        public MapRequest() { }
        public List<ScreenRoute> Routes { get; set; } = new();
        public List<string> Order { get; set; } = new();
        public bool Automatic { get; set; } = true;
        public string Theme { get; set; } = "system";
        public List<ScreenPlacement>? Placements { get; set; }
    }

    private readonly Action<bool> _setPower;
    private readonly Action<string, string?, string?> _session;
    private readonly Action<byte[]> _cameraHandler;
    private readonly Action<string> _peerFilesHandler;
    private readonly Action<string, string> _chatHandler;
    private readonly Action<byte[]> _voiceHandler;
    private readonly Action<bool, string, byte[]> _tileHandler;
    private readonly Action<string, string> _removalHandler;
    private readonly Action<string> _noticeHandler;
    private readonly Action _controlOffHandler;
    private bool _tileControl;

    public ControlPanelForm(EdgeBridge bridge, Func<string> status, Action openViewer, Action<bool> setPower, Action<string, string?, string?> session)
    {
        _bridge = bridge; _status = status; _openViewer = openViewer; _setPower = setPower; _session = session;
        Text = "TecLink — pantallas y salidas";
        Icon = TrayAppContext.AppIcon;
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(370, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        _cameraHandler = frame => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "cameraFrame", data = Convert.ToBase64String(frame) })); };
        bridge.CameraFrameReceived += _cameraHandler;
        _peerFilesHandler = json => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { try { Post(new { type = "peerFiles", items = JsonDocument.Parse(json).RootElement.Clone() }); } catch (JsonException) { } }); };
        bridge.PeerFilesReceived += _peerFilesHandler;
        _chatHandler = (from, text) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "chat", from, text, at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() })); };
        _voiceHandler = pcm => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "voice", data = Convert.ToBase64String(pcm) })); };
        bridge.ChatReceived += _chatHandler; bridge.VoiceReceived += _voiceHandler;
        _tileHandler = (remote, id, jpeg) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "tile", remote, id, data = Convert.ToBase64String(jpeg) })); };
        _removalHandler = (id, label) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { Post(new { type = "removeAsk", id, label, from = _bridge.RemoteName }); Show(); Activate(); }); };
        _noticeHandler = text => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { Post(new { type = "toast", message = text }); RefreshState(); }); };
        _controlOffHandler = () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { _tileControl = false; _bridge.SetViewInput(false); Post(new { type = "controlOff" }); }); };
        bridge.TileReceived += _tileHandler; bridge.RemovalAsked += _removalHandler; bridge.Notice += _noticeHandler; bridge.ViewerCloseRequested += _controlOffHandler;
        // Controlling the other PC from a tile only lasts while this window is in front.
        Deactivate += (_, _) => { if (_tileControl) { _tileControl = false; _bridge.SetViewInput(false); Post(new { type = "controlOff" }); } };
        VisibleChanged += (_, _) => { if (!Visible) _bridge.WatchScreens(false, 0); else Post(new { type = "shown" }); };
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, e) => { if (!_shutdown && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    private async Task InitializeAsync()
    {
        if (_ready) return;
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TecLink", "Panel");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await _web.EnsureCoreWebView2Async(environment);
            // The panel files change with every rebuild; never let WebView2 serve a cached
            // copy of them across runs, or UI edits can silently stop working.
            try { await _web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage); }
            catch (Exception ex) { Logger.LogError(ex); }
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping("panel.teclink.local", Path.Combine(AppContext.BaseDirectory, "Ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            _web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
            _web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            _web.CoreWebView2.WebMessageReceived += OnMessage;
            // The video-call bubble needs the camera, and only the panel's own pages may ask for it.
            _web.CoreWebView2.PermissionRequested += (_, e) =>
            {
                e.State = e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone && e.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)
                    ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            };
            _web.CoreWebView2.NavigationCompleted += (_, _) => RefreshState();
            _ready = true;
            _web.Source = new Uri(Origin + "/index.html");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            _web.Visible = false;
            Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(24), Font = new Font("Segoe UI", 12),
                Text = "No se pudo abrir el panel visual. TecLink sigue disponible junto al reloj.\n\n" + ex.Message });
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready": RefreshState(); break;
                case "save":
                    var request = root.GetProperty("map").Deserialize<MapRequest>(Json) ?? throw new ArgumentException("El mapa está vacío.");
                    var message = request.Placements is { } positions
                        ? await _bridge.ApplySpatialMapAsync(positions, request.Automatic, request.Theme)
                        : null;
                    if (request.Placements is null) _bridge.ApplyMap(request.Routes, request.Order, request.Automatic, request.Theme);
                    Post(new { type = "saved", state = _bridge.ViewState(_status()), message });
                    break;
                case "toggleControl": _bridge.ToggleControl(); RefreshState(); break;
                case "pointer":
                    _bridge.ApplyPointerSettings(root.GetProperty("speed").GetInt32(), root.GetProperty("style").GetString(), root.GetProperty("size").GetInt32());
                    RefreshState();
                    break;
                case "viewScreen": _openViewer(); break;
                case "pickFiles":
                    using (var files = new OpenFileDialog { Multiselect = true, Title = "Compartir archivos con la sesión" })
                        if (files.ShowDialog(this) == DialogResult.OK) { _bridge.ShareFiles(files.FileNames); RefreshState(); }
                    break;
                case "pickFolder":
                    using (var folder = new FolderBrowserDialog { Description = "Compartir una carpeta completa con la sesión", UseDescriptionForTitle = true })
                        if (folder.ShowDialog(this) == DialogResult.OK) { _bridge.ShareFiles(new[] { folder.SelectedPath }); RefreshState(); }
                    break;
                case "shareAction": _bridge.ShareAction(root.GetProperty("id").GetString() ?? "", root.GetProperty("action").GetString() ?? ""); RefreshState(); break;
                case "autoReceive": _bridge.SetAutoReceive(root.GetProperty("on").GetBoolean()); RefreshState(); break;
                case "watch":
                    _bridge.WatchScreens(root.GetProperty("on").GetBoolean(), root.TryGetProperty("width", out var width) ? width.GetInt32() : 480,
                        root.TryGetProperty("focusRemote", out var fr) && fr.ValueKind is JsonValueKind.True or JsonValueKind.False ? fr.GetBoolean() : null,
                        root.TryGetProperty("focusId", out var fid) ? fid.GetString() : null);
                    break;
                case "tileControl":
                    _tileControl = root.GetProperty("on").GetBoolean();
                    _bridge.SetViewInput(_tileControl); RefreshState();
                    break;
                case "tileMove": _bridge.TileMove(root.GetProperty("id").GetString() ?? "", root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble()); break;
                case "tileButton":
                    _bridge.ViewerButton(root.GetProperty("button").GetInt32() switch { 2 => TecLink.Core.Protocol.MouseButtonKind.Right, 1 => TecLink.Core.Protocol.MouseButtonKind.Middle, _ => TecLink.Core.Protocol.MouseButtonKind.Left }, root.GetProperty("down").GetBoolean());
                    break;
                case "tileWheel": _bridge.ViewerWheel(root.GetProperty("delta").GetInt32()); break;
                case "removeAsk": _bridge.RequestRemoveScreen(root.GetProperty("id").GetString() ?? ""); break;
                case "removeAnswer": _bridge.AnswerRemoval(root.GetProperty("id").GetString() ?? "", root.GetProperty("accept").GetBoolean()); break;
                case "repair":
                    // One button for "it does not connect": allow TecLink in the Firewall (Windows asks once) and report what the network shows.
                    _ = Task.Run(async () =>
                    {
                        if (!EdgeBridge.CheckFirewall()) _bridge.AllowFirewall();
                        var lines = await _bridge.DiagnoseAsync(CancellationToken.None);
                        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { Post(new { type = "diagnostic", lines }); RefreshState(); });
                    });
                    break;
                case "dropFiles":
                    var dropped = e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
                    if (dropped.Length > 0) { _bridge.ShareFiles(dropped); RefreshState(); }
                    else Post(new { type = "error", message = "No se pudo leer lo que soltaste. Usa «Elegir archivos» o «Elegir carpeta»." });
                    break;
                case "openReceived":
                    Directory.CreateDirectory(_bridge.Files.ReceiveFolder);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_bridge.Files.ReceiveFolder) { UseShellExecute = true });
                    break;
                case "setPower": _setPower(root.GetProperty("on").GetBoolean()); RefreshState(); break;
                case "session":
                    _session(root.GetProperty("action").GetString() ?? "", root.TryGetProperty("code", out var code) ? code.GetString() : null,
                        root.TryGetProperty("address", out var address) ? address.GetString() : null);
                    RefreshState();
                    break;
                case "cameraFrame":
                    if (root.GetProperty("data").GetString() is { Length: > 0 and < 700_000 } b64) _bridge.SendCameraFrame(Convert.FromBase64String(b64));
                    break;
                case "chat":
                    if (!_bridge.SendChat(root.GetProperty("text").GetString() ?? "")) Post(new { type = "toast", message = "No hay nadie conectado para recibir el mensaje." });
                    break;
                case "voice":
                    if (root.GetProperty("data").GetString() is { Length: > 0 and < 90_000 } voice) _bridge.SendVoice(Convert.FromBase64String(voice));
                    break;
                case "listFiles": Post(new { type = "myFiles", items = _bridge.Files.List() }); break;
                case "listPeerFiles": _bridge.RequestPeerFiles(); break;
                case "fileAction":
                    var mine = _bridge.Files.PathInside(root.GetProperty("name").GetString() ?? "");
                    if (mine is null) { Post(new { type = "error", message = "Ese archivo ya no está en la carpeta de TecLink." }); break; }
                    switch (root.GetProperty("action").GetString())
                    {
                        case "send": _ = _bridge.Files.SendAsync(new[] { mine }); break;
                        case "desktop": _bridge.Files.CopyToDesktop(mine); Post(new { type = "toast", message = "Puesto en tu escritorio." }); break;
                        case "show": System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{mine}\""); break;
                    }
                    break;
                case "peerFileAction": _bridge.PeerFileCommand(root.GetProperty("action").GetString() ?? "", root.GetProperty("name").GetString() ?? ""); break;
                case "diagnose":
                    _ = Task.Run(async () =>
                    {
                        var lines = await _bridge.DiagnoseAsync(CancellationToken.None);
                        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "diagnostic", lines }));
                    });
                    break;
                case "freeInput": _bridge.FreeInput(); RefreshState(); break;
                case "setManagement": _bridge.SetManagement(root.GetProperty("on").GetBoolean()); RefreshState(); break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Post(new { type = "error", message = ex.Message }); }
    }

    private void Post(object message) { if (_ready && !_web.IsDisposed) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json)); }
    public void RefreshState() => Post(new { type = "state", state = _bridge.ViewState(_status()) });
    public void Shutdown() { _bridge.TileReceived -= _tileHandler; _bridge.RemovalAsked -= _removalHandler; _bridge.Notice -= _noticeHandler; _bridge.ViewerCloseRequested -= _controlOffHandler; _bridge.WatchScreens(false, 0);_bridge.CameraFrameReceived -= _cameraHandler; _bridge.PeerFilesReceived -= _peerFilesHandler; _bridge.ChatReceived -= _chatHandler; _bridge.VoiceReceived -= _voiceHandler; _shutdown = true; Close(); Dispose(); }
}
