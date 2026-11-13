using System.ComponentModel;
using System.Runtime.InteropServices;
using TecLink.App.Native;

namespace TecLink.App.Input;

/// <summary>Hardware movement still arrives when the low-level hook blocks the cursor.</summary>
public sealed class RawMouseInput : NativeWindow, IDisposable
{
    private readonly Dictionary<IntPtr, Point> _absolutePositions = new();
    private readonly IntPtr _buffer = Marshal.AllocHGlobal(Marshal.SizeOf<Win32.RAWINPUTMOUSE>());
    private bool _installed;
    public event Action<int, int>? Move;

    public void Install()
    {
        CreateHandle(new CreateParams { Caption = "TecLink raw input", Parent = new IntPtr(-3) });
        var device = new Win32.RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = Win32.RIDEV_INPUTSINK, Target = Handle };
        if (!Win32.RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo registrar Raw Input para el ratón.");
        _installed = true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == Win32.WM_INPUT)
        {
            uint size = (uint)Marshal.SizeOf<Win32.RAWINPUTMOUSE>();
            var read = Win32.GetRawInputData(message.LParam, Win32.RID_INPUT, _buffer, ref size,
                (uint)Marshal.SizeOf<Win32.RAWINPUTHEADER>());
            if (read != uint.MaxValue && read >= Marshal.SizeOf<Win32.RAWINPUTMOUSE>())
            {
                var input = Marshal.PtrToStructure<Win32.RAWINPUTMOUSE>(_buffer);
                if (input.Header.Type == 0 && input.Mouse.ExtraInformation != (uint)Win32.InputMarker.ToInt64())
                {
                    var dx = input.Mouse.LastX;
                    var dy = input.Mouse.LastY;
                    if ((input.Mouse.Flags & 1) != 0)
                    {
                        var bounds = (input.Mouse.Flags & 2) != 0
                            ? SystemInformation.VirtualScreen : Screen.PrimaryScreen!.Bounds;
                        var point = new Point(bounds.Left + (int)((long)dx * (bounds.Width - 1) / 65535),
                            bounds.Top + (int)((long)dy * (bounds.Height - 1) / 65535));
                        var hadPrevious = _absolutePositions.TryGetValue(input.Header.Device, out var previous);
                        _absolutePositions[input.Header.Device] = point;
                        dx = hadPrevious ? point.X - previous.X : 0;
                        dy = hadPrevious ? point.Y - previous.Y : 0;
                    }
                    if (dx != 0 || dy != 0) Move?.Invoke(dx, dy);
                }
            }
        }
        // DefWindowProc must also see foreground WM_INPUT for native cleanup.
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_installed)
        {
            var device = new Win32.RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = Win32.RIDEV_REMOVE };
            Win32.RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>());
        }
        DestroyHandle();
        Marshal.FreeHGlobal(_buffer);
    }
}
