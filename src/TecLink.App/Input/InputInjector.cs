using TecLink.App.Native;
using TecLink.Core.Protocol;
using TecLink.Core;
using System.Runtime.InteropServices;

namespace TecLink.App.Input;

/// <summary>
/// Applies input received from the peer to this machine (used while this machine is
/// the one being remote-controlled).
/// </summary>
public static class InputInjector
{
    private static long _lastFailureLog;

    public static bool MoveTo(int x, int y)
    {
        var desktop = SystemInformation.VirtualScreen;
        return Send(new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            u = new Win32.INPUT_UNION { mi = new Win32.MOUSEINPUT
            {
                dx = (int)((long)Math.Clamp(x - desktop.Left, 0, desktop.Width - 1) * 65535 / Math.Max(1, desktop.Width - 1)),
                dy = (int)((long)Math.Clamp(y - desktop.Top, 0, desktop.Height - 1) * 65535 / Math.Max(1, desktop.Height - 1)),
                dwFlags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK,
                dwExtraInfo = Win32.InputMarker,
            } },
        });
    }

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
            u = new Win32.INPUT_UNION { mi = new Win32.MOUSEINPUT { dwFlags = flags, dwExtraInfo = Win32.InputMarker } },
        };
        Send(input);
    }

    public static void Wheel(int delta)
    {
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            u = new Win32.INPUT_UNION { mi = new Win32.MOUSEINPUT { dwFlags = Win32.MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)delta), dwExtraInfo = Win32.InputMarker } },
        };
        Send(input);
    }

    public static bool Key(int vkCode, bool isDown, bool isExtended, int scanCode = 0)
    {
        var key = KeyboardStroke.Create(vkCode, scanCode, isDown, isExtended);
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            u = new Win32.INPUT_UNION { ki = new Win32.KEYBDINPUT
            { wVk = key.VirtualKey, wScan = key.ScanCode, dwFlags = key.Flags, dwExtraInfo = Win32.InputMarker } },
        };
        return Send(input);
    }

    private static bool Send(Win32.INPUT input)
    {
        if (Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>()) == 1) return true;
        var error = Marshal.GetLastWin32Error();
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastFailureLog);
        if (now - previous > 2000 && Interlocked.CompareExchange(ref _lastFailureLog, now, previous) == previous)
            Logger.LogError(new InvalidOperationException($"Windows rechazó entrada remota (SendInput, error {error}). Comprobar permisos y estado de la aplicación destino; UIPI puede bloquear sin código de error."));
        return false;
    }
}
