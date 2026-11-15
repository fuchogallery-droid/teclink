using System.Text.Json;
using TecLink.Core.Networking;
using TecLink.Core.Protocol;

namespace TecLink.App;

/// <summary>Who is in the room: each person's own name and photo, the rooms they saved, and the requests to come back in.
/// Everything lives in each PC's own config file; nothing goes to any server.</summary>
public sealed partial class EdgeBridge
{
    private const char Sep = '\u001f';
    private bool _askedToJoin;                 // guest: this link was made as a request, not with a fresh invitation
    private bool _nextJoinAsk;                 // guest: the next JoinSession is a request
    private readonly List<(string Text, long At)> _events = new();

    /// <summary>The name this person chose (or, failing that, the computer's name).</summary>
    public string OwnName => string.IsNullOrWhiteSpace(_config.DisplayName) ? _config.MachineName : _config.DisplayName.Trim();

    public void AskNextJoin() => _nextJoinAsk = true;

    private static string Clean(string? text, int max) { var t = (text ?? "").Trim(); t = new string(t.Where(c => !char.IsControl(c)).ToArray()); return t.Length > max ? t[..max] : t; }

    // ---- Profile: name, computer name and photo ----
    public void SetProfile(string? name, string? pc, string? avatar)
    {
        _config.DisplayName = Clean(name, 32);
        var computer = Clean(pc, 32);
        var renamed = computer.Length > 0 && computer != _config.MachineName;
        if (computer.Length > 0) _config.MachineName = computer;
        if (avatar is not null)
            _config.Avatar = avatar.Length == 0 ? "" : avatar.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) && avatar.Length <= 60_000 ? avatar : _config.Avatar;
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        SendProfileToRoom();
        if (renamed) foreach (var c in RoomConnections()) _ = SendDesktopAsync(c);
        StatusChanged?.Invoke("Perfil guardado en esta PC.");
    }

    private string ProfileJson() => JsonSerializer.Serialize(new { name = OwnName, pc = _config.MachineName, avatar = _config.Avatar });

    private void SendProfile() { if (_connection is { } conn) SendProfileTo(conn); }
    private void SendProfileTo(PeerConnection conn) => _ = conn.SendAsync(WireMessage.ProfileMsg(ProfileJson()));

    /// <summary>My profile changed: every PC in the room gets it (a guest only talks to the owner, who tells the rest).</summary>
    private void SendProfileToRoom()
    {
        foreach (var c in RoomConnections()) SendProfileTo(c);
        if (_config.Role == PeerRole.Server) BroadcastRoster();
    }

    private void HandleProfile(string? json)
    {
        if (Cur is not { } slot) return;
        HandleProfileFor(slot, json);
        StatusChanged?.Invoke($"{slot.Name} actualizó su perfil.");
    }

    private void HandleProfileFor(PeerSlot slot, string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            var name = Clean(root.TryGetProperty("name", out var n) ? n.GetString() : null, 32);
            slot.ProfileName = name.Length > 0 ? name : null;
            slot.Pc = Clean(root.TryGetProperty("pc", out var p) ? p.GetString() : null, 32);
            var avatar = root.TryGetProperty("avatar", out var a) ? a.GetString() ?? "" : "";
            slot.Avatar = avatar.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) && avatar.Length <= 60_000 ? avatar : null;
            if (slot.ProfileName is not null) slot.Name = slot.ProfileName;
        }
        catch (JsonException) { }
    }

    private void ResetRemoteProfile() { _remoteProfileName = null; _remotePc = null; _remoteAvatar = null; }

    // ---- Join requests (host side): each waiting guest has its own ----
    private void RequestJoin(PeerSlot slot, string name)
    {
        var who = Clean(name, 32);
        if (who.Length == 0) who = "Alguien";
        slot.Request = new JoinRequest(Guid.NewGuid().ToString("N"), who, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (slot.Connection is { } conn)
            _ = conn.SendAsync(WireMessage.JoinResult(false, "wait:Esperando a que " + (string.IsNullOrWhiteSpace(_config.DisplayName) ? "el dueño de la sala" : _config.DisplayName) + " acepte tu solicitud..."));
        AddEvent($"{who} pidió entrar a tu sala.");
        ControlChanged?.Invoke($"{who} pide entrar a tu sala");
        Notice?.Invoke($"{who} pide entrar a tu sala. Revisa las notificaciones.");
        StatusChanged?.Invoke($"{who} pide entrar a tu sala.");
    }

    public void AnswerJoin(string id, bool accept)
    {
        var slot = SlotsSnapshot().FirstOrDefault(s => s.Request?.Id == id);
        if (slot?.Request is not { } request) { StatusChanged?.Invoke("Esa solicitud ya no está activa."); return; }
        slot.Request = null;
        if (slot.Connection is null) { AddEvent($"{request.Name} ya no estaba esperando."); StatusChanged?.Invoke("Esa solicitud ya no está activa."); return; }
        if (accept) { AddEvent($"Aceptaste a {request.Name}."); AcceptJoin(slot); }
        else { AddEvent($"Rechazaste a {request.Name}."); Reject(slot, "El dueño de la sala no aceptó tu solicitud."); }
    }

    private void AddEvent(string text)
    {
        lock (_events) { _events.Insert(0, (text, DateTimeOffset.UtcNow.ToUnixTimeSeconds())); if (_events.Count > 12) _events.RemoveAt(_events.Count - 1); }
    }

    public object NotificationsView()
    {
        object[] recent; lock (_events) recent = _events.Select(e => (object)new { text = e.Text, at = e.At }).ToArray();
        var requests = SlotsSnapshot().Where(s => s.Request is not null).Select(s => (object)new { id = s.Request!.Id, name = s.Request.Name, at = s.Request.At }).ToArray();
        return new { request = requests.Length > 0 ? requests[0] : null, requests, recent };
    }
    // ---- Saved rooms (guest side) ----
    public bool CanSaveRoom => _config.Role == PeerRole.Client && !string.IsNullOrEmpty(_config.JoinCode) && _connection is not null && _remoteKnown && _sessionMode;

    public void SaveRoom()
    {
        if (!CanSaveRoom) { Notice?.Invoke("Primero entra a una sala para poder guardarla."); return; }
        var code = SessionDiscovery.Normalize(_config.JoinCode)!;
        var room = _config.SavedRooms.FirstOrDefault(r => r.Code == code);
        if (room is null) { room = new SavedRoom { Code = code }; _config.SavedRooms.Add(room); }
        room.Name = _remoteName; room.Address = _config.PeerHost ?? ""; room.SavedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        Notice?.Invoke($"Sala de {_remoteName} guardada en esta PC.");
    }

    public void ForgetRoom(string? code)
    {
        var clean = SessionDiscovery.Normalize(code);
        if (_config.SavedRooms.RemoveAll(r => r.Code == clean) == 0) return;
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        StatusChanged?.Invoke("Sala olvidada.");
    }

    /// <summary>Looks on the local network for each saved room's host (only when the person asks): code -> found.</summary>
    public async Task<Dictionary<string, bool>> CheckRoomsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, bool>();
        foreach (var room in _config.SavedRooms.ToArray())
        {
            try
            {
                var (host, _) = await SessionDiscovery.FindHostAsync(room.Code, _config.Port, TimeSpan.FromSeconds(2.5), ct);
                result[room.Code] = host is not null;
                if (host is not null && host.Address.ToString() != room.Address) room.Address = host.Address.ToString();
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or IOException) { result[room.Code] = false; }
        }
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        return result;
    }

    public object SavedRoomsView() => _config.SavedRooms.Select(r => new { code = SessionDiscovery.Pretty(r.Code), raw = r.Code, name = r.Name, address = r.Address, savedAt = r.SavedAt }).ToArray();

    public object ProfileView() => new { name = _config.DisplayName, pc = _config.MachineName, avatar = _config.Avatar, shown = OwnName };

    public string? SavedRoomAddress(string? code) => _config.SavedRooms.FirstOrDefault(r => r.Code == SessionDiscovery.Normalize(code))?.Address;
}
