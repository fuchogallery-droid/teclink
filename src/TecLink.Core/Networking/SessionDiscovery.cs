using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace TecLink.Core.Networking;

/// <summary>Session invitation codes and LAN discovery: the guest types only the code and
/// finds the host by asking the local network "who has this code?" over UDP.</summary>
public static class SessionDiscovery
{
    public const int DiscoveryPort = 51778;
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I/L

    public static string NewCode()
    {
        var chars = new char[6];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Uppercase, no spaces or dashes. Returns null when it cannot be a code.</summary>
    public static string? Normalize(string? code)
    {
        if (code is null) return null;
        var clean = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return clean.Length == 6 && clean.All(Alphabet.Contains) ? clean : null;
    }

    public static string Pretty(string code) => code.Length == 6 ? code[..3] + "-" + code[3..] : code;

    /// <summary>Host side: answers "TECLINK?CODE" with its TCP port while the code matches.</summary>
    public static async Task RunResponderAsync(Func<string> code, int tcpPort, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort)) { EnableBroadcast = true };
        using var reg = ct.Register(udp.Dispose);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text.StartsWith("TECLINK?", StringComparison.Ordinal) && Normalize(text[8..]) == code())
                {
                    var reply = Encoding.UTF8.GetBytes("TECLINK!" + tcpPort);
                    await udp.SendAsync(reply, reply.Length, result.RemoteEndPoint);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    /// <summary>Guest side: broadcasts on every network card and waits for the host's answer.</summary>
    public static async Task<IPEndPoint?> FindAsync(string code, TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var query = Encoding.UTF8.GetBytes("TECLINK?" + code);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var ip in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null))
            {
                var a = ip.Address.GetAddressBytes(); var m = ip.IPv4Mask!.GetAddressBytes();
                targets.Add(new IPAddress(a.Select((b, i) => (byte)(b | ~m[i])).ToArray()));
            }
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                foreach (var target in targets) { try { await udp.SendAsync(query, query.Length, new IPEndPoint(target, DiscoveryPort)); } catch (SocketException) { } }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
                wait.CancelAfter(1000);
                try
                {
                    var reply = await udp.ReceiveAsync(wait.Token);
                    var text = Encoding.UTF8.GetString(reply.Buffer);
                    if (text.StartsWith("TECLINK!", StringComparison.Ordinal) && int.TryParse(text[8..], out var port))
                        return new IPEndPoint(reply.RemoteEndPoint.Address, port);
                }
                catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }
}
