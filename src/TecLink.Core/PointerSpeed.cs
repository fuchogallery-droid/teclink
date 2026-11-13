namespace TecLink.Core;

/// <summary>One speed for both PCs, on the Windows scale (1–20, 10 = normal). The same
/// level is written to Windows on each machine and used to scale the raw deltas that
/// drive the pointer while it is on the other PC, carrying the fractional remainder so
/// slow speeds still move and nothing is lost to rounding.</summary>
public sealed class PointerSpeed
{
    public const int Min = 1, Max = 20, Default = 10;
    // The multipliers Windows itself applies per level (pointer precision off).
    private static readonly double[] Factors = { .03125, .0625, .125, .25, .375, .5, .625, .75, .875, 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3, 3.25, 3.5 };
    private double _restX, _restY;

    public static int Clamp(int level) => level <= 0 ? Default : Math.Clamp(level, Min, Max);
    public static double Factor(int level) => Factors[Clamp(level) - 1];

    public void Reset() => _restX = _restY = 0;

    public (int X, int Y) Scale(int deltaX, int deltaY, int level)
    {
        var factor = Factor(level);
        _restX += deltaX * factor;
        _restY += deltaY * factor;
        var x = (int)Math.Truncate(_restX);
        var y = (int)Math.Truncate(_restY);
        _restX -= x;
        _restY -= y;
        return (x, y);
    }
}
