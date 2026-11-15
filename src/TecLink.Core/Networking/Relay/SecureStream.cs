using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TecLink.Core.Networking.Relay;

/// <summary>End-to-end encryption between the two PCs of a room, whatever carries the bytes in between (the online relay included).
/// The key comes from the invitation's secret, which never leaves either PC: a relay (or anybody listening) only sees encrypted data
/// and cannot read, change or replay it. A PC that does not hold the secret cannot even complete the handshake.</summary>
public sealed class SecureStream : Stream
{
    private const int MaxChunk = 60_000;
    private const int Tag = 16;
    private readonly Stream _inner;
    private readonly AesGcm _sendKey, _receiveKey;
    private ulong _sendCounter, _receiveCounter;
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingAt;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private SecureStream(Stream inner, byte[] sendKey, byte[] receiveKey)
    {
        _inner = inner; _sendKey = new AesGcm(sendKey, Tag); _receiveKey = new AesGcm(receiveKey, Tag);
    }

    /// <summary>Both sides send a random nonce, derive two keys (one per direction) from the secret and prove they got the same ones.</summary>
    public static async Task<SecureStream> HandshakeAsync(Stream inner, string secret, bool isHost, CancellationToken ct)
    {
        var mine = RandomNumberGenerator.GetBytes(16);
        await inner.WriteAsync(mine, ct); await inner.FlushAsync(ct);
        var theirs = new byte[16];
        await ReadExactAsync(inner, theirs, ct);
        var salt = isHost ? mine.Concat(theirs).ToArray() : theirs.Concat(mine).ToArray();
        var ikm = Encoding.UTF8.GetBytes("teclink-v1|" + secret.ToUpperInvariant());
        var hostToGuest = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.ASCII.GetBytes("teclink host->guest"));
        var guestToHost = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.ASCII.GetBytes("teclink guest->host"));
        var stream = isHost ? new SecureStream(inner, hostToGuest, guestToHost) : new SecureStream(inner, guestToHost, hostToGuest);
        // Key confirmation: each side sends a sealed word the other can only open with the same secret.
        await stream.WriteAsync(Encoding.ASCII.GetBytes(isHost ? "TECLINK-H" : "TECLINK-G"), ct);
        var expected = Encoding.ASCII.GetBytes(isHost ? "TECLINK-G" : "TECLINK-H");
        var got = new byte[expected.Length];
        try { await ReadExactAsync(stream, got, ct); }
        catch (CryptographicException) { throw new InvalidDataException("La invitación no coincide."); }
        if (!got.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("La invitación no coincide.");
        return stream;
    }

    private static async Task ReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static byte[] NonceFor(ulong counter) { var nonce = new byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter); return nonce; }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            for (var offset = 0; offset < data.Length; offset += MaxChunk)
            {
                var chunk = data.Slice(offset, Math.Min(MaxChunk, data.Length - offset));
                var frame = new byte[4 + chunk.Length + Tag];
                BinaryPrimitives.WriteInt32LittleEndian(frame, chunk.Length + Tag);
                _sendKey.Encrypt(NonceFor(_sendCounter++), chunk.Span, frame.AsSpan(4, chunk.Length), frame.AsSpan(4 + chunk.Length, Tag));
                await _inner.WriteAsync(frame, ct); // one write per frame: over a WebSocket it is one message
            }
        }
        finally { _writeGate.Release(); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pendingAt >= _pending.Length)
        {
            var header = new byte[4];
            try { await ReadExactAsync(_inner, header, ct); } catch (EndOfStreamException) { return 0; }
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < Tag or > MaxChunk + Tag) throw new InvalidDataException("Marco cifrado inválido.");
            var body = new byte[length];
            try { await ReadExactAsync(_inner, body, ct); } catch (EndOfStreamException) { return 0; }
            var plain = new byte[length - Tag];
            _receiveKey.Decrypt(NonceFor(_receiveCounter++), body.AsSpan(0, plain.Length), body.AsSpan(plain.Length, Tag), plain);
            _pending = plain; _pendingAt = 0;
        }
        var n = Math.Min(buffer.Length, _pending.Length - _pendingAt);
        _pending.AsMemory(_pendingAt, n).CopyTo(buffer);
        _pendingAt += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _inner.Dispose(); _sendKey.Dispose(); _receiveKey.Dispose(); _writeGate.Dispose(); }
        base.Dispose(disposing);
    }
}
