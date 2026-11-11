namespace TecLink.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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
