using System.Text.Json;
using TecLink.Core.Networking;
using TecLink.Core;

namespace TecLink.App;

public enum PeerRole { Server, Client }

public enum EdgeDirection { Left, Right }

public sealed class SavedRoom
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public long SavedAt { get; set; }
}

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
    /// <summary>Host with several guests: where each guest's screens sit in the map (only its own screens, Remote = true), by PC name.
    /// The mouse crosses to the guest whose side of this PC it leaves by; the active guest's map lives in <see cref="ScreenPositions"/>.</summary>
    public Dictionary<string, List<ScreenPlacement>> GuestMaps { get; set; } = new();
    /// <summary>Windows scale 1–20; 0 = never chosen in TecLink, keep what Windows has.</summary>
    public int PointerSpeedLevel { get; set; }
    public string CursorStyle { get; set; } = "system";
    public int CursorSizePercent { get; set; } = 100;
    /// <summary>Files shared with the session are copied to this PC's TecLink folder as soon as they are shared.</summary>
    public bool AutoReceive { get; set; }
    /// <summary>Off by default: TecLink never copies the clipboard between PCs unless the person asks for it.</summary>
    public bool ShareClipboard { get; set; }
    /// <summary>"own" = all the computers are mine (one mouse and keyboard that cross by themselves); "people" = a room with other people: chat, voice and camera, and controlling somebody's PC only if they allow it.</summary>
    public string RoomMode { get; set; } = "people";
    /// <summary>Also open this PC's room on the internet (through the online relay), so people in other cities or networks can join.</summary>
    public bool OnlineEnabled { get; set; } = false; // 0.9.7: la invitacion en linea esta retirada; solo el codigo simple
    /// <summary>Secret of this PC's online room (16 characters); it never leaves the two PCs, only a hash of it reaches the relay.</summary>
    public string OnlineSecret { get; set; } = "";
    /// <summary>Address of the online relay used to open the room, for example relay.example.com.</summary>
    public string RelayHost { get; set; } = "";
    /// <summary>True only when the person typed the relay address by hand; otherwise TecLink keeps it up to date by itself.</summary>
    public bool RelayHostPinned { get; set; }
    /// <summary>Guest: secret and relay of the online invitation that was pasted (empty = a normal local join).</summary>
    public string JoinSecret { get; set; } = "";
    /// <summary>Key typed by the person when an update asks for one (see Updater).</summary>
    public string UpdateKey { get; set; } = "";
    /// <summary>Names of PCs the owner said are his own: they join in "Mis computadoras" mode by themselves.</summary>
    public List<string> OwnPcs { get; set; } = new();
    public string JoinRelay { get; set; } = "";
    /// <summary>How this person wants to be called in rooms (empty = the computer name). Lives only on this PC.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Small round picture (data:image/jpeg;base64,...) shown to the people in the room. Lives only on this PC.</summary>
    public string Avatar { get; set; } = "";
    /// <summary>Rooms this person saved to come back to; each one is asked to the host as a request.</summary>
    public List<SavedRoom> SavedRooms { get; set; } = new();

    // TECLINK_CONFIG lets the automated tests keep their settings away from the real ones.
    private static string ConfigPath => Environment.GetEnvironmentVariable("TECLINK_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecLink", "config.json");

    public static AppConfig? TryLoad()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json); if (loaded is not null) loaded.OnlineEnabled = false; return loaded;
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
