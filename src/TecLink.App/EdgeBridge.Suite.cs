using System.Text.Json;
using TecLink.Core;
using TecLink.Core.Protocol;

namespace TecLink.App;

/// <summary>The room: live tiles of every screen, the session's shared files and the consent a guest gives before a screen is removed.</summary>
public sealed partial class EdgeBridge
{
    // ---- Live tiles ----
    private readonly ScreenTiles _tilesForPeer = new(), _tilesLocal = new();

    /// <summary>(remote, screen id, JPEG). Raised on a worker or network thread.</summary>
    public event Action<bool, string, byte[]>? TileReceived;

    /// <summary>The panel is showing the room: start (or stop) the small live pictures of this PC and of the other one.
    /// With a focus, only that screen is captured, at a larger size (tile in full view).</summary>
    public void WatchScreens(bool on, int maxWidth, bool? focusRemote = null, string? focusId = null)
    {
        if (!on)
        {
            _tilesLocal.Stop();
            if (_connection is { } quiet && _peerExtras) _ = quiet.SendAsync(WireMessage.ScreenWatch(""));
            return;
        }
        var screens = Screen.AllScreens;
        if (focusRemote == true) _tilesLocal.Stop();
        else
        {
            var mine = focusId is null ? Array.Empty<int>() : new[] { Array.FindIndex(screens, s => s.DeviceName == focusId) };
            _tilesLocal.Start(mine, maxWidth, (index, jpeg) =>
            {
                var all = Screen.AllScreens;
                if (index < all.Length) TileReceived?.Invoke(false, all[index].DeviceName, jpeg);
                return Task.CompletedTask;
            });
        }
        if (_connection is not { } conn || !CanUseExtras) return;
        if (!ManagementAllowed || focusRemote == false) { _ = conn.SendAsync(WireMessage.ScreenWatch("")); return; }
        var remote = _remoteDisplays;
        var theirs = focusId is null ? Enumerable.Range(0, remote.Length) : new[] { Array.FindIndex(remote, d => d.Id == focusId) };
        _ = conn.SendAsync(WireMessage.ScreenWatch($"{maxWidth}|{string.Join(',', theirs.Where(i => i >= 0))}"));
    }

    private void HandleWatch(string text)
    {
        var split = text.IndexOf('|');
        if (split < 0 || !ManagementAllowed || _connection is not { } conn) { _tilesForPeer.Stop(); return; }
        if (!int.TryParse(text[..split], out var width)) return;
        var indices = text[(split + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n >= 0).Take(MaxScreens).ToArray();
        if (indices.Length == 0) { _tilesForPeer.Stop(); return; }
        _tilesForPeer.Start(indices, width, (index, jpeg) =>
        {
            var screen = Screen.AllScreens.ElementAtOrDefault(index);
            return screen is null ? Task.CompletedTask : conn.SendAsync(WireMessage.ScreenTile(index, screen.Bounds.Width, screen.Bounds.Height, jpeg));
        });
    }

    /// <summary>A remote tile's own position (0..1 inside that monitor) → the other PC's wire coordinates.</summary>
    public void TileMove(string id, double fx, double fy)
    {
        var d = _remoteDisplays.FirstOrDefault(s => s.Id == id);
        if (d is null) return;
        ViewerMove(d.Left + (int)Math.Round(Math.Clamp(fx, 0, 1) * (d.Width - 1)), d.Top + (int)Math.Round(Math.Clamp(fy, 0, 1) * (d.Height - 1)));
    }

    // ---- Removing a guest's screen needs that guest's consent ----
    /// <summary>(screen id, label). The owner of the session asks to remove one of this PC's screens.</summary>
    public event Action<string, string>? RemovalAsked;
    /// <summary>A plain message for the panel (toast).</summary>
    public event Action<string>? Notice;

    public void RequestRemoveScreen(string id)
    {
        if (_config.Role != PeerRole.Server) throw new ArgumentException("Solo el dueño de la sala puede quitar pantallas.");
        if (_connection is not { } conn || !_remoteKnown || !_remoteDisplays.Any(d => d.Id == id)) throw new ArgumentException("Esa pantalla ya no está conectada.");
        _ = conn.SendAsync(WireMessage.ScreenRemoval("ask:" + id));
        Notice?.Invoke($"Se pidió permiso a {_remoteName}. La pantalla se quita cuando acepte.");
    }

    public void AnswerRemoval(string id, bool accept)
    {
        if (_connection is { } conn) _ = conn.SendAsync(WireMessage.ScreenRemoval((accept ? "ok:" : "no:") + id));
    }

    private void HandleRemoval(string text)
    {
        var split = text.IndexOf(':');
        if (split < 0) return;
        var verb = text[..split]; var id = text[(split + 1)..];
        if (verb == "ask")
        {
            if (_config.Role != PeerRole.Client) return;
            var mine = LocalDisplays();
            var index = Array.FindIndex(mine, d => d.Id == id);
            if (index < 0) { AnswerRemoval(id, false); return; }
            RemovalAsked?.Invoke(id, $"Pantalla {index + 1} ({mine[index].Width} × {mine[index].Height})");
            return;
        }
        if (_config.Role != PeerRole.Server) return;
        if (verb == "no") { Notice?.Invoke($"{_remoteName} no aceptó que se quite su pantalla."); return; }
        if (verb != "ok" || !_remoteDisplays.Any(d => d.Id == id)) return;
        try
        {
            var positions = _config.ScreenPositions.Select(p => p.Remote && p.Id == id ? p with { Hidden = true } : p).ToList();
            StoreSpatialMap(positions, _config.AutomaticSwitching, _config.Theme);
            if (_peerMapSupported) _ = _connection?.SendAsync(WireMessage.Map(Guid.NewGuid().ToString("N"), _config.ScreenPositions.ToArray()));
            Notice?.Invoke($"{_remoteName} aceptó: la pantalla se quitó de la sala.");
            StatusChanged?.Invoke("Pantalla quitada de la sala con permiso del invitado.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { Notice?.Invoke("No se pudo quitar la pantalla: " + ex.Message); }
    }

    // ---- Files shared with the session: one history that everyone sees, with the same actions for everyone ----
    private sealed class SharedItem
    {
        public string Id = ""; public long At; public string Name = ""; public long Size; public bool IsDirectory; public bool Mine;
        public string Sender = ""; public string? LocalPath; public string? Pending; public bool Fetching;
    }
    private sealed record ShareWire(string Id, string Name, long Size, bool IsDirectory);
    private readonly List<SharedItem> _shares = new();

    private void InitSuite()
    {
        Files.Arrived += (id, paths) =>
        {
            SharedItem? item; string? pending;
            lock (_shares)
            {
                item = _shares.FirstOrDefault(s => s.Id == id);
                if (item is null)
                {
                    // Sent the direct way (tray menu or dropped on the remote screen): it still belongs in the history.
                    foreach (var path in paths)
                        _shares.Add(new SharedItem { Id = Guid.NewGuid().ToString("N"), At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Name = Path.GetFileName(path), Size = File.Exists(path) ? new FileInfo(path).Length : 0, IsDirectory = Directory.Exists(path), Sender = _remoteName, LocalPath = path });
                    pending = null;
                }
                else { item.LocalPath = paths.FirstOrDefault(); item.Fetching = false; pending = item.Pending; item.Pending = null; }
            }
            if (item is not null && pending is not null) RunShareAction(item, pending);
            StatusChanged?.Invoke(Files.LastStatus);
        };
    }

    private void ClearSuite()
    {
        lock (_shares) _shares.Clear();
        _tilesForPeer.Stop();
    }

    public object SharesView()
    {
        lock (_shares)
            return _shares.OrderByDescending(s => s.At).Select(s => new
            {
                id = s.Id, at = s.At, name = s.Name, size = s.Size, isDirectory = s.IsDirectory, mine = s.Mine, sender = s.Sender,
                have = s.Mine || s.LocalPath is not null, fetching = s.Fetching,
            }).ToArray();
    }

    public void SetAutoReceive(bool on)
    {
        _config.AutoReceive = on; _config.Save();
        if (!on) return;
        string[] missing;
        lock (_shares) missing = _shares.Where(s => !s.Mine && s.LocalPath is null && !s.Fetching).Select(s => s.Id).ToArray();
        foreach (var id in missing) Fetch(id, null);
    }

    /// <summary>Shares files or folders with the session: everyone sees them in the history at once.</summary>
    public void ShareFiles(IEnumerable<string> paths)
    {
        if (_connection is not { } conn || !CanUseExtras) { Notice?.Invoke("Todavía no hay nadie conectado para recibir archivos."); return; }
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path)) continue;
            var item = new SharedItem
            {
                Id = Guid.NewGuid().ToString("N"), At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Name = Path.GetFileName(path), IsDirectory = isDirectory,
                Size = isDirectory ? 0 : new FileInfo(path).Length, Mine = true, Sender = _config.MachineName, LocalPath = path,
            };
            if (string.IsNullOrEmpty(item.Name)) item.Name = path;
            lock (_shares) _shares.Add(item);
            _ = conn.SendAsync(WireMessage.ShareOffer(JsonSerializer.Serialize(new ShareWire(item.Id, item.Name, item.Size, item.IsDirectory), PeerFilesJson)));
        }
        StatusChanged?.Invoke("Archivos compartidos con la sesión.");
    }

    private void HandleShareOffer(string json)
    {
        ShareWire? offer;
        try { offer = JsonSerializer.Deserialize<ShareWire>(json, PeerFilesJson); } catch (JsonException) { return; }
        if (offer is null || string.IsNullOrWhiteSpace(offer.Id) || string.IsNullOrWhiteSpace(offer.Name)) return;
        lock (_shares)
        {
            if (_shares.Count > 2000 || _shares.Any(s => s.Id == offer.Id)) return;
            _shares.Add(new SharedItem { Id = offer.Id, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Name = Path.GetFileName(offer.Name), Size = Math.Max(0, offer.Size), IsDirectory = offer.IsDirectory, Sender = _remoteName });
        }
        if (_config.AutoReceive) Fetch(offer.Id, null);
        Notice?.Invoke($"{_remoteName} compartió: {Path.GetFileName(offer.Name)}");
        StatusChanged?.Invoke($"{_remoteName} compartió un archivo.");
    }

    private void HandleShareFetch(string id)
    {
        SharedItem? item;
        lock (_shares) item = _shares.FirstOrDefault(s => s.Id == id && s.Mine);
        if (item?.LocalPath is { } path && (File.Exists(path) || Directory.Exists(path))) _ = Files.SendAsync(new[] { path }, id);
        else Notice?.Invoke("Pidieron un archivo que ya no está en esta PC.");
    }

    private void Fetch(string id, string? then)
    {
        SharedItem? item;
        lock (_shares)
        {
            item = _shares.FirstOrDefault(s => s.Id == id);
            if (item is null || item.Mine) return;
            if (then is not null) item.Pending = then;
            if (item.Fetching) return;
            item.Fetching = true;
        }
        if (_connection is { } conn && CanUseExtras) _ = conn.SendAsync(WireMessage.ShareFetch(id));
        else { lock (_shares) { item.Fetching = false; item.Pending = null; } Notice?.Invoke("Quien lo compartió ya no está conectado."); }
    }

    /// <summary>The same two actions for everyone: "view" (open it) and "desktop" (put a copy on my desktop).</summary>
    public void ShareAction(string id, string action)
    {
        if (action is not ("view" or "desktop")) return;
        SharedItem? item;
        lock (_shares) item = _shares.FirstOrDefault(s => s.Id == id);
        if (item is null) return;
        if (item.LocalPath is { } path && (File.Exists(path) || Directory.Exists(path))) RunShareAction(item, action);
        else if (item.Mine) Notice?.Invoke("Ese archivo ya no está donde estaba en esta PC.");
        else { Fetch(id, action); Notice?.Invoke("Trayendo el archivo…"); }
    }

    private void RunShareAction(SharedItem item, string action)
    {
        try
        {
            if (action == "desktop") { Files.CopyToDesktop(item.LocalPath!); Notice?.Invoke("Puesto en tu escritorio: " + item.Name); }
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.LocalPath!) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { Notice?.Invoke("No se pudo: " + ex.Message); }
    }
}
