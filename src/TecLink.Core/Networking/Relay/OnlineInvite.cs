using System.Security.Cryptography;
using System.Text;

namespace TecLink.Core.Networking.Relay;

/// <summary>The invitation of an online room: the 6-letter session code plus a 16-character secret (80 bits), optionally followed by
/// "@relay-host". The secret never goes to the relay: the relay only sees a hash of it (the room id) and encrypted data.</summary>
public static class OnlineInvite
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int SecretLength = 16;

    public static string NewSecret()
    {
        var chars = new char[SecretLength];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>What the relay sees: a hash, never the secret.</summary>
    public static string RoomId(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("teclink-room|" + secret.ToUpperInvariant())))[..32].ToLowerInvariant();

    /// <summary>ABC-DEF-GHJK-MNPQ-RSTU-VWXY (code, then secret), with "@host" if the relay is not the default one.</summary>
    public static string Format(string code, string secret, string? relayHost = null)
    {
        var all = code + secret;
        var pretty = string.Join('-', new[] { all[..3], all[3..6], all[6..10], all[10..14], all[14..18], all[18..22] });
        return string.IsNullOrWhiteSpace(relayHost) ? pretty : pretty + "@" + relayHost.Trim();
    }

    /// <summary>True when the text is an online invitation (22 letters/numbers, optionally "@host").</summary>
    public static bool TryParse(string? text, out string code, out string secret, out string? relayHost)
    {
        code = secret = ""; relayHost = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('@', 2);
        var clean = new string(parts[0].Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length != 6 + SecretLength || !clean.All(Alphabet.Contains)) return false;
        code = clean[..6]; secret = clean[6..];
        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])) relayHost = parts[1].Trim();
        return true;
    }

    /// <summary>"relay.example.com" or a full ws/wss URL -> the websocket base URL (wss by default).</summary>
    public static string RelayBase(string hostOrUrl)
    {
        var text = hostOrUrl.Trim().TrimEnd('/');
        if (text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) return text;
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + text[8..];
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + text[7..];
        // A relay on this machine or this network (development) has no certificate: plain ws. Any real relay is wss.
        if (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || text.StartsWith("127.") || text.StartsWith("192.168.") || text.StartsWith("10.")) return "ws://" + text;
        return "wss://" + text;
    }
}
