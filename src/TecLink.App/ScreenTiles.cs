using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TecLink.App;

/// <summary>Small live pictures of several monitors of THIS machine at once (the tiles of the room).
/// A few frames per second each; a monitor that did not change is not sent again, and the next
/// picture waits for the previous one to be delivered, so a slow link lowers the rate instead of piling up.</summary>
internal sealed class ScreenTiles : IDisposable
{
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private CancellationTokenSource? _cts;

    public bool Active => _cts is not null;

    /// <param name="indices">Monitors to show (index into Screen.AllScreens); empty = all of them.</param>
    public void Start(int[] indices, int maxWidth, Func<int, byte[], Task> sink)
    {
        Stop();
        var cts = _cts = new CancellationTokenSource();
        maxWidth = Math.Clamp(maxWidth, 160, 2560);
        _ = Task.Run(() => RunAsync(indices, maxWidth, sink, cts.Token));
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    private static async Task RunAsync(int[] indices, int maxWidth, Func<int, byte[], Task> sink, CancellationToken ct)
    {
        var hashes = new Dictionary<int, (ulong Hash, long At)>();
        using var parameters = new EncoderParameters(1);
        // Text must stay readable: a good quality, and the picture is only shrunk as much as the card really needs.
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, maxWidth > 1200 ? 78L : 82L);
        var interval = maxWidth > 1500 ? 380 : maxWidth > 900 ? 300 : 240;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = Environment.TickCount64;
                var screens = Screen.AllScreens;
                var wanted = indices.Length == 0 ? Enumerable.Range(0, screens.Length) : indices.Where(i => i >= 0 && i < screens.Length);
                foreach (var index in wanted.ToArray())
                {
                    ct.ThrowIfCancellationRequested();
                    byte[]? jpeg = null;
                    try
                    {
                        var bounds = screens[index].Bounds;
                        var ratio = Math.Min(1.0, (double)maxWidth / bounds.Width);
                        var size = new Size(Math.Max(1, (int)(bounds.Width * ratio)), Math.Max(1, (int)(bounds.Height * ratio)));
                        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                        using (var g = Graphics.FromImage(full))
                        {
                            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                            ScreenShareHost.DrawCursor(g, bounds);
                        }
                        using var small = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.SmoothingMode = SmoothingMode.HighQuality;
                            g.DrawImage(full, new Rectangle(Point.Empty, size));
                        }
                        using var stream = new MemoryStream();
                        small.Save(stream, Jpeg, parameters);
                        jpeg = stream.ToArray();
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
                    {
                        // Lock screen or UAC prompt: nothing can be captured right now.
                    }
                    if (jpeg is null) continue;
                    var hash = Hash(jpeg); var now = Environment.TickCount64;
                    // An unchanged monitor is repeated every 4 s so a tile that just opened still gets its picture.
                    if (hashes.TryGetValue(index, out var last) && last.Hash == hash && now - last.At < 4000) continue;
                    hashes[index] = (hash, now);
                    await sink(index, jpeg);
                }
                var wait = interval - (int)(Environment.TickCount64 - started);
                await Task.Delay(Math.Max(40, wait), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Logger.LogError(ex); }
    }

    private static ulong Hash(byte[] data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data) hash = (hash ^ b) * 1099511628211UL;
        return hash;
    }

    public void Dispose() => Stop();
}
