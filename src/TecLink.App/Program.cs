namespace TecLink.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Guard before installing hooks or opening a connection. Keep the mutex alive
        // for the entire message loop, including setup and reconfiguration.
        using var instance = new Mutex(false, @"Local\TechnoCrazy.TecLink");
        bool firstInstance;
        try { firstInstance = instance.WaitOne(0); }
        catch (AbandonedMutexException) { firstInstance = true; }
        if (!firstInstance)
        {
            MessageBox.Show("TecLink ya está abierto junto al reloj.", "TecLink",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            var config = AppConfig.TryLoad();
            if (config is null)
            {
                using var setup = new SetupForm();
                if (setup.ShowDialog() != DialogResult.OK) return;
                config = setup.Result;
            }

            Application.Run(new TrayAppContext(config));
        }
        finally { instance.ReleaseMutex(); }
    }
}
