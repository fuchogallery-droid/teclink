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
    private const short KEY_DOWN_MASK = unchecked((short)0x8000);

    private readonly Win32.HookProc _proc;
    private IntPtr _hookHandle;
    private bool _hotkeyActive; // true from the moment Ctrl+Alt+Space is recognized until Space is released

    private readonly HashSet<int> _osDown = new();

    public bool Swallow { get; set; }

    public event Action<KeyEventArgsLite>? KeyEvent;

    /// <summary>Fires once per physical press of the global toggle hotkey (Ctrl+Alt+Space).
    /// Consumed entirely at the hook level: never reaches local apps nor the generic
    /// <see cref="KeyEvent"/> (so it's never forwarded to the peer either).</summary>
    public event Action? HotkeyToggle;

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
            if (data.dwExtraInfo == Win32.InputMarker)
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            var msg = (int)wParam;
            var isDown = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            var isUp = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
            var vkCode = (int)data.vkCode;

            if (vkCode == Win32.VK_SPACE && isDown && !_hotkeyActive
                && (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & KEY_DOWN_MASK) != 0
                && (Win32.GetAsyncKeyState(Win32.VK_MENU) & KEY_DOWN_MASK) != 0)
            {
                _hotkeyActive = true;
                HotkeyToggle?.Invoke();
                return (IntPtr)1; // consumed unconditionally: never reaches local apps nor the peer
            }
            if (vkCode == Win32.VK_SPACE && _hotkeyActive)
            {
                if (isUp) _hotkeyActive = false;
                return (IntPtr)1; // repeat keydowns and the eventual key-up of the same combo press
            }

            if (isDown || isUp)
            {
                KeyEvent?.Invoke(new KeyEventArgsLite
                {
                    VkCode = vkCode,
                    ScanCode = (int)data.scanCode,
                    IsDown = isDown,
                    IsExtended = (data.flags & LLKHF_EXTENDED) != 0,
                });
            }

            // Keys the OS already saw go down must also see their key-up, even when we are
            // swallowing now (e.g. Ctrl+Alt held while the hotkey flips to Remote). Otherwise
            // Windows keeps Ctrl/Alt "stuck" and the whole PC misbehaves.
            if (isDown && !Swallow) _osDown.Add(vkCode);
            if (isUp && _osDown.Remove(vkCode))
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

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
