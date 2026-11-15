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
    private readonly Func<bool> _uninstall;
    private readonly Func<string, bool> _installUpdate;
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
    private readonly Action<bool, string?> _fullScreen;
    private readonly Action<string, string?, string?> _session;
    private readonly Action<string, byte[]> _cameraHandler;
    private readonly Action<string> _peerFilesHandler;
    private readonly Action<string, string> _chatHandler;
    private readonly Action<string, byte[]> _voiceHandler;
    private readonly Action _roomHandler;
    private readonly Action<string> _controlAskHandler;
    private readonly Action _controlGrantHandler;
    private readonly Action<bool, string, byte[]> _tileHandler;
    private readonly Action<string, string> _removalHandler;
    private readonly Action<string> _noticeHandler;
    private readonly Action _controlOffHandler;
    private bool _tileControl;

    public ControlPanelForm(EdgeBridge bridge, Func<string> status, Action openViewer, Action<bool> setPower, Action<string, string?, string?> session, Action<bool, string?> fullScreen, Func<bool> uninstall, Func<string, bool> installUpdate)
    {
        _uninstall = uninstall; _installUpdate = installUpdate;
        _bridge = bridge; _status = status; _openViewer = openViewer; _setPower = setPower; _session = session; _fullScreen = fullScreen;
        Text = $"TecLink {AppInfo.VersionText} — pantallas y salidas";
        Icon = TrayAppContext.AppIcon;
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(370, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        _cameraHandler = (id, frame) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "cameraFrame", id, data = Convert.ToBase64String(frame) })); };
        bridge.CameraFrameReceived += _cameraHandler;
        _peerFilesHandler = json => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { try { Post(new { type = "peerFiles", items = JsonDocument.Parse(json).RootElement.Clone() }); } catch (JsonException) { } }); };
        bridge.PeerFilesReceived += _peerFilesHandler;
        _chatHandler = (from, text) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "chat", from, text, at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() })); };
        _voiceHandler = (id, pcm) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "voice", id, data = Convert.ToBase64String(pcm) })); };
        bridge.ChatReceived += _chatHandler; bridge.VoiceReceived += _voiceHandler;
        _controlAskHandler = from => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { Post(new { type = "controlAsk", from }); Show(); Activate(); }); };
        _controlGrantHandler = () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "controlGranted" })); };
        bridge.ControlAsked += _controlAskHandler; bridge.ControlGranted += _controlGrantHandler;
        _roomHandler = () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(RefreshState); };
        bridge.RoomChanged += _roomHandler;
        _tileHandler = (remote, id, jpeg) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(new { type = "tile", remote, id, data = Convert.ToBase64String(jpeg) })); };
        _removalHandler = (id, label) => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { Post(new { type = "removeAsk", id, label, from = _bridge.RemoteName }); Show(); Activate(); }); };
        _noticeHandler = text => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { Post(new { type = "toast", message = text }); RefreshState(); }); };
        _controlOffHandler = () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { _tileControl = false; _bridge.SetViewInput(false); Post(new { type = "controlOff" }); }); };
        bridge.TileReceived += _tileHandler; bridge.RemovalAsked += _removalHandler; bridge.Notice += _noticeHandler; bridge.ViewerCloseRequested += _controlOffHandler;
        // Controlling the other PC from a tile only lasts while this window is in front.
        // The button stays on until the person turns it off. While another window is in front the keyboard goes back to this PC
        // (it must never be captured in the background) and control resumes by itself when the panel is used again.
        Deactivate += (_, _) => { if (_tileControl) _bridge.SetViewInput(false); };
        Activated += (_, _) => { if (_tileControl) { _bridge.SetViewInput(true); RefreshState(); } };
        VisibleChanged += (_, _) => { if (!Visible) { _bridge.WatchScreens(false, 0); SleepPanelIfIdle(); } else { ResumePanel(); Post(new { type = "shown" }); } };
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
                    // The panel never appears inside the live pictures: without this, a tile of the screen that holds this very
                    // window shows the window showing the tile showing the window... (an endless tunnel, and a blurry one).
                    if (IsHandleCreated) TecLink.App.Native.Win32.SetWindowDisplayAffinity(Handle, root.GetProperty("on").GetBoolean() ? TecLink.App.Native.Win32.WDA_EXCLUDEFROMCAPTURE : TecLink.App.Native.Win32.WDA_NONE);
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
                case "detectDisplays":
                    // Windows' own "Extend these displays" (the same as Win+P): asks the graphics driver to look for monitors again.
                    _ = Task.Run(async () =>
                    {
                        try { using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("DisplaySwitch.exe", "/extend") { UseShellExecute = false, CreateNoWindow = true }); p?.WaitForExit(8000); }
                        catch (System.ComponentModel.Win32Exception) { }
                        await Task.Delay(2500);
                        var count = Screen.AllScreens.Length;
                        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { Post(new { type = "toast", message = count > 1 ? $"Windows detectó {count} pantallas." : "Windows sigue viendo una sola pantalla. Revisa el cable HDMI y que el monitor esté en esa entrada; también prueba Windows + Ctrl + Shift + B." }); RefreshState(); });
                    });
                    break;
                case "fullscreen":
                    var remoteScreen = root.GetProperty("remote").GetBoolean();
                    if (remoteScreen && !(_bridge.CanUseExtras && _bridge.ManagementAllowed)) { Post(new { type = "toast", message = "Activa «Gestión de pantallas» para ver la pantalla de la otra PC." }); break; }
                    if (!remoteScreen && Screen.AllScreens.Length < 2) { Post(new { type = "toast", message = "Necesitas otro monitor para verla a pantalla completa: en un solo monitor usa «Extender pantalla»." }); break; }
                    _fullScreen(remoteScreen, root.GetProperty("id").GetString());
                    break;
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
                case "downloadManual":
                    try
                    {
                        // El manual viaja dentro de la app (Ui\manual); se guarda en Descargas y se abre.
                        var source = Path.Combine(AppContext.BaseDirectory, "Ui", "manual", "TecLink-Manual.pdf");
                        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                        Directory.CreateDirectory(downloads);
                        var target = Path.Combine(downloads, "TecLink-Manual.pdf");
                        File.Copy(source, target, true);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                        Post(new { type = "toast", message = "Manual guardado en Descargas: TecLink-Manual.pdf" });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Post(new { type = "error", message = "No se pudo guardar el manual. Revisa que la carpeta Descargas esté disponible." }); }
                    break;
                case "setPower": _setPower(root.GetProperty("on").GetBoolean()); RefreshState(); break;
                case "checkUpdate":
                    // Only when the person presses the button: TecLink never contacts anything by itself.
                    _ = Task.Run(async () =>
                    {
                        object result;
                        try
                        {
                            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                            http.DefaultRequestHeaders.UserAgent.ParseAdd("TecLink-update-check");
                            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/fuchogallery-droid/teclink/releases/latest"));
                            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                            var page = doc.RootElement.GetProperty("html_url").GetString() ?? "";
                            var current = typeof(ControlPanelForm).Assembly.GetName().Version ?? new Version(0, 0);
                            var newer = Version.TryParse(tag.TrimStart('v'), out var latest) && latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
                            var asset = doc.RootElement.TryGetProperty("assets", out var assets) ? assets.EnumerateArray().Select(x => x.GetProperty("browser_download_url").GetString() ?? "").FirstOrDefault(u => u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ?? "" : "";
                            result = new { type = "update", ok = true, newer, latest = tag.TrimStart('v'), current = $"{current.Major}.{current.Minor}.{Math.Max(0, current.Build)}", url = page, asset };
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
                        { result = new { type = "update", ok = false }; }
                        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(result));
                    });
                    break;
                case "updateCheck":
                    _ = Task.Run(async () =>
                    {
                        var info = await Updater.CheckAsync();
                        var key = _bridge.UpdateKey;
                        var result = new { type = "upd", phase = "checked", ok = info.Ok, newer = info.Newer, latest = info.Latest, current = info.Current, enabled = info.Enabled, message = info.Message, buyUrl = info.BuyUrl, needKey = info.NeedKey, keyOk = Updater.KeyAccepted(info, key), size = info.Size };
                        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(result));
                    });
                    break;
                case "updateApply":
                    {
                        var typedKey = root.TryGetProperty("key", out var keyText) ? keyText.GetString() ?? "" : "";
                        _ = Task.Run(async () =>
                        {
                            void Say(object o) { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => Post(o)); }
                            try
                            {
                                var info = await Updater.CheckAsync();
                                if (!info.Ok) { Say(new { type = "upd", phase = "error", message = "No se pudo consultar. Revisa tu conexión a internet." }); return; }
                                if (!info.Enabled) { Say(new { type = "upd", phase = "error", message = string.IsNullOrWhiteSpace(info.Message) ? "Las actualizaciones están en pausa por ahora." : info.Message }); return; }
                                if (!info.Newer) { Say(new { type = "upd", phase = "error", message = "Ya tienes la versión más reciente." }); return; }
                                if (!Updater.KeyAccepted(info, typedKey.Length > 0 ? typedKey : _bridge.UpdateKey)) { Say(new { type = "upd", phase = "key", message = "Esta actualización necesita una clave válida." }); return; }
                                if (typedKey.Length > 0) _bridge.UpdateKey = typedKey;
                                var path = await Updater.DownloadAsync(info, pct => Say(new { type = "upd", phase = "progress", percent = pct }));
                                Say(new { type = "upd", phase = "installing", latest = info.Latest });
                                await Task.Delay(800);
                                if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!_installUpdate(path)) Post(new { type = "upd", phase = "error", message = "No se pudo iniciar el instalador. Está en: " + path }); });
                            }
                            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
                            { Say(new { type = "upd", phase = "error", message = ex is InvalidDataException ? ex.Message : "La descarga falló. Inténtalo de nuevo." }); }
                        });
                    }
                    break;
                case "uninstall":
                    // The uninstaller of Windows asks once more by itself; TecLink only closes after launching it.
                    if (!_uninstall()) Post(new { type = "toast", message = "TecLink no se instaló con el instalador, así que aquí no hay desinstalador. Si lo quieres quitar, hazlo desde Configuración de Windows → Aplicaciones." });
                    break;
                case "openUrl":
                    var wanted = root.GetProperty("url").GetString() ?? "";
                    if (wanted.StartsWith("https://github.com/fuchogallery-droid/teclink/releases", StringComparison.Ordinal) || wanted.StartsWith("https://technocrazy.org", StringComparison.Ordinal))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(wanted) { UseShellExecute = true });
                    break;
                case "setOnline":
                    _bridge.SetOnline(root.GetProperty("on").GetBoolean(), root.TryGetProperty("relay", out var relayText) ? relayText.GetString() : null);
                    if (_bridge.HostingNow) _session("open", null, null); // the room opens or closes on the internet with the session
                    RefreshState();
                    break;
                case "refreshRelay":
                    _ = Task.Run(async () =>
                    {
                        var changed = await _bridge.RefreshRelayNowAsync();
                        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (changed && _bridge.HostingNow) _session("open", null, null); Post(new { type = "toast", message = changed ? "Servidor en línea actualizado." : "El servidor en línea ya estaba al día." }); RefreshState(); });
                    });
                    break;
                case "renewOnline": _bridge.RenewOnlineInvite(); if (_bridge.HostingNow) _session("open", null, null); RefreshState(); break;
                case "setClipboard": _bridge.SetShareClipboard(root.GetProperty("on").GetBoolean()); RefreshState(); break;
                case "setRoomMode": _bridge.SetRoomMode(root.GetProperty("mode").GetString() ?? ""); RefreshState(); break;
                case "controlRequest": _bridge.RequestControl(); RefreshState(); break;
                case "controlAnswer": _bridge.AnswerControl(root.GetProperty("accept").GetBoolean()); RefreshState(); break;
                case "controlEnd": _bridge.EndControl(); break;
                case "useMouse": _bridge.UseMouseWith(root.GetProperty("id").GetString() ?? ""); break;
                case "kickGuest": _bridge.KickGuest(root.GetProperty("id").GetString() ?? ""); break;
                case "roomFullscreen": SetRoomFullscreen(root.GetProperty("on").GetBoolean()); break;
                case "setProfile":
                    _bridge.SetProfile(root.TryGetProperty("name", out var pName) ? pName.GetString() : null, root.TryGetProperty("pc", out var pPc) ? pPc.GetString() : null,
                        root.TryGetProperty("avatar", out var pAv) && pAv.ValueKind == System.Text.Json.JsonValueKind.String ? pAv.GetString() : null);
                    RefreshState();
                    break;
                case "joinAnswer":
                    {
                        bool? joinerIsMine = root.TryGetProperty("own", out var ownFlag) && ownFlag.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? ownFlag.GetBoolean() : null;
                        _bridge.AnswerJoin(root.GetProperty("id").GetString() ?? "", root.GetProperty("accept").GetBoolean(), joinerIsMine); RefreshState(); break;
                    }
                case "peerIsMine": _bridge.MarkPeerAsMine(root.GetProperty("mine").GetBoolean()); RefreshState(); break;
                case "saveRoom": _bridge.SaveRoom(); RefreshState(); break;
                case "forgetRoom": _bridge.ForgetRoom(root.GetProperty("code").GetString()); RefreshState(); break;
                case "checkRooms":
                    _ = Task.Run(async () =>
                    {
                        var found = await _bridge.CheckRoomsAsync(CancellationToken.None);
                        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { Post(new { type = "roomsPresence", found }); RefreshState(); });
                    });
                    break;
                case "enterRoom":
                    try
                    {
                        var roomCode = root.GetProperty("code").GetString();
                        _bridge.AskNextJoin();
                        _session("join", roomCode, _bridge.SavedRoomAddress(roomCode));
                    }
                    catch (ArgumentException ex) { Post(new { type = "toast", message = ex.Message }); }
                    RefreshState();
                    break;
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

    private bool _suspended;

    private void Post(object message)
    {
        if (!_ready || _web.IsDisposed) return;
        ResumePanel();
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    /// <summary>With the window hidden and nobody connected the panel's web engine is put to sleep (no timers, no CPU);
    /// it wakes up the instant something has to be shown.</summary>
    private void SleepPanelIfIdle()
    {
        if (!_ready || _web.IsDisposed || _suspended || Visible || _bridge.HasPeerConnected) return;
        try { _ = _web.CoreWebView2.TrySuspendAsync(); _suspended = true; } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private void ResumePanel()
    {
        if (!_suspended) return;
        try { _web.CoreWebView2.Resume(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        _suspended = false;
    }
    private FormBorderStyle _borderBefore; private FormWindowState _stateBefore; private bool _roomFull;

    /// <summary>The whole panel window covers its monitor while the room is shown full screen; everything is restored afterwards.</summary>
    private void SetRoomFullscreen(bool on)
    {
        if (on == _roomFull) return;
        _roomFull = on;
        if (on)
        {
            _borderBefore = FormBorderStyle; _stateBefore = WindowState;
            WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _borderBefore; WindowState = _stateBefore == FormWindowState.Minimized ? FormWindowState.Normal : _stateBefore;
        }
    }

    public void RefreshState() => Post(new { type = "state", state = _bridge.ViewState(_status()) });
    public void Shutdown() { _bridge.TileReceived -= _tileHandler; _bridge.RemovalAsked -= _removalHandler; _bridge.Notice -= _noticeHandler; _bridge.ViewerCloseRequested -= _controlOffHandler; _bridge.WatchScreens(false, 0);_bridge.CameraFrameReceived -= _cameraHandler; _bridge.PeerFilesReceived -= _peerFilesHandler; _bridge.ChatReceived -= _chatHandler; _bridge.VoiceReceived -= _voiceHandler; _bridge.RoomChanged -= _roomHandler; _bridge.ControlAsked -= _controlAskHandler; _bridge.ControlGranted -= _controlGrantHandler; _shutdown = true; Close(); Dispose(); }
}
