using System.Net.Sockets;
using System.Diagnostics;
using System.Buffers.Binary;
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
    private readonly object _queueLock = new();
    private readonly LinkedList<Outbound> _outbound = new();
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sendTask;
    private Task? _receiveTask;
    private bool _wakePending;
    private bool _closed;
    private int _disconnected;
    private long _nextMouseSend;
    private sealed record Outbound(byte[] Frame, bool Mouse, TaskCompletionSource Completion);

    public event Action<WireMessage>? MessageReceived;
    public event Action? Disconnected;

    public PeerConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true; // low latency matters more than throughput here
        try
        {
            // A PC that sleeps, loses Wi-Fi or is switched off never says goodbye: probe an idle link
            // (10 s, then every 3 s, 3 times) so a dead peer is noticed in ~20 s instead of never.
            var socket = client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException) { }
        _stream = client.GetStream();
        _sendTask = SendLoopAsync();
    }

    public bool IsDisconnected => Volatile.Read(ref _disconnected) == 1;

    public void StartReceiving() => _receiveTask ??= ReceiveLoopAsync(_cts.Token);

    public Task SendAsync(WireMessage message)
    {
        var payload = message.Encode();
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        var work = new Outbound(frame, message.Type == MessageType.MouseMove,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_queueLock)
        {
            if (_closed) return Task.CompletedTask;
            // Only adjacent moves may replace each other. Keys, clicks and handoffs
            // form ordering barriers and are never discarded or overtaken.
            if (work.Mouse && _outbound.Last?.Value is { Mouse: true } previous)
            {
                _outbound.RemoveLast();
                previous.Completion.TrySetResult(); // superseded, not an unsent key
            }
            _outbound.AddLast(work);
            if (!_wakePending)
            {
                _wakePending = true;
                _sendSignal.Release();
            }
        }
        return work.Completion.Task;
    }

    private async Task SendLoopAsync()
    {
        Outbound? current = null;
        try
        {
            while (true)
            {
                await _sendSignal.WaitAsync(_cts.Token);
                lock (_queueLock) _wakePending = false;
                while (true)
                {
                    bool mouse;
                    lock (_queueLock)
                    {
                        if (_outbound.First is null) break;
                        mouse = _outbound.First.Value.Mouse;
                    }
                    // At most 120 mouse updates/s, retaining the latest position
                    // instead of accumulating seconds of stale motion on Wi-Fi.
                    var remaining = _nextMouseSend - Stopwatch.GetTimestamp();
                    if (mouse && remaining > 0)
                        await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), _cts.Token);
                    lock (_queueLock)
                    {
                        if (_outbound.First is null) break;
                        current = _outbound.First.Value;
                        _outbound.RemoveFirst();
                    }
                    if (current.Mouse) _nextMouseSend = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 120;
                    // One write per frame avoids separate tiny TCP packets for
                    // the length header and its payload. NoDelay remains enabled.
                    await _stream.WriteAsync(current.Frame, _cts.Token);
                    current.Completion.TrySetResult();
                    current = null;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_cts.IsCancellationRequested) { }
        catch (IOException) { SignalDisconnected(); }
        catch (SocketException) { SignalDisconnected(); }
        finally
        {
            current?.Completion.TrySetResult();
            lock (_queueLock)
            {
                _closed = true;
                foreach (var pending in _outbound) pending.Completion.TrySetResult();
                _outbound.Clear();
            }
        }
    }

    private void SignalDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 0) Disconnected?.Invoke();
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
                if (length is < 0 or > 32_000_000) break; // sanity guard against a corrupt stream

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
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // corrupt frame: drop the link and let the caller reconnect
        }

        SignalDisconnected();
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
        await _sendTask;
        if (_receiveTask is { } receive) await receive;
        _sendSignal.Dispose();
        _cts.Dispose();
    }
}
