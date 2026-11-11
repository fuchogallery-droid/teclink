using System.Runtime.InteropServices;
using TecLink.App.Native;

namespace TecLink.App;

/// <summary>
/// Hidden message-only window that listens for system clipboard changes via
/// WM_CLIPBOARDUPDATE. Used both to detect local copies to forward to the peer,
/// and to suppress the echo when we apply a clipboard update that came from the peer.
/// </summary>
public sealed class ClipboardWatcher : Form
{
    private bool _ignoreNextChange;

    public event Action<string>? TextCopied;

    public ClipboardWatcher()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0;
        Width = 0;
        Height = 0;
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false); // never actually show

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Win32.AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_CLIPBOARDUPDATE)
        {
            if (_ignoreNextChange)
            {
                _ignoreNextChange = false;
            }
            else if (Clipboard.ContainsText())
            {
                try
                {
                    var text = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text))
                        TextCopied?.Invoke(text);
                }
                catch (ExternalException)
                {
                    // clipboard was locked by another app at the exact moment we read it; skip this tick
                }
            }
        }

        base.WndProc(ref m);
    }

    public void ApplyRemoteText(string text)
    {
        _ignoreNextChange = true;
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            _ignoreNextChange = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Win32.RemoveClipboardFormatListener(Handle);
        base.Dispose(disposing);
    }
}
