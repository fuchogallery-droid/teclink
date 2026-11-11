using TecLink.App.Native;

namespace TecLink.App.Input;

public sealed class KeyEventArgsLite : EventArgs
{
    public int VkCode { get; init; }
    public int ScanCode { get; init; }
    public bool IsDown { get; init; }
    public bool IsExtended { get; init; }
}

/// <summary>
/// System-wide low-level keyboard hook, same swallow-while-remote behavior as <see cref="MouseHook"/>.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const uint LLKHF_EXTENDED = 0x01;

    private readonly Win32.HookProc _proc;
    private IntPtr _hookHandle;

    public bool Swallow { get; set; }

    public event Action<KeyEventArgsLite>? KeyEvent;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        using var curModule = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        var hMod = Win32.GetModuleHandle(curModule.ModuleName);
        _hookHandle = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo instalar el hook de teclado (SetWindowsHookEx).");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var isDown = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            var isUp = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;

            if (isDown || isUp)
            {
                KeyEvent?.Invoke(new KeyEventArgsLite
                {
                    VkCode = (int)data.vkCode,
                    ScanCode = (int)data.scanCode,
                    IsDown = isDown,
                    IsExtended = (data.flags & LLKHF_EXTENDED) != 0,
                });
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
