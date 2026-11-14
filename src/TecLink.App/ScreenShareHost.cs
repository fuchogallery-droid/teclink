using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TecLink.App.Native;
using TecLink.Core.Networking;
using TecLink.Core.Protocol;

namespace TecLink.App;

/// <summary>Captures one monitor of THIS machine and streams it to the peer as JPEG
/// frames. The next frame is only sent after the viewer acknowledged the previous one,
/// so a slow network lowers the frame rate instead of building up delay.</summary>
internal sealed class ScreenShareHost : IDisposable
{
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private readonly SemaphoreSlim _ack = new(0, 1);
    private CancellationTokenSource? _cts;

    public bool Active => _cts is not null;

    public void Start(PeerConnection connection, int index, int maxWidth, int maxHeight) =>
        Start(index, maxWidth, maxHeight, message => connection.SendAsync(message));

    /// <summary>Streams to any destination (the peer, or a window on this same PC). Whoever receives the frame must call <see cref="Ack"/>.</summary>
    public void Start(int index, int maxWidth, int maxHeight, Func<WireMessage, Task> send)
    {
        Stop();
        var cts = _cts = new CancellationTokenSource();
        maxWidth = Math.Clamp(maxWidth, 320, 7680);
        maxHeight = Math.Clamp(maxHeight, 200, 4320);
        _ = Task.Run(() => RunAsync(send, index, maxWidth, maxHeight, cts.Token));
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    public void Ack()
    {
        try { if (_ack.CurrentCount == 0) _ack.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RunAsync(Func<WireMessage, Task> send, int index, int maxWidth, int maxHeight, CancellationToken ct)
    {
        Bitmap? full = null, scaled = null;
        ulong lastHash = 0;
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = Environment.TickCount64;
                byte[]? jpeg = null;
                Rectangle bounds = default, desktop = default;
                try
                {
                    var screens = Screen.AllScreens;
                    bounds = screens[Math.Clamp(index, 0, screens.Length - 1)].Bounds;
                    desktop = screens.Select(s => s.Bounds).Aggregate(Rectangle.Union);
                    if (full is null || full.Size != bounds.Size) { full?.Dispose(); full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb); }
                    using (var g = Graphics.FromImage(full))
                    {
                        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                        DrawCursor(g, bounds);
                    }
                    var ratio = Math.Min(1.0, Math.Min((double)maxWidth / bounds.Width, (double)maxHeight / bounds.Height));
                    var source = full;
                    if (ratio < 0.999)
                    {
                        var size = new Size(Math.Max(1, (int)(bounds.Width * ratio)), Math.Max(1, (int)(bounds.Height * ratio)));
                        if (scaled is null || scaled.Size != size) { scaled?.Dispose(); scaled = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb); }
                        using var g = Graphics.FromImage(scaled);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(full, new Rectangle(Point.Empty, size));
                        source = scaled;
                    }
                    using var stream = new MemoryStream();
                    source.Save(stream, Jpeg, parameters);
                    jpeg = stream.ToArray();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
                {
                    // Lock screen or UAC prompt: nothing can be captured right now.
                }

                if (jpeg is not null)
                {
                    var hash = Hash(jpeg);
                    if (hash != lastHash)
                    {
                        lastHash = hash;
                        while (_ack.CurrentCount > 0) await _ack.WaitAsync(ct);
                        await send(WireMessage.ScreenFrame(index, bounds.Left - desktop.Left, bounds.Top - desktop.Top, bounds.Width, bounds.Height, jpeg));
                        await _ack.WaitAsync(3000, ct);
                    }
                }
                var wait = 40 - (int)(Environment.TickCount64 - started);
                await Task.Delay(Math.Max(5, wait), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Logger.LogError(ex); }
        finally { full?.Dispose(); scaled?.Dispose(); }
    }

    internal static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var info = new Win32.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.CURSORINFO>() };
        if (!Win32.GetCursorInfo(ref info) || info.flags != Win32.CURSOR_SHOWING || info.hCursor == IntPtr.Zero) return;
        int hotX = 0, hotY = 0;
        if (Win32.GetIconInfo(info.hCursor, out var icon))
        {
            hotX = icon.xHotspot; hotY = icon.yHotspot;
            if (icon.hbmColor != IntPtr.Zero) Win32.DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) Win32.DeleteObject(icon.hbmMask);
        }
        var hdc = g.GetHdc();
        try { Win32.DrawIconEx(hdc, info.ptScreenPos.x - bounds.Left - hotX, info.ptScreenPos.y - bounds.Top - hotY, info.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
        finally { g.ReleaseHdc(hdc); }
    }

    private static ulong Hash(byte[] data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data) hash = (hash ^ b) * 1099511628211UL;
        return hash;
    }

    public void Dispose() => Stop();
}
