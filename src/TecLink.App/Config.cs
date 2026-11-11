using System.Text.Json;
using TecLink.Core.Networking;

namespace TecLink.App;

public enum PeerRole { Server, Client }

public enum EdgeDirection { Left, Right }

public sealed class AppConfig
{
    public string MachineName { get; set; } = Environment.MachineName;
    public PeerRole Role { get; set; } = PeerRole.Server;
    public string PeerHost { get; set; } = "";
    public int Port { get; set; } = PeerServer.DefaultPort;

    /// <summary>Which edge of THIS machine's screen leads to the peer.</summary>
    public EdgeDirection EdgeToPeer { get; set; } = EdgeDirection.Right;

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
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
