using System.Drawing;

namespace TecLink.Core;

/// <summary>Accumulates raw hardware deltas independently of the local cursor.</summary>
public sealed class RemotePointer
{
    private readonly Size _size;
    public Point Position { get; private set; }

    public RemotePointer(Size size, Point entry)
    {
        if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        _size = size;
        Position = new Point(Math.Clamp(entry.X, 0, size.Width - 1), Math.Clamp(entry.Y, 0, size.Height - 1));
    }

    // X may cross an edge so the caller can hand control back. Stored coordinates
    // remain bounded; sitting at the top/bottom never accumulates invisible movement.
    public Point Advance(int deltaX, int deltaY)
    {
        var x = (long)Position.X + deltaX;
        var y = Math.Clamp((long)Position.Y + deltaY, 0, _size.Height - 1);
        Position = new Point((int)Math.Clamp(x, 0, _size.Width - 1), (int)y);
        return new Point((int)Math.Clamp(x, int.MinValue, int.MaxValue), (int)y);
    }
}
