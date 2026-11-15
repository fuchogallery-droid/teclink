using System.Net.WebSockets;

namespace TecLink.Core.Networking.Relay;

/// <summary>A WebSocket seen as a plain byte stream (each write is one binary message).</summary>
public sealed class WebSocketStream : Stream
{
    private readonly WebSocket _socket;
    public WebSocketStream(WebSocket socket) => _socket = socket;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseSent)) return 0;
        try
        {
            var result = await _socket.ReceiveAsync(buffer, ct);
            return result.MessageType == WebSocketMessageType.Close ? 0 : result.Count;
        }
        catch (WebSocketException) { return 0; }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        try { await _socket.SendAsync(buffer, WebSocketMessageType.Binary, true, ct); }
        catch (WebSocketException ex) { throw new IOException(ex.Message, ex); }
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _socket.Abort(); } catch (Exception) { } _socket.Dispose(); }
        base.Dispose(disposing);
    }
}
