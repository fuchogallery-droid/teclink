namespace TecLink.App;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly ClipboardWatcher _clipboard;
    private readonly EdgeBridge _bridge;
    private readonly NotifyIcon _trayIcon;
    private readonly CancellationTokenSource _cts = new();

    public TrayAppContext(AppConfig config)
    {
        _config = config;
        _clipboard = new ClipboardWatcher();
        _ = _clipboard.Handle; // touching Handle forces native window creation (and fires OnLoad) without ever showing it

        _bridge = new EdgeBridge(config, _clipboard);
        _bridge.StatusChanged += OnStatusChanged;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Reconfigurar...", null, (_, _) => Reconfigure());
        menu.Items.Add("Salir", null, (_, _) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "TecLink — iniciando...",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _bridge.InstallHooks();
        _ = _bridge.RunNetworkingAsync(_cts.Token).ContinueWith(
            t => Logger.LogError(t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnStatusChanged(string status)
    {
        // status events arrive from the networking/hook threads; NotifyIcon must be touched from the UI thread
        _clipboard.BeginInvoke(() =>
        {
            var text = "TecLink — " + status;
            _trayIcon.Text = text.Length > 127 ? text[..127] : text; // NotifyIcon.Text has a 127-char limit
        });
    }

    private void Reconfigure()
    {
        using var form = new SetupForm();
        form.ShowDialog();
        MessageBox.Show("Reinicia TecLink para aplicar la nueva configuración.", "TecLink");
    }

    private void ExitApp()
    {
        _cts.Cancel();
        _trayIcon.Visible = false;
        _bridge.Dispose();
        _clipboard.Dispose();
        ExitThread();
    }
}
