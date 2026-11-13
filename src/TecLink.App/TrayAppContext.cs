namespace TecLink.App;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly ClipboardWatcher _clipboard;
    private readonly EdgeBridge _bridge;
    private readonly NotifyIcon _trayIcon;
    private readonly CancellationTokenSource _cts = new();
    private ControlPanelForm? _panel;
    private string _status = "Iniciando conexión…";
    private RegisteredWaitHandle? _showRequest;

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
        menu.Items.Add("Reconfigurar...", null, (_, _) => Reconfigure());
        menu.Items.Add("Salir", null, (_, _) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "TecLink — iniciando...",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowPanel();

        _bridge.InstallHooks();
        _ = _bridge.RunNetworkingAsync(_cts.Token).ContinueWith(
            t => Logger.LogError(t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
        _clipboard.BeginInvoke(ShowPanel);
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
        if (_panel is null || _panel.IsDisposed) _panel = new ControlPanelForm(_bridge, () => _status);
        _panel.Show();
        if (_panel.WindowState == FormWindowState.Minimized) _panel.WindowState = FormWindowState.Normal;
        _panel.Activate();
        _panel.RefreshState();
    }

    private void ExitApp()
    {
        _cts.Cancel();
        _showRequest?.Unregister(null);
        _panel?.Shutdown();
        _trayIcon.Visible = false;
        _bridge.Dispose();
        _clipboard.Dispose();
        ExitThread();
    }
}
