namespace TecLink.Core;

public readonly record struct KeyboardStroke(ushort VirtualKey, ushort ScanCode, uint Flags)
{
    public static KeyboardStroke Create(int virtualKey, int scanCode, bool down, bool extended)
    {
        var physical = scanCode is > 0 and <= ushort.MaxValue;
        return new KeyboardStroke(physical ? (ushort)0 : (ushort)virtualKey,
            physical ? (ushort)scanCode : (ushort)0,
            (down ? 0u : 0x0002u) | (extended ? 0x0001u : 0u) | (physical ? 0x0008u : 0u));
    }
}
