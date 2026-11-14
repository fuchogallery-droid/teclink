using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TecLink.App.Native;

namespace TecLink.App;

/// <summary>Replaces the Windows arrow pointer with a TecLink design (color + size).
/// "system" puts the user's own Windows pointers back.</summary>
internal static class CursorStyler
{
    public static readonly string[] Styles = { "system", "white", "black", "green", "red" };
    private static bool _custom;

    private static readonly System.Text.RegularExpressions.Regex HexColor = new("^#[0-9a-fA-F]{6}$");

    /// <summary>A preset name, or a free color as "#RRGGBB".</summary>
    public static string Normalize(string? style) =>
        Styles.Contains(style) ? style! : style is not null && HexColor.IsMatch(style) ? style.ToLowerInvariant() : "system";
    public static int ClampSize(int percent) => percent <= 0 ? 100 : Math.Clamp(percent, 100, 300);

    public static void Apply(string? style, int sizePercent)
    {
        style = Normalize(style);
        if (style == "system") { Restore(); return; }
        var (fill, edge) = style switch
        {
            "white" => (Color.White, Color.Black),
            "black" => (Color.FromArgb(17, 17, 17), Color.White),
            "green" => (Color.FromArgb(0, 255, 102), Color.Black),
            "red" => (Color.FromArgb(255, 59, 59), Color.White),
            _ => FreeColor(style),
        };
        try
        {
            var scale = Win32.GetDpiForSystem() / 96f * ClampSize(sizePercent) / 100f;
            var side = (int)Math.Ceiling(32 * scale);
            using var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var unit = 26f * scale;
                var arrow = new[] { (0f, 0f), (0f, .74f), (.17f, .58f), (.29f, .86f), (.40f, .81f), (.28f, .54f), (.51f, .54f) }
                    .Select(p => new PointF(1.5f * scale + p.Item1 * unit, 1.5f * scale + p.Item2 * unit)).ToArray();
                using var brush = new SolidBrush(fill);
                using var pen = new Pen(edge, Math.Max(1.2f, 1.4f * scale)) { LineJoin = LineJoin.Round };
                g.FillPolygon(brush, arrow);
                g.DrawPolygon(pen, arrow);
            }
            var icon = bitmap.GetHicon();
            try
            {
                if (!Win32.GetIconInfo(icon, out var info)) return;
                info.fIcon = false;
                info.xHotspot = info.yHotspot = (int)Math.Round(1.5f * scale);
                var cursor = Win32.CreateIconIndirect(ref info);
                Win32.DeleteObject(info.hbmColor);
                Win32.DeleteObject(info.hbmMask);
                // SetSystemCursor takes ownership of the handle.
                if (cursor != IntPtr.Zero && Win32.SetSystemCursor(cursor, Win32.OCR_NORMAL)) _custom = true;
            }
            finally { Win32.DestroyIcon(icon); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        { Logger.LogError(ex); }
    }

    private static (Color fill, Color edge) FreeColor(string hex)
    {
        var fill = Color.FromArgb(Convert.ToInt32(hex[1..], 16) | unchecked((int)0xFF000000));
        var luminance = 0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B;
        return (fill, luminance > 140 ? Color.Black : Color.White);
    }

    /// <summary>Reloads the pointers configured in Windows.</summary>
    public static void Restore()
    {
        if (!_custom) return;
        Win32.SystemParametersInfo(Win32.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        _custom = false;
    }
}
