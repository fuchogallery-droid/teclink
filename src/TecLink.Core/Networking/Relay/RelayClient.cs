using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TecLink.Core.Networking.Relay;

/// <summary>Connects through the online relay when the two PCs cannot reach each other directly (another city, a router that does not
/// accept connections from outside). Both PCs only connect OUT to the relay; everything between them is encrypted end to end.</summary>
public static class RelayClient
{
    private static ClientWebSocket NewSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20); // proxies drop silent connections
        return socket;
    }

    /// <summary>Guest: enters the room of the invitation and returns the encrypted connection to its owner.</summary>
    public static async Task<PeerConnection> JoinAsync(string relayBase, string secret, CancellationToken ct)
    {
        var socket = NewSocket();
        try
        {
            await socket.ConnectAsync(new Uri($"{relayBase}/ws/join?room={OnlineInvite.RoomId(secret)}"), ct);
            var secure = await SecureStream.HandshakeAsync(new WebSocketStream(socket), secret, isHost: false, ct);
            return new PeerConnection(secure);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Owner: keeps the room open on the relay and hands every guest that arrives to <paramref name="onGuest"/>
    /// (already decrypted and authenticated: only whoever holds the invitation gets this far).</summary>
    public static async Task HostAsync(string relayBase, string secret, Action<PeerConnection, string> onGuest, Action<string> status, CancellationToken ct)
    {
        var room = OnlineInvite.RoomId(secret);
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            using var control = NewSocket();
            try
            {
                await control.ConnectAsync(new Uri($"{relayBase}/ws/host?room={room}"), ct);
                if (failures > 0) status("Sala en línea: reconectada.");
                failures = 0;
                var buffer = new byte[2048];
                while (control.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await control.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;
                    string? guest;
                    try { guest = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.GetProperty("guest").GetString(); }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { continue; }
                    if (guest is { Length: > 0 }) _ = Task.Run(() => AcceptAsync(relayBase, room, secret, guest, onGuest, ct), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException or InvalidOperationException)
            {
                failures++;
                if (failures == 1) status("Sala en línea: sin conexión con el servidor. Reintentando...");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 2 + failures * 2)), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static async Task AcceptAsync(string relayBase, string room, string secret, string guest, Action<PeerConnection, string> onGuest, CancellationToken ct)
    {
        var socket = NewSocket();
        try
        {
            await socket.ConnectAsync(new Uri($"{relayBase}/ws/accept?room={room}&guest={Uri.EscapeDataString(guest)}"), ct);
            var secure = await SecureStream.HandshakeAsync(new WebSocketStream(socket), secret, isHost: true, ct);
            onGuest(new PeerConnection(secure), "relay-" + guest);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
        {
            socket.Dispose(); // somebody without the right invitation: nothing gets in
        }
    }
}
