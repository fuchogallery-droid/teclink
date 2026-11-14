namespace TecLink.App;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly ClipboardWatcher _clipboard;
    private readonly EdgeBridge _bridge;
    private readonly NotifyIcon _trayIcon;
    private readonly CancellationTokenSource _cts = new();
    private ControlPanelForm? _panel;
    private RemoteScreenForm? _viewer;
    private string _status = "Iniciando conexión…";
    private RegisteredWaitHandle? _showRequest;

    /// <summary>The TecLink isotype, baked into the exe via ApplicationIcon; read from the
    /// running process instead of shipping a loose .ico so the single-file publish still has it.</summary>
    public static Icon AppIcon { get; } = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

    public TrayAppContext(AppConfig config)
    {
        _config = config;
        _clipboard = new ClipboardWatcher();
        _ = _clipboard.Handle;

        _bridge = new EdgeBridge(config, _clipboard);
        _bridge.StatusChanged += OnStatusChanged;
        _bridge.ControlChanged += OnControlChanged;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir panel de pantallas", null, (_, _) => ShowPanel());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pasar mando a la otra PC", null, (_, _) => _bridge.ToggleControl());
        menu.Items.Add("Tomar mando aquí", null, (_, _) => _bridge.ToggleControl());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ver pantalla de la otra PC", null, (_, _) => ShowViewer());
        menu.Items.Add("Enviar archivos a la otra PC…", null, (_, _) => SendFiles());
        menu.Items.Add("Abrir carpeta de recibidos", null, (_, _) => OpenReceived());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Reconfigurar...", null, (_, _) => Reconfigure());
        menu.Items.Add("Salir", null, (_, _) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon = AppIcon,
            Text = "TecLink — iniciando...",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowPanel();

        _powerItem = new ToolStripMenuItem("Encendido") { Checked = true, CheckOnClick = false };
        _powerItem.Click += (_, _) => SetPower(!_bridge.Powered);
        menu.Items.Insert(0, _powerItem);

        _bridge.InstallHooks();
        StartNetworking();
        _clipboard.BeginInvoke(ShowPanel);
    }

    private ToolStripMenuItem _powerItem = null!;
    private CancellationTokenSource? _netCts;

    private void StartNetworking()
    {
        _netCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = _bridge.RunNetworkingAsync(_netCts.Token).ContinueWith(
            t => { if (t.Exception!.InnerExceptions.Any(e => e is not OperationCanceledException)) Logger.LogError(t.Exception); },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Master on/off: off hands control back locally, drops the connection and stops
    /// reconnecting; on resumes connecting. Hooks stay installed but are inert without a peer.</summary>
    private void SetPower(bool on)
    {
        if (on == _bridge.Powered) return;
        _bridge.Powered = on;
        _powerItem.Checked = on;
        if (on) { CursorStyler.Apply(_config.CursorStyle, _config.CursorSizePercent); StartNetworking(); }
        else { _netCts?.Cancel(); _netCts = null; CursorStyler.Restore(); }
        OnControlChanged(on ? "TecLink encendido" : "TecLink apagado");
        _panel?.RefreshState();
    }

    private void OnStatusChanged(string status)
    {
        _status = status;
        // status events arrive from the networking/hook threads; NotifyIcon must be touched from the UI thread
        _clipboard.BeginInvoke(() =>
        {
            var text = "TecLink — " + status;
            _trayIcon.Text = text.Length > 127 ? text[..127] : text; // NotifyIcon.Text has a 127-char limit
            _panel?.RefreshState();
        });
    }

    private void OnControlChanged(string message)
    {
        _clipboard.BeginInvoke(() => _trayIcon.ShowBalloonTip(1500, "TecLink", message, ToolTipIcon.Info));
    }

    private void Reconfigure()
    {
        using var form = new SetupForm();
        form.ShowDialog();
        MessageBox.Show("Reinicia TecLink para aplicar la nueva configuración.", "TecLink");
    }

    public void BindShowSignal(EventWaitHandle signal) => _showRequest = ThreadPool.RegisterWaitForSingleObject(signal,
        (_, _) => { if (!_clipboard.IsDisposed) _clipboard.BeginInvoke(ShowPanel); }, null, Timeout.Infinite, false);

    private void ShowPanel()
    {
        if (_panel is null || _panel.IsDisposed) _panel = new ControlPanelForm(_bridge, () => _status, ShowViewer, SetPower);
        _panel.Show();
        if (_panel.WindowState == FormWindowState.Minimized) _panel.WindowState = FormWindowState.Normal;
        _panel.Activate();
        _panel.RefreshState();
    }

    private void ShowViewer()
    {
        if (!_bridge.CanUseExtras)
        {
            _trayIcon.ShowBalloonTip(2500, "TecLink", "La otra PC no está conectada o necesita esta misma versión de TecLink.", ToolTipIcon.Warning);
            return;
        }
        if (_viewer is null || _viewer.IsDisposed) _viewer = new RemoteScreenForm(_bridge);
        _viewer.Show();
        _viewer.Activate();
    }

    private void SendFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "Enviar archivos a la otra PC" };
        if (dialog.ShowDialog() == DialogResult.OK) _ = _bridge.Files.SendAsync(dialog.FileNames);
    }

    private void OpenReceived()
    {
        Directory.CreateDirectory(_bridge.Files.ReceiveFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_bridge.Files.ReceiveFolder) { UseShellExecute = true });
    }

    private void ExitApp()
    {
        _cts.Cancel();
        if (_viewer is { IsDisposed: false }) _viewer.Close();
        CursorStyler.Restore();
        _showRequest?.Unregister(null);
        _panel?.Shutdown();
        _trayIcon.Visible = false;
        _bridge.Dispose();
        _clipboard.Dispose();
        ExitThread();
    }
}
