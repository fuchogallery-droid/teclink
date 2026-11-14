using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using TecLink.Core.Protocol;

namespace TecLink.Core.Networking;

/// <summary>A connection that already showed the right invitation code. Messages that arrived
/// right behind the code (the guest's Hello) are held until <see cref="Attach"/> replays them in order.</summary>
public sealed class AdmittedConnection
{
    private readonly object _gate = new();
    private readonly List<WireMessage> _held = new();
    private Action<WireMessage>? _live;

    public PeerConnection Connection { get; }
    internal AdmittedConnection(PeerConnection connection) => Connection = connection;

    internal void Receive(WireMessage message)
    {
        Action<WireMessage>? live;
        lock (_gate) { live = _live; if (live is null) { _held.Add(message); return; } }
        live(message);
    }

    /// <summary>Hands every held message (including the Join) to <paramref name="handler"/>, then everything that follows.</summary>
    public void Attach(Action<WireMessage> handler)
    {
        lock (_gate)
        {
            foreach (var message in _held) handler(message);
            _held.Clear();
            _live = handler;
        }
    }
}

/// <summary>
/// Host side of a session: keeps the TCP port open all the time. Every new connection must send
/// the invitation code within the timeout; the valid ones are queued, so a guest who comes back
/// replaces one whose link died silently (sleep, Wi-Fi drop) instead of being refused.
/// </summary>
public sealed class HostAdmission : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string?> _expectedCode;
    private readonly TimeSpan _timeout;
    private readonly Channel<AdmittedConnection> _admitted = Channel.CreateUnbounded<AdmittedConnection>();
    private readonly CancellationTokenSource _cts;
    private readonly Task _acceptLoop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ChannelReader<AdmittedConnection> Guests => _admitted.Reader;
    /// <summary>(remote address, reason) for connections that did not get in.</summary>
    public event Action<string, string>? Refused;

    public HostAdmission(int port, Func<string?> expectedCode, TimeSpan joinTimeout, CancellationToken ct)
    {
        _expectedCode = expectedCode; _timeout = joinTimeout;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = AdmitAsync(client, ct); // concurrently: a silent stranger must not delay the real guest
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        finally { _admitted.Writer.TryComplete(); }
    }

    private async Task AdmitAsync(TcpClient client, CancellationToken ct)
    {
        var who = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        PeerConnection? connection = null;
        try
        {
            connection = new PeerConnection(client);
            var holder = new AdmittedConnection(connection);
            var joined = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.MessageReceived += message => { if (message.Type == MessageType.Join) joined.TrySetResult(message); holder.Receive(message); };
            connection.Disconnected += () => joined.TrySetCanceled();
            connection.StartReceiving();

            WireMessage join;
            try { join = await joined.Task.WaitAsync(_timeout, ct); }
            catch (TimeoutException) { Refused?.Invoke(who, "no envió el código a tiempo"); await connection.DisposeAsync(); return; }
            catch (TaskCanceledException) { await connection.DisposeAsync(); return; }

            var expected = SessionDiscovery.Normalize(_expectedCode());
            if (expected is null || SessionDiscovery.Normalize(join.Text) != expected)
            {
                await connection.SendAsync(WireMessage.JoinResult(false, "Código incorrecto"));
                await Task.Delay(300, ct);
                Refused?.Invoke(who, "código incorrecto");
                await connection.DisposeAsync();
                return;
            }
            if (!_admitted.Writer.TryWrite(holder)) await connection.DisposeAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
        {
            if (connection is not null) try { await connection.DisposeAsync(); } catch (Exception) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch (Exception) { }
        while (_admitted.Reader.TryRead(out var left)) await left.Connection.DisposeAsync();
        _cts.Dispose();
    }
}
