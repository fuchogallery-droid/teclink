namespace TecLink.App;

/// <summary>The version the person is running, shown in the window title, the tray and the panel header.</summary>
public static class AppInfo
{
    public static string VersionText { get; } = "v" + (typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "?");
    public const string Company = "TechnoCrazy";
}
