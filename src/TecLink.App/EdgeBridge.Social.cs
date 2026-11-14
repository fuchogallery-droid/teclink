using System.Text.Json;
using TecLink.Core.Networking;
using TecLink.Core.Protocol;

namespace TecLink.App;

/// <summary>Who is in the room: each person's own name and photo, the rooms they saved, and the requests to come back in.
/// Everything lives in each PC's own config file; nothing goes to any server.</summary>
public sealed partial class EdgeBridge
{
    private const char Sep = '\u001f';
    private string? _remoteProfileName, _remotePc, _remoteAvatar;
    private bool _askedToJoin;                 // guest: this link was made as a request, not with a fresh invitation
    private bool _nextJoinAsk;                 // guest: the next JoinSession is a request
    private sealed record JoinRequest(string Id, string Name, long At, PeerConnection Connection);
    private JoinRequest? _joinRequest;         // host: somebody is waiting for an answer
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
        SendProfile();
        if (renamed && _connection is { } conn && _remoteKnown) _ = SendDesktopAsync(conn);
        StatusChanged?.Invoke("Perfil guardado en esta PC.");
    }

    private void SendProfile()
    {
        if (_connection is not { } conn) return;
        var json = JsonSerializer.Serialize(new { name = OwnName, pc = _config.MachineName, avatar = _config.Avatar });
        _ = conn.SendAsync(WireMessage.ProfileMsg(json));
    }

    private void HandleProfile(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            var name = Clean(root.TryGetProperty("name", out var n) ? n.GetString() : null, 32);
            _remoteProfileName = name.Length > 0 ? name : null;
            _remotePc = Clean(root.TryGetProperty("pc", out var p) ? p.GetString() : null, 32);
            var avatar = root.TryGetProperty("avatar", out var a) ? a.GetString() ?? "" : "";
            _remoteAvatar = avatar.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) && avatar.Length <= 60_000 ? avatar : null;
            if (_remoteProfileName is not null) _remoteName = _remoteProfileName;
            StatusChanged?.Invoke($"{_remoteName} actualizó su perfil.");
        }
        catch (JsonException) { }
    }

    private void ResetRemoteProfile() { _remoteProfileName = null; _remotePc = null; _remoteAvatar = null; }

    // ---- Join requests (host side) ----
    private void RequestJoin(PeerConnection conn, string name)
    {
        var who = Clean(name, 32);
        if (who.Length == 0) who = "Alguien";
        var request = new JoinRequest(Guid.NewGuid().ToString("N"), who, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), conn);
        _joinRequest = request;
        _ = conn.SendAsync(WireMessage.JoinResult(false, "wait:Esperando a que " + (string.IsNullOrWhiteSpace(_config.DisplayName) ? "el dueño de la sala" : _config.DisplayName) + " acepte tu solicitud..."));
        AddEvent($"{who} pidió entrar a tu sala.");
        ControlChanged?.Invoke($"{who} pide entrar a tu sala");
        Notice?.Invoke($"{who} pide entrar a tu sala. Revisa las notificaciones.");
        StatusChanged?.Invoke($"{who} pide entrar a tu sala.");
    }

    public void AnswerJoin(string id, bool accept)
    {
        if (_joinRequest is not { } request || request.Id != id) return;
        _joinRequest = null;
        if (_connection != request.Connection) { AddEvent($"{request.Name} ya no estaba esperando."); StatusChanged?.Invoke("Esa solicitud ya no está activa."); return; }
        if (accept) { AddEvent($"Aceptaste a {request.Name}."); AcceptJoin(request.Connection); }
        else { AddEvent($"Rechazaste a {request.Name}."); Reject("El dueño de la sala no aceptó tu solicitud."); }
    }

    private void AddEvent(string text)
    {
        lock (_events) { _events.Insert(0, (text, DateTimeOffset.UtcNow.ToUnixTimeSeconds())); if (_events.Count > 12) _events.RemoveAt(_events.Count - 1); }
    }

    public object NotificationsView()
    {
        object[] recent; lock (_events) recent = _events.Select(e => (object)new { text = e.Text, at = e.At }).ToArray();
        return new { request = _joinRequest is { } r ? new { id = r.Id, name = r.Name, at = r.At } : null, recent };
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
