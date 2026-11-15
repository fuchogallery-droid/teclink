using TecLink.Core.Networking;
using TecLink.Core.Networking.Relay;

namespace TecLink.App;

/// <summary>Rooms on the internet: when two PCs cannot reach each other directly (another city, a router that blocks incoming
/// connections), both connect OUT to the online relay, which only passes data encrypted end to end between them.</summary>
public sealed partial class EdgeBridge
{
    private HostAdmission? _hostAdmission;

    /// <summary>The invitation for people on other networks, or "" while the online room is off or no relay is set.</summary>
    public string OnlineInviteText => _config.OnlineEnabled && _config.OnlineSecret.Length == OnlineInvite.SecretLength && !string.IsNullOrWhiteSpace(_config.RelayHost)
        ? OnlineInvite.Format(SessionDiscovery.Normalize(_config.SessionCode) ?? "", _config.OnlineSecret, _config.RelayHost) : "";

    /// <summary>This PC's room is open right now (a change to the online room restarts it).</summary>
    public bool HostingNow => NetworkActive && _config.Role == PeerRole.Server;

    public object OnlineView() => new
    {
        enabled = _config.OnlineEnabled, relayHost = _config.RelayHost, ready = _config.OnlineEnabled && !string.IsNullOrWhiteSpace(_config.RelayHost),
        invite = OnlineInviteText,
    };

    public void SetOnline(bool on, string? relayHost)
    {
        if (relayHost is not null) _config.RelayHost = CleanHost(relayHost);
        _config.OnlineEnabled = on;
        if (_config.OnlineSecret.Length != OnlineInvite.SecretLength) _config.OnlineSecret = OnlineInvite.NewSecret();
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        StatusChanged?.Invoke(on ? "Sala en línea: se abrirá junto con tu sesión." : "Sala en línea: apagada.");
    }

    /// <summary>A new secret: whoever had the old invitation can no longer come in.</summary>
    public void RenewOnlineInvite()
    {
        _config.OnlineSecret = OnlineInvite.NewSecret();
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
    }

    private static string CleanHost(string text)
    {
        var t = (text ?? "").Trim().TrimEnd('/');
        foreach (var prefix in new[] { "https://", "http://", "wss://", "ws://" }) if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { t = t[prefix.Length..]; break; }
        return new string(t.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':').ToArray());
    }

    /// <summary>Host: keep the room open on the relay for as long as the session runs; every guest that arrives goes through the usual admission.</summary>
    private void StartOnlineRoom(CancellationToken ct)
    {
        if (!_config.OnlineEnabled || string.IsNullOrWhiteSpace(_config.RelayHost) || _config.OnlineSecret.Length != OnlineInvite.SecretLength) return;
        var relay = OnlineInvite.RelayBase(_config.RelayHost);
        var secret = _config.OnlineSecret;
        _ = Task.Run(() => RelayClient.HostAsync(relay, secret, (connection, who) =>
        {
            if (_hostAdmission is { } admission) admission.AdmitExternal(connection, who); else _ = connection.DisposeAsync();
        }, text => StatusChanged?.Invoke(text), ct), ct);
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
            _joinNotice = "Esa sala no está abierta ahora, o el servidor en línea no responde. Pide que abran su sesión con la opción «también por internet».";
            throw;
        }
    }
}
