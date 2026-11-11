using System.Net;
using System.Net.Sockets;

namespace TecLink.Core.Networking;

/// <summary>
/// MVP is two machines, so the server only needs to accept a single peer at a time.
/// </summary>
public static class PeerServer
{
    public const int DefaultPort = 51777;

    public static async Task<PeerConnection> AcceptOneAsync(int port, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        try
        {
            using var reg = ct.Register(listener.Stop);
            var client = await listener.AcceptTcpClientAsync(ct);
            return new PeerConnection(client);
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task<PeerConnection> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        return new PeerConnection(client);
    }
}
