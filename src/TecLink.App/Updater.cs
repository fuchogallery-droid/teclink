using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TecLink.App;

/// <summary>What the public update file says (update.json in the repository): the newest version, where its installer is, its fingerprint,
/// and two switches the owner controls: "enabled" (updates can be turned off with a message) and "keys" (when there are hashes, the update
/// needs a key whose hash is in the list: that is the paywall).</summary>
internal sealed record UpdateInfo(bool Ok, bool Newer, string Latest, string Current, bool Enabled, string Message, string BuyUrl,
    bool NeedKey, string[] KeyHashes, string Url, string Sha256, long Size);

/// <summary>One-click update. Nothing runs by itself: it only talks to the internet when the person presses "Actualizar".
/// The installer is accepted only from the project's GitHub releases and only if its size and SHA-256 match update.json.</summary>
internal static class Updater
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/fuchogallery-droid/teclink/master/update.json";
    private const string AllowedPrefix = "https://github.com/fuchogallery-droid/teclink/releases/download/";

    public static Version Current
    {
        get { var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0); return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
    }

    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((key ?? "").Trim().ToUpperInvariant()))).ToLowerInvariant();
    public static bool KeyAccepted(UpdateInfo i, string key) => !i.NeedKey || (key.Trim().Length > 0 && i.KeyHashes.Contains(HashKey(key)));

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var current = Current;
        var fail = new UpdateInfo(false, false, "", current.ToString(), true, "", "", false, [], "", "", 0);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TecLink-update");
            var json = await http.GetStringAsync(ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Text(string name) => r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            var latest = Text("version");
            var keys = r.TryGetProperty("keys", out var k) && k.ValueKind == JsonValueKind.Array ? k.EnumerateArray().Select(x => (x.GetString() ?? "").ToLowerInvariant()).Where(x => x.Length == 64).ToArray() : [];
            var enabled = !r.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
            var needKey = r.TryGetProperty("requiresKey", out var rk) && rk.ValueKind == JsonValueKind.True;
            var size = r.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            return new UpdateInfo(true, Version.TryParse(latest, out var v) && v > current, latest, current.ToString(), enabled, Text("message"), Text("buyUrl"), needKey, keys, Text("url"), Text("sha256").ToLowerInvariant(), size);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return fail; }
    }

    /// <summary>Downloads the installer to a private folder and checks it. Returns its path, or throws InvalidDataException when it does not match.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, Action<int> progress, CancellationToken ct = default)
    {
        if (!info.Url.StartsWith(AllowedPrefix, StringComparison.Ordinal) || !info.Url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || info.Sha256.Length != 64 || info.Size <= 0)
            throw new InvalidDataException("La información de la actualización no es válida.");
        var dir = Path.Combine(Path.GetTempPath(), "TecLink-Update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "TecLink-Setup-" + info.Latest + ".exe");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TecLink-update");
        using (var response = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(target);
            var buffer = new byte[81920]; long total = 0; int last = -1, read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct); total += read;
                var pct = (int)(total * 100 / info.Size); if (pct != last && pct <= 100) { last = pct; progress(pct); }
            }
        }
        var ok = new FileInfo(target).Length == info.Size;
        if (ok) { await using var f = File.OpenRead(target); ok = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase); }
        if (!ok) { try { File.Delete(target); } catch (IOException) { } throw new InvalidDataException("El archivo descargado no coincide con el original. No se instaló."); }
        return target;
    }
}
