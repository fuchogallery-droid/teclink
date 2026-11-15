using System.Text.Json;
using TecLink.Core.Networking;
using TecLink.Core.Networking.Relay;

namespace TecLink.App;

/// <summary>Rooms on the internet: when two PCs cannot reach each other directly (another city, a router that blocks incoming
/// connections), both connect OUT to the online relay, which only passes data encrypted end to end between them.
/// The invitation is made by the app itself, and the app finds the relay's current address by itself (no typing).</summary>
public sealed partial class EdgeBridge
{
    /// <summary>A tiny public file that says where the online relay is now (it can move without anybody reinstalling anything).</summary>
    public const string RelayInfoUrl = "https://raw.githubusercontent.com/fuchogallery-droid/teclink/master/relay.json";
    public const string DownloadUrl = "https://github.com/fuchogallery-droid/teclink/releases/latest";

    private HostAdmission? _hostAdmission;
    private string _relayStatus = "";

    /// <summary>The invitation for people on other networks, or "" while the online room is off or no relay is known.</summary>
    public string OnlineInviteText => _config.OnlineEnabled && _config.OnlineSecret.Length == OnlineInvite.SecretLength && !string.IsNullOrWhiteSpace(_config.RelayHost)
        ? OnlineInvite.Format(SessionDiscovery.Normalize(_config.SessionCode) ?? "", _config.OnlineSecret, _config.RelayHost) : "";

    /// <summary>The whole message to send to somebody: where to download TecLink, where to paste the invitation and the invitation itself.</summary>
    private string InviteMessage(string invite) => string.IsNullOrEmpty(invite) ? "" :
        "Únete a mi sala de TecLink.\n1) Instala TecLink (si no lo tienes): " + DownloadUrl + "\n2) Ábrelo, ve a «Conectar» y pega esta invitación en el campo de código:\n" + invite + "\n3) Pulsa «Unirme».";

    /// <summary>This PC's room is open right now (a change to the online room restarts it).</summary>
    public bool HostingNow => NetworkActive && _config.Role == PeerRole.Server;

    public object OnlineView()
    {
        var invite = OnlineInviteText;
        return new
        {
            enabled = _config.OnlineEnabled, relayHost = _config.RelayHost, pinned = _config.RelayHostPinned, ready = _config.OnlineEnabled && !string.IsNullOrWhiteSpace(_config.RelayHost),
            invite, message = InviteMessage(invite), downloadUrl = DownloadUrl, status = _relayStatus,
        };
    }

    public void SetOnline(bool on, string? relayHost)
    {
        if (relayHost is not null)
        {
            var typed = CleanHost(relayHost);
            // Typing an address pins it; leaving the box empty goes back to the automatic one.
            _config.RelayHostPinned = typed.Length > 0;
            if (typed.Length > 0) _config.RelayHost = typed;
        }
        _config.OnlineEnabled = on;
        EnsureOnlineSecret();
        Save();
        StatusChanged?.Invoke(on ? "Sala en línea: se abrirá junto con tu sesión." : "Sala en línea: apagada.");
    }

    private void EnsureOnlineSecret()
    {
        if (_config.OnlineSecret.Length != OnlineInvite.SecretLength) _config.OnlineSecret = OnlineInvite.NewSecret();
    }

    public string UpdateKey { get => _config.UpdateKey; set { _config.UpdateKey = (value ?? "").Trim(); Save(); } }

    private void Save()
    {
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
    }

    /// <summary>A new secret: whoever had the old invitation can no longer come in.</summary>
    public void RenewOnlineInvite() { _config.OnlineSecret = OnlineInvite.NewSecret(); Save(); }

    private static string CleanHost(string text)
    {
        var t = (text ?? "").Trim().TrimEnd('/');
        foreach (var prefix in new[] { "https://", "http://", "wss://", "ws://" }) if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { t = t[prefix.Length..]; break; }
        return new string(t.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':').ToArray());
    }

    /// <summary>Asks where the relay is now. Returns true when the address changed. Only runs when the person opens a room or presses "Actualizar".</summary>
    public async Task<bool> RefreshRelayAsync(CancellationToken ct = default)
    {
        if (_config.RelayHostPinned && !string.IsNullOrWhiteSpace(_config.RelayHost)) { _relayStatus = "Dirección fijada a mano."; return false; }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TecLink-relay-lookup");
            var json = await http.GetStringAsync(RelayInfoUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
            using var doc = JsonDocument.Parse(json);
            var found = CleanHost(doc.RootElement.GetProperty("host").GetString() ?? "");
            if (found.Length < 4) { _relayStatus = "El servidor en línea no está disponible ahora."; return false; }
            var changed = !string.Equals(found, _config.RelayHost, StringComparison.OrdinalIgnoreCase);
            _config.RelayHost = found; _relayStatus = "Servidor en línea encontrado.";
            if (changed) Save();
            return changed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _relayStatus = string.IsNullOrWhiteSpace(_config.RelayHost) ? "No pude consultar el servidor en línea. Revisa tu internet." : "Sin internet para actualizar; se usa el último servidor conocido.";
            return false;
        }
    }

    /// <summary>The "Actualizar" button: look the relay up again and, if this PC's room is open, put it on the new address.</summary>
    public async Task<bool> RefreshRelayNowAsync()
    {
        var changed = await RefreshRelayAsync();
        RoomChanged?.Invoke();
        return changed;
    }

    /// <summary>Host: keep the room open on the relay for as long as the session runs; every guest that arrives goes through the usual admission.</summary>
    private async Task StartOnlineRoomAsync(CancellationToken ct)
    {
        if (!_config.OnlineEnabled) return;
        EnsureOnlineSecret();
        await RefreshRelayAsync(ct);
        RoomChanged?.Invoke();
        if (string.IsNullOrWhiteSpace(_config.RelayHost) || ct.IsCancellationRequested) return;
        var relay = OnlineInvite.RelayBase(_config.RelayHost);
        var secret = _config.OnlineSecret;
        _ = Task.Run(() => RelayClient.HostAsync(relay, secret, (connection, who) =>
        {
            if (_hostAdmission is { } admission) admission.AdmitExternal(connection, who); else _ = connection.DisposeAsync();
        }, text => { _relayStatus = text; StatusChanged?.Invoke(text); }, ct), ct);
        Logger.Info("Sala en línea abierta en el servidor puente.");
    }

    /// <summary>Guest: connect through the relay with the pasted invitation.</summary>
    private async Task<PeerConnection> ConnectOnlineAsync(CancellationToken ct)
    {
        StatusChanged?.Invoke("Conectando por internet...");
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(25));
        try { return await RelayClient.JoinAsync(OnlineInvite.RelayBase(_config.JoinRelay), _config.JoinSecret, attempt.Token); }
        catch (InvalidDataException) { _joinNotice = "La invitación no coincide con la sala. Pídela de nuevo a quien te invitó."; throw; }
        catch (System.Security.Cryptography.CryptographicException) { _joinNotice = "La invitación no coincide con la sala. Pídela de nuevo a quien te invitó."; throw; }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or System.Net.WebSockets.WebSocketException)
        {
            _joinNotice = "Esa sala no está abierta ahora, o el servidor en línea no responde. Pide una invitación nueva a quien te invitó.";
            throw;
        }
    }
}
