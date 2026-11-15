using TecLink.Core.Protocol;

namespace TecLink.App;

/// <summary>Everything TecLink does outside its own window, in one place, and only while somebody is connected:
/// the keyboard and mouse hooks, the raw mouse input, the arrow design and the optional clipboard sharing.
/// Idle (nobody connected, or switched off) TecLink touches nothing in Windows.</summary>
public sealed partial class EdgeBridge
{
    private bool _hooksOn;

    /// <summary>Automated tests turn this off so no real hook is ever installed on the machine running them.</summary>
    public bool SystemEffects { get; set; } = true;

    /// <summary>Somebody is connected right now (the panel keeps working in the background only then).</summary>
    public bool HasPeerConnected => HasLivePeer();

    private bool HasLivePeer() => SlotsSnapshot().Any(s => s.Known && s.Connection is not null);

    /// <summary>Re-evaluates what TecLink may touch (safe to call from any thread).</summary>
    public void RefreshSystemEffects()
    {
        if (_clipboard.IsDisposed) return;
        try { _clipboard.BeginInvoke(ApplySystemEffects); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private void ApplySystemEffects()
    {
        if (!SystemEffects) return;
        var live = Powered && HasLivePeer();
        if (live && !_hooksOn)
        {
            try
            {
                _rawMouse.Install();
                _mouseHook.Install();
                _keyboardHook.Install();
                _hooksOn = true;
                Logger.Info("Hay alguien conectado: se activaron el mouse y el teclado compartidos.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Logger.LogError(ex);
                ReleaseHooks();
            }
        }
        else if (!live && _hooksOn) ReleaseHooks();

        // The arrow design is a personal choice: it is worn only while TecLink is in use and is always put back.
        if (live) CursorStyler.Apply(_config.CursorStyle, _config.CursorSizePercent); else CursorStyler.Restore();
        // The clipboard is shared only if the person turned that on, and only while somebody is connected.
        _clipboard.Sharing = live && _config.ShareClipboard;
    }

    private void ReleaseHooks()
    {
        if (_hooksOn) Logger.Info("Nadie conectado: se retiraron todos los ganchos del mouse y el teclado.");
        _hooksOn = false;
        _mouseHook.Swallow = false; _keyboardHook.Swallow = false;
        _mouseHook.Dispose(); _keyboardHook.Dispose(); _rawMouse.Uninstall();
        _lastLocalPoint = null;
    }

    public void SetShareClipboard(bool on)
    {
        _config.ShareClipboard = on;
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        RefreshSystemEffects();
        StatusChanged?.Invoke(on ? "Compartir el portapapeles: activado (solo texto, mientras haya alguien conectado)." : "Compartir el portapapeles: apagado.");
    }
}
