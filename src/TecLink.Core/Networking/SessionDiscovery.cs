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
    public static async Task RunResponderAsync(Func<string> code, int tcpPort, CancellationToken ct, Action<Exception>? onError = null)
    {
        UdpClient udp;
        try { udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort)) { EnableBroadcast = true }; }
        catch (SocketException ex) { onError?.Invoke(ex); return; } // port taken (another TecLink?) or blocked
        using var _ = udp;
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

    /// <summary>Asks one address "do you host the session with this code?". Needs only an OUTBOUND connection.</summary>
    public static async Task<bool> ProbeAsync(IPAddress address, int port, string code, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        PeerConnection? connection = null;
        try
        {
            connection = await PeerServer.ConnectAsync(address.ToString(), port, linked.Token);
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.MessageReceived += m => { if (m.Type == Protocol.MessageType.JoinResult) answer.TrySetResult(m.Accepted); };
            connection.Disconnected += () => answer.TrySetResult(false);
            connection.StartReceiving();
            await connection.SendAsync(Protocol.WireMessage.ProbeMsg(code));
            return await answer.Task.WaitAsync(linked.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { return false; }
        finally { if (connection is not null) try { await connection.DisposeAsync(); } catch (Exception) { } }
    }

    /// <summary>The other addresses of every local IPv4 network (its /24), without this PC.</summary>
    public static List<IPAddress> LocalSubnetCandidates()
    {
        var own = new HashSet<string>(); var result = new List<IPAddress>(); var seen = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            foreach (var ua in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                own.Add(ua.Address.ToString());
        foreach (var ip in own.Where(a => !a.StartsWith("169.254.")))
        {
            var parts = ip.Split('.'); var prefix = string.Join('.', parts.Take(3));
            for (var host = 1; host < 255; host++) { var candidate = $"{prefix}.{host}"; if (!own.Contains(candidate) && seen.Add(candidate)) result.Add(IPAddress.Parse(candidate)); }
        }
        return result;
    }

    /// <summary>One pass over the candidates; the first that confirms the code wins.</summary>
    public static async Task<IPEndPoint?> SweepAsync(IEnumerable<IPAddress> candidates, int port, string code, TimeSpan perHost, CancellationToken ct, int parallel = 64)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(parallel);
        var tasks = candidates.Select(async address =>
        {
            await gate.WaitAsync(stop.Token);
            try { if (await ProbeAsync(address, port, code, perHost, stop.Token)) { stop.Cancel(); return new IPEndPoint(address, port); } }
            catch (OperationCanceledException) { }
            finally { gate.Release(); }
            return null;
        }).ToList();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        return tasks.Select(t => t.IsCompletedSuccessfully ? t.Result : null).FirstOrDefault(r => r is not null);
    }

    /// <summary>
    /// Finds the host of a session: asks the network with UDP and, at the same time, tries every local
    /// address over TCP. The TCP way works even when this PC's firewall drops the UDP answers.
    /// </summary>
    public static async Task<(IPEndPoint? Host, string How)> FindHostAsync(string code, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var all = CancellationTokenSource.CreateLinkedTokenSource(ct);
        all.CancelAfter(timeout);
        var udp = FindAsync(code, timeout, all.Token);
        var tcp = Task.Run(async () =>
        {
            while (!all.IsCancellationRequested)
            {
                var found = await SweepAsync(LocalSubnetCandidates(), port, code, TimeSpan.FromMilliseconds(700), all.Token);
                if (found is not null) return found;
                await Task.Delay(500, all.Token);
            }
            return null;
        }, all.Token);
        var first = await Task.WhenAny(udp, tcp);
        IPEndPoint? hit = null; var how = "";
        try { hit = await first; how = first == udp ? "UDP" : "TCP"; } catch (OperationCanceledException) { }
        if (hit is null)
        {
            try { hit = await (first == udp ? tcp : udp); how = first == udp ? "TCP" : "UDP"; } catch (OperationCanceledException) { }
        }
        all.Cancel();
        return (hit, how);
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
