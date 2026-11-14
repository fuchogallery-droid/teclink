using TecLink.App.Native;

namespace TecLink.App.Input;

public enum MouseButton { Left, Right, Middle }

public sealed class HookMouseEventArgs : EventArgs
{
    public int X { get; init; }
    public int Y { get; init; }
    public MouseButton Button { get; init; }
    public bool IsDown { get; init; }
    public int WheelDelta { get; init; }
    public bool Suppress { get; set; }
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

    private readonly HashSet<int> _osDown = new();

    public bool Swallow { get; set; }

    public event Action<HookMouseEventArgs>? Move;
    public event Action<HookMouseEventArgs>? ButtonChanged;
    public event Action<HookMouseEventArgs>? Wheel;

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
            if (data.dwExtraInfo == Win32.InputMarker)
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            var msg = (int)wParam;

            switch (msg)
            {
                case Win32.WM_MOUSEMOVE:
                    var move = new HookMouseEventArgs { X = data.pt.x, Y = data.pt.y };
                    Move?.Invoke(move);
                    if (move.Suppress) return (IntPtr)1;
                    break;
                case Win32.WM_LBUTTONDOWN:
                    ButtonChanged?.Invoke(new HookMouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Left, IsDown = true });
                    break;
                case Win32.WM_LBUTTONUP:
                    ButtonChanged?.Invoke(new HookMouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Left, IsDown = false });
                    break;
                case Win32.WM_RBUTTONDOWN:
                    ButtonChanged?.Invoke(new HookMouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Right, IsDown = true });
                    break;
                case Win32.WM_RBUTTONUP:
                    ButtonChanged?.Invoke(new HookMouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Right, IsDown = false });
                    break;
                case Win32.WM_MBUTTONDOWN:
                    ButtonChanged?.Invoke(new HookMouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Middle, IsDown = true });
                    break;
                case Win32.WM_MBUTTONUP:
                    ButtonChanged?.Invoke(new HookMouseEventArgs { X = data.pt.x, Y = data.pt.y, Button = MouseButton.Middle, IsDown = false });
                    break;
                case Win32.WM_MOUSEWHEEL:
                    var delta = (short)((data.mouseData >> 16) & 0xFFFF);
                    Wheel?.Invoke(new HookMouseEventArgs { X = data.pt.x, Y = data.pt.y, WheelDelta = delta });
                    break;
            }

            // A button the OS saw go down must also see its release (drag that crosses to the
            // other PC), otherwise Windows keeps it stuck.
            var downKey = msg switch { Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP => 0, Win32.WM_RBUTTONDOWN or Win32.WM_RBUTTONUP => 1,
                Win32.WM_MBUTTONDOWN or Win32.WM_MBUTTONUP => 2, _ => -1 };
            if (downKey >= 0)
            {
                var isDown = msg is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN;
                if (isDown && !Swallow) _osDown.Add(downKey);
                else if (!isDown && _osDown.Remove(downKey))
                    return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
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
