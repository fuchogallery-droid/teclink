namespace TecLink.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Two instances would install the hooks twice and both fight over the same
        // mouse/keyboard events and the same TCP port — refuse to start a second one.
        using var singleInstance = new Mutex(true, "TecLink_SingleInstance_8f3c2e1a", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("TecLink ya está corriendo — revisa el ícono en la bandeja del sistema.", "TecLink");
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.LogError(e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Logger.LogError(e.Exception); e.SetObserved(); };
        Application.ThreadException += (_, e) => Logger.LogError(e.Exception);

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var config = AppConfig.TryLoad();
        if (config is null)
        {
            using var setup = new SetupForm();
            if (setup.ShowDialog() != DialogResult.OK) return;
            config = setup.Result;
        }

        Application.Run(new TrayAppContext(config));
    }
}
