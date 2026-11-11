using TecLink.App.Native;

namespace TecLink.App.Input;

public enum MouseButton { Left, Right, Middle }

public sealed class MouseEventArgs : EventArgs
{
    public int X { get; init; }
    public int Y { get; init; }
    public MouseButton Button { get; init; }
    public bool IsDown { get; init; }
    public int WheelDelta { get; init; }
}

/// <summary>
/// System-wide low-level mouse hook. When <see cref="Swallow"/> is true, events are
/// consumed before Windows moves the real cursor or delivers the click to any app —
/// this is what lets us read raw movement while the local cursor stays pinned at the
/// screen edge during remote control.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly Win32.HookProc _proc; // keep alive: SetWindowsHookEx does not pin delegates
    private IntPtr _hookHandle;

    public bool Swallow { get; set; }

    public event Action<MouseEventArgs>? Move;
    public event Action<MouseEventArgs>? ButtonChanged;
    public event Action<MouseEventArgs>? Wheel;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        using var curModule = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        var hMod = Win32.GetModuleHandle(curModule.ModuleName);
        _hookHandle = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, hMod, 0);
        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo instalar el hook de mouse (SetWindowsHookEx).");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;

            switch (msg)
            {
                case Win32.WM_MOUSEMOVE:
                    Move?.Invoke(new MouseEventArgs { X = data.pt.x, Y = data.pt.y });
                    break;
                case Win32.WM_LBUTTONDOWN:
                    ButtonChanged?.Invoke(new MouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Left, IsDown = true });
                    break;
                case Win32.WM_LBUTTONUP:
                    ButtonChanged?.Invoke(new MouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Left, IsDown = false });
                    break;
                case Win32.WM_RBUTTONDOWN:
                    ButtonChanged?.Invoke(new MouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Right, IsDown = true });
                    break;
                case Win32.WM_RBUTTONUP:
                    ButtonChanged?.Invoke(new MouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Right, IsDown = false });
                    break;
                case Win32.WM_MBUTTONDOWN:
                    ButtonChanged?.Invoke(new MouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Middle, IsDown = true });
                    break;
                case Win32.WM_MBUTTONUP:
                    ButtonChanged?.Invoke(new MouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Middle, IsDown = false });
                    break;
                case Win32.WM_MOUSEWHEEL:
                    var delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    Wheel?.Invoke(new MouseEventArgs { X = data.pt.x, Y = data.pt.y, WheelDelta = delta });
                    break;
            }

            if (Swallow)
                return (IntPtr)1;
        }

        return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
