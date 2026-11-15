using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TecLink.Relay;

/// <summary>The online relay: a meeting point for two PCs that cannot reach each other directly (another city, a router that does not
/// accept connections from outside). It knows nothing about the people: no accounts, no email, no logs, nothing stored. It only joins two
/// connections that arrive at the same room id (a hash of the invitation's secret) and copies encrypted bytes between them. It cannot read,
/// change or invent anything, because the key never leaves the two PCs.</summary>
public static class RelayServer
{
    public const int MaxRooms = 5000;
    public const int MaxGuestsPerRoom = 9;
    public const int MaxSplices = 4000;
    public const int JoinsPerMinutePerAddress = 40;

    private sealed class Room
    {
        public required WebSocket Host;
        public readonly ConcurrentDictionary<string, Pending> Waiting = new();
        public int Guests;
    }
    private sealed record Pending(TaskCompletionSource<WebSocket> Socket, TaskCompletionSource Done);

    private static readonly ConcurrentDictionary<string, Room> Rooms = new();
    private static readonly ConcurrentDictionary<string, (long Minute, int Count)> Attempts = new();
    private static int _splices;

    public static int RoomCount => Rooms.Count;

    public static void Map(WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapGet("/", () => "TecLink relay: only encrypted traffic passes through here.");
        app.MapGet("/health", () => Results.Ok(new { ok = true, rooms = RoomCount }));
        app.Map("/ws/host", HostAsync);
        app.Map("/ws/join", JoinAsync);
        app.Map("/ws/accept", AcceptAsync);
    }

    private static bool ValidRoom(string? room) => room is { Length: 32 } && room.All(Uri.IsHexDigit);

    private static string Address(HttpContext ctx) =>
        ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    private static bool Allowed(string address)
    {
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var entry = Attempts.AddOrUpdate(address, (minute, 1), (_, old) => old.Minute == minute ? (minute, old.Count + 1) : (minute, 1));
        if (Attempts.Count > 20000) Attempts.Clear(); // never grows without limit
        return entry.Count <= JoinsPerMinutePerAddress;
    }

    private static async Task HostAsync(HttpContext ctx)
    {
        var room = ctx.Request.Query["room"].ToString();
        if (!ctx.WebSockets.IsWebSocketRequest || !ValidRoom(room)) { ctx.Response.StatusCode = 400; return; }
        if (Rooms.Count >= MaxRooms && !Rooms.ContainsKey(room)) { ctx.Response.StatusCode = 503; return; }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var mine = new Room { Host = socket };
        if (Rooms.TryGetValue(room, out var old)) { try { old.Host.Abort(); } catch (Exception) { } }
        Rooms[room] = mine;
        try
        {
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally { Rooms.TryRemove(new KeyValuePair<string, Room>(room, mine)); }
    }

    private static async Task JoinAsync(HttpContext ctx)
    {
        var room = ctx.Request.Query["room"].ToString();
        if (!ctx.WebSockets.IsWebSocketRequest || !ValidRoom(room)) { ctx.Response.StatusCode = 400; return; }
        if (!Allowed(Address(ctx))) { ctx.Response.StatusCode = 429; return; }
        using var guest = await ctx.WebSockets.AcceptWebSocketAsync();
        if (!Rooms.TryGetValue(room, out var r) || r.Host.State != WebSocketState.Open) { await CloseAsync(guest, "nadie ha abierto esta sala"); return; }
        if (Interlocked.Increment(ref r.Guests) > MaxGuestsPerRoom || Volatile.Read(ref _splices) >= MaxSplices) { Interlocked.Decrement(ref r.Guests); await CloseAsync(guest, "sala llena"); return; }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var pending = new Pending(new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
        r.Waiting[id] = pending;
        try
        {
            await r.Host.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { guest = id })), WebSocketMessageType.Text, true, ctx.RequestAborted);
            var hostSide = await pending.Socket.Task.WaitAsync(TimeSpan.FromSeconds(20), ctx.RequestAborted);
            Interlocked.Increment(ref _splices);
            try { await SpliceAsync(guest, hostSide, ctx.RequestAborted); }
            finally { Interlocked.Decrement(ref _splices); pending.Done.TrySetResult(); }
        }
        catch (Exception ex) when (ex is TimeoutException or WebSocketException or OperationCanceledException) { await CloseAsync(guest, "el dueño no respondió"); }
        finally { r.Waiting.TryRemove(id, out _); Interlocked.Decrement(ref r.Guests); }
    }

    private static async Task AcceptAsync(HttpContext ctx)
    {
        var room = ctx.Request.Query["room"].ToString(); var guest = ctx.Request.Query["guest"].ToString();
        if (!ctx.WebSockets.IsWebSocketRequest || !ValidRoom(room) || guest.Length is 0 or > 32) { ctx.Response.StatusCode = 400; return; }
        if (!Rooms.TryGetValue(room, out var r) || !r.Waiting.TryRemove(guest, out var pending)) { ctx.Response.StatusCode = 404; return; }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        pending.Socket.TrySetResult(socket);
        try { await pending.Done.Task.WaitAsync(ctx.RequestAborted); } catch (OperationCanceledException) { }
    }

    /// <summary>Copies whole messages both ways until either side closes. Nothing is inspected, stored or logged.</summary>
    private static async Task SpliceAsync(WebSocket a, WebSocket b, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ab = PumpAsync(a, b, stop.Token); var ba = PumpAsync(b, a, stop.Token);
        await Task.WhenAny(ab, ba);
        stop.Cancel();
        try { a.Abort(); b.Abort(); } catch (Exception) { }
        try { await Task.WhenAll(ab, ba); } catch (Exception) { }
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[70_000];
        try
        {
            while (from.State == WebSocketState.Open && to.State == WebSocketState.Open)
            {
                var result = await from.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                await to.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        try { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None); } catch (Exception) { }
    }
}
