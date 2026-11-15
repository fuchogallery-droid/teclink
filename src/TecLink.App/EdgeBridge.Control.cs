using TecLink.Core.Protocol;

namespace TecLink.App;

/// <summary>Two ways to use TecLink. "Mis computadoras": every PC is mine, so the mouse and the keyboard cross between them by themselves.
/// "Con otras personas": a room of communication and video; nobody can move another person's mouse unless that person says yes, from the
/// "Controlar PC" button, and they can stop it whenever they want.</summary>
public sealed partial class EdgeBridge
{
    public const string RoomOwn = "own", RoomPeople = "people";

    private string _hostRoomMode = RoomOwn;        // guest: what the owner of the room told (an older owner says nothing: it is "own")
    private PeerSlot? _grantedSlot;                // this PC lets that guest control it
    private PeerSlot? _askerSlot; private string? _askerName;
    private volatile bool _iControl, _controlPending;

    /// <summary>The owner asks to control this PC / the other PC answered "yes" (the panel opens the remote window).</summary>
    public event Action<string>? ControlAsked;
    public event Action? ControlGranted;

    private static string NormalizeMode(string? mode) => mode == RoomOwn ? RoomOwn : RoomPeople;
    public string RoomMode => _config.Role == PeerRole.Server ? NormalizeMode(_config.RoomMode) : _hostRoomMode;
    /// <summary>All the PCs are the same person's (or an old direct link): they cross and control each other freely.</summary>
    public bool OwnRoom => !_sessionMode || RoomMode == RoomOwn;
    public bool IControl => _iControl;

    /// <summary>May the other PC move this one's mouse and press its keys right now?</summary>
    private bool ControlAllowedFrom(PeerSlot slot) => OwnRoom ? ManagementAllowed : ReferenceEquals(_grantedSlot, slot);

    public object ControlView() => new
    {
        mode = RoomMode, own = OwnRoom, pending = _controlPending, iControl = _iControl,
        asking = _askerName, controlledBy = _grantedSlot?.Name,
    };

    /// <summary>The "Controlar PC" button.</summary>
    public void RequestControl()
    {
        if (_connection is not { } conn || !_remoteKnown || !CanUseExtras) { Notice?.Invoke("Conecta primero con otra PC."); return; }
        if (OwnRoom)
        {
            if (!ManagementAllowed) { Notice?.Invoke("La gestión de pantallas está apagada: el anfitrión debe activarla."); return; }
            ControlGranted?.Invoke(); // my own computers: no permission needed
            return;
        }
        _controlPending = true;
        _ = conn.SendAsync(WireMessage.ControlMsg("ask"));
        Notice?.Invoke($"Pidiendo permiso a {_remoteName} para controlar su PC…");
        RoomChanged?.Invoke();
    }

    /// <summary>The window that shows the other PC was closed: with another person's PC that is the end of the permission.</summary>
    public void ViewerClosedByUser() { if (!OwnRoom && _iControl) EndControl(); }

    /// <summary>The person being asked answers.</summary>
    public void AnswerControl(bool accept)
    {
        var slot = _askerSlot; _askerSlot = null; _askerName = null;
        if (slot?.Connection is not { } conn) { RoomChanged?.Invoke(); return; }
        if (accept)
        {
            _grantedSlot = slot;
            _ = conn.SendAsync(WireMessage.ControlMsg("ok"));
            StatusChanged?.Invoke($"{slot.Name} está controlando tu PC. Puedes detenerlo cuando quieras.");
        }
        else _ = conn.SendAsync(WireMessage.ControlMsg("no"));
        RoomChanged?.Invoke();
    }

    /// <summary>Either side ends the control: the one who controls lets go, the one who is controlled takes it back.</summary>
    public void EndControl()
    {
        var granted = _grantedSlot; _grantedSlot = null;
        if (granted?.Connection is { } gc) _ = gc.SendAsync(WireMessage.ControlMsg("end"));
        if (_iControl)
        {
            _iControl = false;
            if (_connection is { } conn) _ = conn.SendAsync(WireMessage.ControlMsg("end"));
        }
        _controlPending = false; _askerSlot = null; _askerName = null;
        _clipboard.BeginInvoke(() =>
        {
            if (_viewInput) SetViewInput(false);
            SwitchToLocal(force: true); ReleaseAllInjectedInput();
            ViewerCloseRequested?.Invoke();
            RoomChanged?.Invoke();
        });
    }

    /// <summary>Messages about control, from the active peer.</summary>
    private void HandleControl(PeerSlot slot, string? text)
    {
        var verb = text ?? "";
        if (verb.StartsWith("mode:", StringComparison.Ordinal))
        {
            if (_config.Role == PeerRole.Server) return; // only the owner decides the mode
            var next = NormalizeMode(verb[5..]);
            if (next != _hostRoomMode) { _hostRoomMode = next; EndControl(); StatusChanged?.Invoke(next == RoomOwn ? "La sala ahora es de «Mis computadoras»." : "La sala ahora es «Con otras personas»: controlar una PC pide permiso."); }
            RoomChanged?.Invoke();
            return;
        }
        switch (verb)
        {
            case "ask":
                if (OwnRoom) { if (slot.Connection is { } c0) _ = c0.SendAsync(WireMessage.ControlMsg("ok")); _grantedSlot = slot; break; } // mine: no need to ask
                if (_grantedSlot is not null || _askerSlot is not null) break;
                _askerSlot = slot; _askerName = slot.Name;
                ControlAsked?.Invoke(slot.Name);
                ControlChanged?.Invoke($"{slot.Name} pide controlar tu PC");
                RoomChanged?.Invoke();
                break;
            case "ok":
                if (!_controlPending && !OwnRoom) break;
                _controlPending = false; _iControl = true;
                ControlGranted?.Invoke();
                RoomChanged?.Invoke();
                break;
            case "no":
                _controlPending = false;
                Notice?.Invoke($"{slot.Name} no permitió el control de su PC.");
                RoomChanged?.Invoke();
                break;
            case "end":
                if (ReferenceEquals(_grantedSlot, slot))
                {
                    _grantedSlot = null; SwitchToLocal(force: true); ReleaseAllInjectedInput();
                    StatusChanged?.Invoke($"{slot.Name} dejó de controlar tu PC.");
                }
                if (_iControl)
                {
                    _iControl = false; _controlPending = false;
                    if (_viewInput) SetViewInput(false);
                    ViewerCloseRequested?.Invoke();
                    Notice?.Invoke($"{slot.Name} terminó el control de su PC.");
                }
                RoomChanged?.Invoke();
                break;
        }
    }

    /// <summary>Owner: choose how this room works. Everybody in it is told, and any control in progress ends.</summary>
    public void SetRoomMode(string mode)
    {
        if (_config.Role != PeerRole.Server) return;
        _config.RoomMode = NormalizeMode(mode);
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        foreach (var s in Joined()) _ = s.Connection!.SendAsync(WireMessage.ControlMsg("mode:" + RoomMode));
        EndControl();
        StatusChanged?.Invoke(RoomMode == RoomOwn ? "Sala de «Mis computadoras»: el mouse y el teclado cruzan solos." : "Sala «Con otras personas»: controlar una PC pide permiso.");
    }

    /// <summary>A guest left (or the link broke): whoever was controlling or being controlled through it is released.</summary>
    private void ResetControl(PeerSlot slot)
    {
        if (ReferenceEquals(_grantedSlot, slot)) _grantedSlot = null;
        if (ReferenceEquals(_askerSlot, slot)) { _askerSlot = null; _askerName = null; }
        if (ReferenceEquals(_active, slot)) { _iControl = false; _controlPending = false; _hostRoomMode = RoomOwn; }
    }
}
