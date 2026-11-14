using TecLink.Core.Networking;

namespace TecLink.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Guard before installing hooks or opening a connection. Keep the mutex alive
        // for the entire message loop, including setup and reconfiguration.
        using var instance = new Mutex(false, @"Local\TechnoCrazy.TecLink");
        using var showPanel = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TechnoCrazy.TecLink.ShowPanel");
        bool firstInstance;
        try { firstInstance = instance.WaitOne(0); }
        catch (AbandonedMutexException) { firstInstance = true; }
        if (!firstInstance)
        {
            showPanel.Set();
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.LogError(e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Logger.LogError(e.Exception); e.SetObserved(); };
        Application.ThreadException += (_, e) => Logger.LogError(e.Exception);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            // First run needs no form: this PC hosts its own session and the panel's
            // "Conectar" tab handles inviting or joining.
            var config = AppConfig.TryLoad() ?? new AppConfig { Role = PeerRole.Server };
            if (SessionDiscovery.Normalize(config.SessionCode) is null) { config.SessionCode = SessionDiscovery.NewCode(); config.Save(); }

            using var app = new TrayAppContext(config);
            app.BindShowSignal(showPanel);
            Application.Run(app);
        }
        finally { instance.ReleaseMutex(); }
    }
}
