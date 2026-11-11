using System.Net.Sockets;
using TecLink.Core.Protocol;

namespace TecLink.Core.Networking;

/// <summary>
/// Length-prefixed message channel over a TCP connection. One instance per peer.
/// Thread-safe for concurrent Send calls (mouse hook, keyboard hook and clipboard watcher
/// all run on different threads).
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    public event Action<WireMessage>? MessageReceived;
    public event Action? Disconnected;

    public PeerConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true; // low latency matters more than throughput here
        _stream = client.GetStream();
    }

    public void StartReceiving() => _ = ReceiveLoopAsync(_cts.Token);

    public async Task SendAsync(WireMessage message)
    {
        var payload = message.Encode();
        var header = BitConverter.GetBytes(payload.Length);

        await _sendLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(header);
            await _stream.WriteAsync(payload);
        }
        catch (IOException)
        {
            Disconnected?.Invoke();
        }
        catch (SocketException)
        {
            Disconnected?.Invoke();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(header, 4, ct)) break;
                var length = BitConverter.ToInt32(header, 0);
                if (length is < 0 or > 1_000_000) break; // sanity guard against a corrupt stream

                var payload = new byte[length];
                if (!await ReadExactAsync(payload, length, ct)) break;

                MessageReceived?.Invoke(WireMessage.Decode(payload));
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // shutting down, ignore
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }

        Disconnected?.Invoke();
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) return false; // remote closed the connection
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _stream.Dispose();
        _client.Dispose();
        _sendLock.Dispose();
        await Task.CompletedTask;
    }
}
