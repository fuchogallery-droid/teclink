using System.Text.Json;
using System.Threading.Channels;
using TecLink.Core;
using TecLink.Core.Networking;
using TecLink.Core.Protocol;

namespace TecLink.App;

/// <summary>A room holds up to 10 PCs: the owner (host) plus up to 9 guests, each connected only to the owner (star).
/// Everyone sees each other in the room, chats, speaks and shows their camera; the owner's mouse and screens work with ONE guest at a
/// time (the "active peer"), which the owner can change whenever they want. The code that moves the mouse keeps working with the
/// active peer exactly as it always did: its state lives in a <see cref="PeerSlot"/> and the old fields are views of it.</summary>
public sealed partial class EdgeBridge
{
    public const int MaxPeople = 10;
    /// <summary>The room changed (somebody came, left, changed their profile or became the mouse peer): the panel redraws.</summary>
    public event Action? RoomChanged;
    private const int MaxGuests = MaxPeople - 1;

    private sealed record JoinRequest(string Id, string Name, long At);

    private sealed class PeerSlot
    {
        public string Id = Guid.NewGuid().ToString("N")[..8];
        public string Address = "";
        public PeerConnection? Connection;
        public bool Joined, Known, Extras, MapSupported;
        public int Width, Height;
        public string Name = "Otra PC", Pc = "", Status = "none";
        public string? ProfileName, Avatar;
        public DisplayInfo[] Displays = Array.Empty<DisplayInfo>();
        public JoinRequest? Request;
        public Action? Drop;
    }

    /// <summary>The slot of the message being handled on this thread (so a message from a secondary guest never touches the active peer).</summary>
    [ThreadStatic] private static PeerSlot? _rx;
    private PeerSlot? _active;
    private readonly List<PeerSlot> _slots = new();
    private readonly object _slotLock = new();
    private PeerSlot? Cur => _rx ?? _active;

    private PeerSlot[] SlotsSnapshot() { lock (_slotLock) return _slots.ToArray(); }
    private PeerSlot[] Joined() { lock (_slotLock) return _slots.Where(s => s.Joined && s.Connection is not null).ToArray(); }

    // ---- The old single-peer fields, now views of the current slot ----
    private PeerConnection? _connection => Cur?.Connection;
    private int _remoteWidth { get => Cur?.Width ?? 0; set { if (Cur is { } c) c.Width = value; } }
    private int _remoteHeight { get => Cur?.Height ?? 0; set { if (Cur is { } c) c.Height = value; } }
    private bool _remoteKnown { get => Cur?.Known ?? false; set { if (Cur is { } c) c.Known = value; } }
    private bool _peerExtras { get => Cur?.Extras ?? false; set { if (Cur is { } c) c.Extras = value; } }
    private bool _peerMapSupported { get => Cur?.MapSupported ?? false; set { if (Cur is { } c) c.MapSupported = value; } }
    private bool _joined { get => Cur?.Joined ?? false; set { if (Cur is { } c) c.Joined = value; } }
    private string _remoteName { get => Cur?.Name ?? "Otra PC"; set { if (Cur is { } c) c.Name = value; } }
    private string _peerStatus { get => Cur?.Status ?? "none"; set { if (Cur is { } c) c.Status = value; } }
    private string? _remoteProfileName { get => Cur?.ProfileName; set { if (Cur is { } c) c.ProfileName = value; } }
    private string? _remotePc { get => Cur?.Pc; set { if (Cur is { } c) c.Pc = value ?? ""; } }
    private string? _remoteAvatar { get => Cur?.Avatar; set { if (Cur is { } c) c.Avatar = value; } }
    private DisplayInfo[] _remoteDisplays { get => Cur?.Displays ?? Array.Empty<DisplayInfo>(); set { if (Cur is { } c) c.Displays = value; } }
    private Action? _dropCurrent => Cur?.Drop;

    // ---- Host: any number of guests at once ----
    private async Task RunHostAsync(CancellationToken ct)
    {
        _ = SessionDiscovery.RunResponderAsync(() => SessionDiscovery.Normalize(_config.SessionCode) ?? "", _config.Port, ct,
            ex => { Logger.LogError(ex); StatusChanged?.Invoke("Otra PC no podrá encontrarte: " + ex.Message); });
        _joinRefused = false;
        HostAdmission? admission = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (Joined().Length == 0) StatusChanged?.Invoke($"Sesión abierta — código {SessionDiscovery.Pretty(_config.SessionCode)}");
                    if (admission is null) { admission = CreateAdmission(ct); _hostAdmission = admission; _ = StartOnlineRoomAsync(ct); }
                    var guest = await admission.Guests.ReadAsync(ct);
                    _ = RunSlotAsync(guest, ct);   // each guest lives in its own task; the loop goes back to wait for the next one
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    if (admission is not null) { try { await admission.DisposeAsync(); } catch (Exception) { } admission = null; }
                    StatusChanged?.Invoke($"Error de red: {ex.Message}. Reintentando en 3s...");
                    await Task.Delay(3000, ct);
                }
            }
        }
        finally { if (admission is not null) await admission.DisposeAsync(); }
    }

    private async Task RunSlotAsync(AdmittedConnection guest, CancellationToken ct)
    {
        var connection = guest.Connection;
        var slot = new PeerSlot { Connection = connection, Address = guest.Address };
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        slot.Drop = () => gone.TrySetResult();
        connection.Disconnected += () => gone.TrySetResult();
        lock (_slotLock) _slots.Add(slot);
        guest.Attach(msg => OnMessageReceived(slot, msg)); // replays the code and the Hello that came right behind it
        if (connection.IsDisconnected) gone.TrySetResult();
        try { await Task.WhenAny(gone.Task, Task.Delay(Timeout.Infinite, ct)); } catch (OperationCanceledException) { }
        await RemoveSlotAsync(slot, ct.IsCancellationRequested);
    }

    private async Task RemoveSlotAsync(PeerSlot slot, bool shuttingDown)
    {
        var connection = slot.Connection;
        lock (_slotLock) _slots.Remove(slot);
        slot.Request = null; ResetControl(slot);
        if (ReferenceEquals(_active, slot))
        {
            // The active peer left: the same sequence as always (the PC stays listed as "disconnected"), then the next guest, if any, takes its place.
            if (slot.Status == "connected") slot.Status = "lost";
            _sessionMode = Joined().Length > 0;
            slot.Connection = null; slot.Known = false; slot.Extras = false; slot.Drop = null;
            _peerControlling = false;
            _screenHost.Stop(); _tilesForPeer.Stop();
            Files.Abort();
            StatusChanged?.Invoke(shuttingDown ? "TecLink apagado" : slot.Status == "left" ? $"{slot.Name} salió de la sesión." : slot.Status == "lost" ? $"{slot.Name} se desconectó." : "Conexión perdida.");
            _clipboard.BeginInvoke(() =>
            {
                _viewInput = false;
                SwitchToLocal(force: true);
                ReleaseAllInjectedInput();
                ViewerCloseRequested?.Invoke();
                if (!shuttingDown && Joined().FirstOrDefault(s => s.Known) is { } next) SwitchActive(next);
                BroadcastRoster();
                ApplySystemEffects();
            });
        }
        else if (!shuttingDown)
        {
            if (slot.Joined) { AddEvent($"{slot.Name} salió de la sala."); StatusChanged?.Invoke($"{slot.Name} salió de la sala."); }
            BroadcastRoster();
            RefreshSystemEffects();
        }
        if (connection is not null) { try { await connection.DisposeAsync(); } catch (Exception) { } }
    }

    private void RemoveDuplicates(PeerSlot slot)
    {
        // The same PC coming back (its old link died without saying goodbye) takes its place instead of using a second seat.
        foreach (var old in SlotsSnapshot().Where(s => !ReferenceEquals(s, slot) && s.Pc.Length > 0 && s.Pc == slot.Pc && s.Address == slot.Address))
        {
            if (ReferenceEquals(_active, old)) { lock (_slotLock) _active = slot; slot.Name = old.Name; slot.Displays = slot.Displays.Length > 0 ? slot.Displays : old.Displays; }
            if (old.Connection is { } c) _ = c.SendAsync(WireMessage.JoinResult(false, "Otra PC entró con el código y tomó la sesión.")).WaitAsync(TimeSpan.FromMilliseconds(600)).ContinueWith(_ => old.Drop?.Invoke());
            else old.Drop?.Invoke();
        }
    }

    private void AcceptJoin(PeerSlot slot)
    {
        if (slot.Connection is not { } conn) return;
        slot.Joined = true; _sessionMode = true;
        bool active;
        lock (_slotLock)
        {
            active = _active is null || _active.Connection is null;
            if (active)
            {
                var old = _active;
                if (old is not null) { slot.Name = old.Name; slot.Displays = old.Displays; slot.Width = old.Width; slot.Height = old.Height; slot.Status = old.Status; }
                _active = slot;
            }
        }
        _ = conn.SendAsync(WireMessage.JoinResult(true));
        _ = conn.SendAsync(WireMessage.Management(active && _management));
        // La sala se acomoda sola: una PC que el dueno ya marco como suya entra en «Mis computadoras»; cualquier otra, en «Con otras personas».
        if (_config.Role == PeerRole.Server && !Joined().Any(s => !ReferenceEquals(s, slot))) { var want = IsOwnPc(slot.Pc) ? RoomOwn : RoomPeople; if (NormalizeMode(_config.RoomMode) != want) { _config.RoomMode = want; Save(); } } // con otros invitados ya dentro el modo no cambia para todos
        _ = conn.SendAsync(WireMessage.ControlMsg("mode:" + RoomMode));
        _ = SendDesktopAsync(conn);
        StatusChanged?.Invoke("Invitado conectado — enviando identificación...");
    }

    private bool RoomFull() => SlotsSnapshot().Count(s => s.Joined || s.Request is not null) >= MaxGuests;

    /// <summary>Owner only: the mouse and the screens now work with this guest (the previous one keeps chatting and showing its camera).</summary>
    public void UseMouseWith(string id)
    {
        if (_config.Role != PeerRole.Server) return;
        var target = Joined().FirstOrDefault(s => s.Id == id && s.Known);
        if (target is null || ReferenceEquals(target, _active)) return;
        _clipboard.BeginInvoke(() => SwitchActive(target));
    }

    // ---- Several guests, one map each: the host's mouse crosses to the guest whose side it leaves by ----
    private static string GuestKey(PeerSlot s) => s.Pc.Length > 0 ? s.Pc : s.Name;

    /// <summary>Remembers where this guest's screens are in the map (called before another guest becomes the active one, and when the map is saved).</summary>
    private void SaveGuestMap(PeerSlot guest)
    {
        var mine = _config.ScreenPositions.Where(p => p.Remote && guest.Displays.Any(d => d.Id == p.Id)).ToList();
        if (mine.Count > 0) _config.GuestMaps[GuestKey(guest)] = mine;
    }

    /// <summary>This PC's placements (as they are now) plus the guest's: its saved place, or glued to the first side nobody else uses.</summary>
    private List<ScreenPlacement> GuestPlacements(PeerSlot guest)
    {
        var local = LocalDisplays();
        var mine = _config.ScreenPositions.Where(p => !p.Remote && local.Any(d => d.Id == p.Id)).GroupBy(p => p.Id).Select(g => g.First()).ToList();
        if (mine.Count == local.Length && _config.GuestMaps.TryGetValue(GuestKey(guest), out var saved)
            && guest.Displays.Length > 0 && guest.Displays.All(d => saved.Any(p => p.Id == d.Id)))
        {
            var combined = mine.Concat(saved.Where(p => guest.Displays.Any(d => d.Id == p.Id))).ToList();
            try { _ = SpatialMap.Build(local, guest.Displays, combined); return combined; } catch (ArgumentException) { /* the guest's screens changed: place it again */ }
        }
        return guest.Displays.Length == 0 ? mine : SpatialMap.PlaceBeside(local, guest.Displays, FreeSideFor(guest));
    }

    /// <summary>The first side (right, left, top, bottom) of this PC not already taken by another guest's saved place.</summary>
    private ScreenEdge FreeSideFor(PeerSlot guest)
    {
        var local = LocalDisplays();
        var localPlaced = local.Select(d => new ScreenPlacement(d.Id, false, d.Left, d.Top)).ToList();
        var taken = new HashSet<ScreenEdge>();
        foreach (var other in Joined().Where(s => !ReferenceEquals(s, guest) && s.Displays.Length > 0))
            if (_config.GuestMaps.TryGetValue(GuestKey(other), out var saved) && SpatialMap.SideOf(local, other.Displays, localPlaced.Concat(saved).ToList()) is { } side)
                taken.Add(side);
        foreach (var side in new[] { ScreenEdge.Right, ScreenEdge.Left, ScreenEdge.Top, ScreenEdge.Bottom })
            if (!taken.Contains(side)) return side;
        return ScreenEdge.Right;
    }

    /// <summary>The routes from this PC's screens to another guest's, as they were arranged for it (null if that map is not valid).</summary>
    private List<ScreenRoute>? RoutesToGuest(PeerSlot guest)
    {
        try
        {
            var local = LocalDisplays();
            return SpatialMap.Build(local, guest.Displays, GuestPlacements(guest));
        }
        catch (ArgumentException) { return null; }
    }

    private void SwitchActive(PeerSlot target)
    {
        if (target.Connection is not { } conn || !target.Known) return;
        var old = _active;
        if (_viewInput) { _viewInput = false; _keyboardHook.Swallow = false; ReleaseAllRemoteHeldInput(); }
        SwitchToLocal(force: true); ReleaseAllInjectedInput();
        _peerControlling = false;
        _screenHost.Stop(); _tilesForPeer.Stop(); Files.Abort();
        ViewerCloseRequested?.Invoke();
        // The previous guest must not be left with a mouse that wants to cross into a PC that is no longer wired to it.
        if (old is { Connection: { } oc } && !ReferenceEquals(old, target)) { _ = oc.SendAsync(WireMessage.Management(false)); _ = oc.SendAsync(WireMessage.Yield()); }
        // Each guest keeps its own place in the map: the previous one is remembered, the new one gets its saved place
        // (or a free side of this PC), so the mouse can later cross to either of them by its edge.
        if (old is not null && !ReferenceEquals(old, target)) SaveGuestMap(old);
        lock (_slotLock) _active = target;
        _config.ScreenPositions = GuestPlacements(target);
        try { _config.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.LogError(ex); }
        _spatialValid = false; _spatialLocal = new(); _spatialRemote = new(); _activeRoute = null; _lastLocalPoint = null;
        if (LocalDisplays().Length + target.Displays.Length > MaxScreens)
        {
            Notice?.Invoke($"Límite de {MaxScreens} pantallas: {target.Name} trae {target.Displays.Length} y esta PC {LocalDisplays().Length}.");
        }
        _ = conn.SendAsync(WireMessage.Management(_management));
        _rx = target;
        try
        {
            AdoptOwnerMap();
            if (target.MapSupported && _config.UseSpatialMap && _spatialValid)
                _ = conn.SendAsync(WireMessage.Map(Guid.NewGuid().ToString("N"), _config.ScreenPositions.ToArray()));
        }
        finally { _rx = null; }
        AddEvent($"El mouse ahora trabaja con {target.Name}.");
        StatusChanged?.Invoke($"El mouse y las pantallas ahora trabajan con {target.Name}.");
        BroadcastRoster();
    }

    /// <summary>Owner only: send a guest out of the room.</summary>
    public void KickGuest(string id)
    {
        if (_config.Role != PeerRole.Server) return;
        var target = SlotsSnapshot().FirstOrDefault(s => s.Id == id);
        if (target?.Connection is not { } conn) return;
        AddEvent($"Sacaste a {target.Name} de la sala.");
        _ = conn.SendAsync(WireMessage.JoinResult(false, "El dueño de la sala te sacó.")).WaitAsync(TimeSpan.FromMilliseconds(600)).ContinueWith(_ => target.Drop?.Invoke());
    }

    // ---- Who is in the room ----
    private string? _youId;
    private List<RosterMember> _roster = new();
    private sealed record RosterMember(string Id, string Name, string Pc, string Avatar, bool Host, bool Mouse, string[] Screens);

    private static string[] ScreenList(IEnumerable<DisplayInfo> displays) => displays.Select(d => $"{d.Width}×{d.Height}").ToArray();

    private List<RosterMember> BuildRoster()
    {
        var list = new List<RosterMember> { new("host", OwnName, _config.MachineName, _config.Avatar ?? "", true, false, ScreenList(LocalDisplays())) };
        foreach (var s in Joined().Where(s => s.Known || s.Name != "Otra PC"))
            list.Add(new RosterMember(s.Id, s.Name, s.Pc, s.Avatar ?? "", false, ReferenceEquals(s, _active), ScreenList(s.Displays)));
        return list;
    }

    /// <summary>Tells every guest who is in the room (each one also learns its own id).</summary>
    private void BroadcastRoster()
    {
        if (_config.Role != PeerRole.Server) return;
        var members = BuildRoster();
        foreach (var s in Joined())
            _ = s.Connection!.SendAsync(WireMessage.RosterMsg(JsonSerializer.Serialize(new { you = s.Id, members }, PeerFilesJson)));
        RoomChanged?.Invoke();
    }

    private void HandleRoster(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            _youId = doc.RootElement.TryGetProperty("you", out var y) ? y.GetString() : null;
            var list = new List<RosterMember>();
            if (doc.RootElement.TryGetProperty("members", out var members))
                foreach (var m in members.EnumerateArray())
                    list.Add(new RosterMember(m.GetProperty("id").GetString() ?? "", Clean(m.GetProperty("name").GetString(), 32), Clean(m.GetProperty("pc").GetString(), 32),
                        m.TryGetProperty("avatar", out var a) && a.GetString() is { } av && av.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) && av.Length <= 60_000 ? av : "",
                        m.TryGetProperty("host", out var h) && h.GetBoolean(), m.TryGetProperty("mouse", out var mo) && mo.GetBoolean(),
                        m.TryGetProperty("screens", out var sc) ? sc.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>()));
            _roster = list;
            RoomChanged?.Invoke();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    /// <summary>Everyone in the room, as the panel shows them (the owner builds it, a guest receives it).</summary>
    public object MembersView()
    {
        if (_config.Role == PeerRole.Server || _roster.Count == 0)
        {
            var self = new { id = "me", name = OwnName, pc = _config.MachineName, avatar = _config.Avatar ?? "", host = _config.Role == PeerRole.Server, me = true, mouse = false, connected = true, screens = ScreenList(LocalDisplays()) };
            var others = _config.Role == PeerRole.Server
                ? Joined().Select(s => (object)new { id = s.Id, name = s.Name, pc = s.Pc, avatar = s.Avatar ?? "", host = false, me = false, mouse = ReferenceEquals(s, _active), connected = s.Known, screens = ScreenList(s.Displays) }).ToArray()
                : Array.Empty<object>();
            return new[] { (object)self }.Concat(others).ToArray();
        }
        return _roster.Select(m => new
        {
            id = m.Id == _youId ? "me" : m.Id, name = m.Id == _youId ? OwnName : m.Name, pc = m.Id == _youId ? _config.MachineName : m.Pc,
            avatar = m.Id == _youId ? _config.Avatar ?? "" : m.Avatar, host = m.Host, me = m.Id == _youId, mouse = m.Mouse, connected = true,
            screens = m.Id == _youId ? ScreenList(LocalDisplays()) : m.Screens,
        }).ToArray();
    }

    private string NameOf(string senderId)
    {
        if (senderId == "host") return _roster.FirstOrDefault(m => m.Host)?.Name ?? _remoteName;
        return _roster.FirstOrDefault(m => m.Id == senderId)?.Name ?? "Otra PC";
    }

    // ---- Chat, voice and camera: from any guest to everyone ----
    private void RouteChat(PeerSlot from, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var clean = text!.Length > 2000 ? text[..2000] : text;
        ChatReceived?.Invoke(from.Name, clean);
        RelayToOthers(from, WireMessage.ChatText(clean));
    }

    private void RouteVoice(PeerSlot from, byte[] pcm)
    {
        VoiceReceived?.Invoke(from.Id, pcm);
        RelayToOthers(from, WireMessage.VoiceChunk(pcm));
    }

    private void RouteCamera(PeerSlot from, byte[] frame)
    {
        CameraFrameReceived?.Invoke(from.Id, frame);
        RelayToOthers(from, WireMessage.CameraFrame(frame));
    }

    /// <summary>The owner is the hub: what one guest says, shows or sends is forwarded to the other guests (never back to the sender).</summary>
    private void RelayToOthers(PeerSlot from, WireMessage inner)
    {
        if (_config.Role != PeerRole.Server) return;
        WireMessage? wrapped = null;
        foreach (var s in Joined().Where(s => !ReferenceEquals(s, from) && s.Known))
        {
            // Voice and camera are dropped (not queued) for a guest whose link is slow: the call never lags behind.
            wrapped ??= WireMessage.RelayMsg(from.Id, inner);
            _ = s.Connection!.SendAsync(wrapped);
        }
    }

    private void HandleRelay(WireMessage msg)
    {
        WireMessage inner;
        try { inner = msg.Unwrap(); } catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException) { return; }
        var sender = msg.Text ?? "";
        switch (inner.Type)
        {
            case MessageType.Chat: if (!string.IsNullOrWhiteSpace(inner.Text)) ChatReceived?.Invoke(NameOf(sender), inner.Text!.Length > 2000 ? inner.Text[..2000] : inner.Text); break;
            case MessageType.Voice: VoiceReceived?.Invoke(sender, inner.Data); break;
            case MessageType.CameraFrame: CameraFrameReceived?.Invoke(sender, inner.Data); break;
        }
    }

    /// <summary>What a guest that is not the mouse peer may tell the owner: who it is. Everything else is ignored.</summary>
    private void HandleSecondary(PeerSlot slot, WireMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.Hello:
                if (msg.ScreenWidth <= 0 || msg.ScreenHeight <= 0) return;
                slot.Width = msg.ScreenWidth; slot.Height = msg.ScreenHeight; slot.Known = true; slot.Status = "connected";
                slot.Pc = msg.MachineName ?? "Otra PC"; slot.Name = slot.ProfileName ?? slot.Pc;
                slot.Displays = msg.Displays.Length > 0 ? msg.Displays : new[] { new DisplayInfo("desktop", 0, 0, slot.Width, slot.Height) };
                slot.MapSupported = msg.SupportsSpatialMap; slot.Extras = msg.SupportsExtras;
                RemoveDuplicates(slot);
                if (ReferenceEquals(_active, slot)) { HandleMessage(slot, msg); return; } // it replaced the active PC that had died silently
                if (slot.Connection is { } c) { _ = c.SendAsync(WireMessage.Management(false)); SendProfileTo(c); }
                RefreshSystemEffects();
                AddEvent($"{slot.Name} entró a la sala.");
                Notice?.Invoke($"{slot.Name} entró a la sala. Usa «Usar el mouse con esta PC» para controlarla.");
                BroadcastRoster();
                break;
            case MessageType.Profile:
                HandleProfileFor(slot, msg.Text);
                BroadcastRoster();
                break;
            case MessageType.Leave:
                slot.Status = "left"; slot.Known = false; RefreshSystemEffects();
                StatusChanged?.Invoke($"{slot.Name} salió de la sala.");
                break;
            case MessageType.SwitchToRemote:
                // Its mouse wants to cross to this PC but the mouse works with another guest right now: send it back.
                if (slot.Connection is { } back) _ = back.SendAsync(WireMessage.Yield());
                break;
        }
    }

    /// <summary>Connections of everyone in the room right now (the owner's messages go to all of them).</summary>
    private PeerConnection[] RoomConnections() => _config.Role == PeerRole.Server ? Joined().Select(s => s.Connection!).ToArray() : _connection is { } c ? new[] { c } : Array.Empty<PeerConnection>();
}
