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

    public ControlPanelForm(EdgeBridge bridge, Func<string> status, Action openViewer)
    {
        _bridge = bridge; _status = status; _openViewer = openViewer;
        Text = "TecLink — pantallas y salidas";
        Icon = TrayAppContext.AppIcon;
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(370, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
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
                    using (var files = new OpenFileDialog { Multiselect = true, Title = "Enviar archivos a la otra PC" })
                        if (files.ShowDialog(this) == DialogResult.OK) _ = _bridge.Files.SendAsync(files.FileNames);
                    break;
                case "pickFolder":
                    using (var folder = new FolderBrowserDialog { Description = "Enviar una carpeta completa a la otra PC", UseDescriptionForTitle = true })
                        if (folder.ShowDialog(this) == DialogResult.OK) _ = _bridge.Files.SendAsync(new[] { folder.SelectedPath });
                    break;
                case "dropFiles":
                    var dropped = e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
                    if (dropped.Length > 0) _ = _bridge.Files.SendAsync(dropped);
                    else Post(new { type = "error", message = "No se pudo leer lo que soltaste. Usa «Elegir archivos» o «Elegir carpeta»." });
                    break;
                case "openReceived":
                    Directory.CreateDirectory(_bridge.Files.ReceiveFolder);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_bridge.Files.ReceiveFolder) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
        { Post(new { type = "error", message = ex.Message }); }
    }

    private void Post(object message) { if (_ready && !_web.IsDisposed) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json)); }
    public void RefreshState() => Post(new { type = "state", state = _bridge.ViewState(_status()) });
    public void Shutdown() { _shutdown = true; Close(); Dispose(); }
}
