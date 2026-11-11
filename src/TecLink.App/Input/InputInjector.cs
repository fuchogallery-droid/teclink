using TecLink.App.Native;
using TecLink.Core.Protocol;

namespace TecLink.App.Input;

/// <summary>
/// Applies input received from the peer to this machine (used while this machine is
/// the one being remote-controlled).
/// </summary>
public static class InputInjector
{
    public static void MoveTo(int x, int y) => Win32.SetCursorPos(x, y);

    public static void Button(MouseButtonKind button, bool isDown)
    {
        var flags = button switch
        {
            MouseButtonKind.Left => isDown ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP,
            MouseButtonKind.Right => isDown ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP,
            MouseButtonKind.Middle => isDown ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP,
            _ => 0u,
        };

        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            u = new Win32.INPUT_UNION { mi = new Win32.MOUSEINPUT { dwFlags = flags } },
        };
        Win32.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>());
    }

    public static void Wheel(int delta)
    {
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            u = new Win32.INPUT_UNION { mi = new Win32.MOUSEINPUT { dwFlags = Win32.MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)delta) } },
        };
        Win32.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>());
    }

    public static void Key(int vkCode, bool isDown, bool isExtended)
    {
        var flags = (isDown ? 0u : Win32.KEYEVENTF_KEYUP) | (isExtended ? Win32.KEYEVENTF_EXTENDEDKEY : 0u);
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            u = new Win32.INPUT_UNION { ki = new Win32.KEYBDINPUT { wVk = (ushort)vkCode, dwFlags = flags } },
        };
        Win32.SendInput(1, new[] { input }, System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>());
    }
}
