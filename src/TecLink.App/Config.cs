using System.Text.Json;
using TecLink.Core.Networking;
using TecLink.Core;

namespace TecLink.App;

public enum PeerRole { Server, Client }

public enum EdgeDirection { Left, Right }

public sealed class AppConfig
{
    public string MachineName { get; set; } = Environment.MachineName;
    public PeerRole Role { get; set; } = PeerRole.Server;
    public string PeerHost { get; set; } = "";
    /// <summary>Invitation code of the session this PC hosts (always set; shown in the panel).</summary>
    public string SessionCode { get; set; } = "";
    /// <summary>Code of the session this PC joined as a guest; empty = hosting (or a legacy direct IP link).</summary>
    public string JoinCode { get; set; } = "";
    public int Port { get; set; } = PeerServer.DefaultPort;

    /// <summary>Which edge of THIS machine's screen leads to the peer.</summary>
    public EdgeDirection EdgeToPeer { get; set; } = EdgeDirection.Right;
    public bool AutomaticSwitching { get; set; } = true;
    public bool RoutesConfigured { get; set; }
    public List<ScreenRoute> ScreenRoutes { get; set; } = new();
    public List<string> ScreenOrder { get; set; } = new();
    public string Theme { get; set; } = "system";
    public bool UseSpatialMap { get; set; }
    public List<ScreenPlacement> ScreenPositions { get; set; } = new();
    /// <summary>Windows scale 1–20; 0 = never chosen in TecLink, keep what Windows has.</summary>
    public int PointerSpeedLevel { get; set; }
    public string CursorStyle { get; set; } = "system";
    public int CursorSizePercent { get; set; } = 100;

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecLink", "config.json");

    public static AppConfig? TryLoad()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var pending = ConfigPath + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, ConfigPath, true);
    }
}
