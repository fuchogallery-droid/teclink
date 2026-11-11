namespace TecLink.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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
