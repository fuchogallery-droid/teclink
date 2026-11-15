// Real bridges talking to each other in one process (no hooks, no panel): one owner and several guests on loopback.
// Checks the room of up to 10 PCs: who is in it, chat/voice/camera for everyone, the mouse peer switch, leaving and the 10-PC limit.
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Windows.Forms;
using TecLink.App;

var temp = Path.Combine(Path.GetTempPath(), "teclink-room-test-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(temp);
Environment.SetEnvironmentVariable("TECLINK_CONFIG", Path.Combine(temp, "config.json"));
Environment.SetEnvironmentVariable("TECLINK_LOGS", temp);

var checks = 0; var failures = 0;
void Check(bool ok, string what) { checks++; if (!ok) { failures++; Console.WriteLine("FAIL: " + what); } else Console.WriteLine("ok:   " + what); }

// A UI thread with a message pump: the bridge hands work to it exactly as in the real app.
Control? ui = null; var started = new ManualResetEventSlim();
var uiThread = new Thread(() => { ui = new Control(); _ = ui.Handle; started.Set(); Application.Run(); }) { IsBackground = true };
uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start(); started.Wait();
T OnUi<T>(Func<T> f) => (T)ui!.Invoke(f)!;

static async Task<bool> Until(Func<bool> cond, int seconds = 25)
{
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end) { try { if (cond()) return true; } catch (Exception) { } await Task.Delay(150); }
    try { return cond(); } catch (Exception) { return false; }
}

const string Code = "K7M4QX";
var port = 52100;
object Ui(Func<object> f) => OnUi(f);

// ---------- Phase 0: two PCs, exactly as before ----------
Console.WriteLine("== Dos PCs (comportamiento de siempre) ==");
{
    var duoPort = port - 10;
    var h = Rig.Make(Ui, "DUO-HOST", true, duoPort, Code);
    var a = Rig.Make(Ui, "DUO-A", false, duoPort, Code);
    bool Connected(Peer p) => p.State().RootElement.GetProperty("connected").GetBoolean();
    int Remote(Peer p) => p.State().RootElement.GetProperty("remote").GetArrayLength();
    string RemoteName(Peer p) => p.State().RootElement.GetProperty("remoteName").GetString() ?? "";
    string PeerStatus(Peer p) => p.State().RootElement.GetProperty("peerStatus").GetString() ?? "";
    Check(await Until(() => Connected(h) && Connected(a), 40), "las dos PCs quedan conectadas");
    Check(RemoteName(h) == "DUO-A" && RemoteName(a) == "DUO-HOST", "cada una ve el nombre de la otra");
    Check(Remote(h) >= 1 && Remote(a) >= 1, "cada una ve las pantallas de la otra");
    Check(h.State().RootElement.GetProperty("members").GetArrayLength() == 2, "la sala tiene 2 miembros");
    a.Bridge.AnnounceLeave(); a.Cts.Cancel();
    Check(await Until(() => !Connected(h) && PeerStatus(h) == "left", 25), "al salir, el anfitrión ve «salió de la sesión» y deja de estar conectado");
    Check(Remote(h) >= 1, "las pantallas del que salió quedan listadas como desconectadas");
    var back = Rig.Make(Ui, "DUO-A", false, duoPort, Code);
    Check(await Until(() => Connected(h) && Connected(back), 40), "el mismo invitado puede volver a entrar");
    Check(h.State().RootElement.GetProperty("members").GetArrayLength() == 2, "sigue siendo una sala de 2, sin asientos duplicados");
    h.Cts.Cancel(); back.Cts.Cancel(); await Task.Delay(1000);
}

// ---------- Phase 1: an owner and three guests ----------
Console.WriteLine("== Sala de 4 PCs ==");
var host = Rig.Make(Ui, "ANFITRION", true, port, Code);
var guests = new List<Peer>();
foreach (var n in new[] { "G1", "G2", "G3" }) guests.Add(Rig.Make(Ui, n, false, port, Code));

Check(await Until(() => host.MemberCount() == 4, 40), "el anfitrión ve a las 4 PCs en la sala");
Check(await Until(() => guests.All(g => g.MemberCount() == 4), 20), "cada invitado ve a las 4 PCs (la lista llega por el anfitrión)");
Check(guests.All(g => g.Members().EnumerateArray().Count(m => m.GetProperty("me").GetBoolean()) == 1), "cada invitado se reconoce a sí mismo en la lista");
Check(host.State().RootElement.GetProperty("members").EnumerateArray().Count(m => m.GetProperty("mouse").GetBoolean()) == 1, "una sola PC tiene el mouse");

var (g1, g2, g3) = (guests[0], guests[1], guests[2]);
g1.Bridge.SendChat("hola desde G1");
Check(await Until(() => host.Chats.Any(c => c.Text == "hola desde G1" && c.From == "G1")), "el anfitrión recibe el chat de G1 con su nombre");
Check(await Until(() => g2.Chats.Any(c => c.Text == "hola desde G1" && c.From == "G1") && g3.Chats.Any(c => c.Text == "hola desde G1" && c.From == "G1")), "G2 y G3 reciben el chat de G1 (pasa por el anfitrión)");
Check(!g1.Chats.Any(c => c.Text == "hola desde G1"), "G1 no recibe su propio mensaje de vuelta");

host.Bridge.SendChat("hola a todos");
Check(await Until(() => guests.All(g => g.Chats.Any(c => c.Text == "hola a todos"))), "el chat del anfitrión llega a los 3 invitados");

g2.Bridge.SendCameraFrame(new byte[] { 1, 2, 3, 4, 5 });
Check(await Until(() => host.Cams.Count > 0 && g1.Cams.Count > 0 && g3.Cams.Count > 0), "la cámara de G2 llega al anfitrión y a G1 y G3");
Check(g1.Cams.All(c => c.Id.Length > 0) && g1.Cams[0].Id == g3.Cams[0].Id, "G1 y G3 identifican la misma cámara de G2");
g3.Bridge.SendVoice(new byte[640]);
Check(await Until(() => host.Voice.Count > 0 && g1.Voice.Count > 0 && g2.Voice.Count > 0), "la voz de G3 llega a todos");

// The mouse peer
string IdOf(string pc) => host.Members().EnumerateArray().First(m => m.GetProperty("pc").GetString() == pc).GetProperty("id").GetString()!;
var first = host.Members().EnumerateArray().First(m => m.GetProperty("mouse").GetBoolean()).GetProperty("pc").GetString();
var other = new[] { "G1", "G2", "G3" }.First(n => n != first);
host.Bridge.UseMouseWith(IdOf(other));
Check(await Until(() => host.Members().EnumerateArray().Any(m => m.GetProperty("pc").GetString() == other && m.GetProperty("mouse").GetBoolean())), $"el mouse pasa a {other} cuando el dueño lo pide");
Check(host.Members().EnumerateArray().Count(m => m.GetProperty("mouse").GetBoolean()) == 1, "sigue habiendo una sola PC con el mouse");
g1.Bridge.SendChat("sigo conectado");
Check(await Until(() => host.Chats.Any(c => c.Text == "sigo conectado")), "el chat sigue funcionando para todos tras cambiar el mouse");

// Leaving
var leaver = guests.First(g => g.Name != other);
leaver.Bridge.AnnounceLeave(); leaver.Cts.Cancel();
Check(await Until(() => host.MemberCount() == 3, 25), "cuando un invitado sale, el anfitrión lo quita de la sala");
Check(await Until(() => guests.Where(g => g != leaver).All(g => g.MemberCount() == 3), 15), "los demás invitados ven la lista actualizada");

// Kick
var kicked = guests.First(g => g != leaver && g.Name != other);
host.Bridge.KickGuest(IdOf(kicked.Name));
Check(await Until(() => host.MemberCount() == 2, 25), "el dueño puede sacar a un invitado");

foreach (var p in guests.Append(host)) p.Cts.Cancel();
await Task.Delay(1500);

// ---------- Phase 2: the room is full at 10 PCs ----------
Console.WriteLine("== Límite de 10 PCs ==");
port++;
var host2 = Rig.Make(Ui, "ANFITRION2", true, port, Code);
var many = new List<Peer>();
for (var i = 1; i <= 9; i++) many.Add(Rig.Make(Ui, "P" + i, false, port, Code));
Check(await Until(() => host2.MemberCount() == 10, 70), "la sala admite 10 PCs (dueño + 9 invitados)");
var extra = Rig.Make(Ui, "P10", false, port, Code);
Check(await Until(() => extra.JoinNotice().Contains("llena"), 40), "la PC número 11 recibe el aviso de sala llena");
Check(host2.MemberCount() == 10, "la sala se queda en 10 PCs");

foreach (var p in many.Append(host2).Append(extra)) p.Cts.Cancel();
await Task.Delay(1000);
// ---------- Phase 3: what TecLink touches in Windows can be put on and taken off, over and over ----------
Console.WriteLine("== Ganchos del sistema (se instalan y se retiran sin dejar nada) ==");
{
    var ok = OnUi<bool>(() =>
    {
        try
        {
            var mouse = new TecLink.App.Input.MouseHook(); var keys = new TecLink.App.Input.KeyboardHook(); var raw = new TecLink.App.Input.RawMouseInput();
            for (var round = 0; round < 3; round++)
            {
                raw.Install(); mouse.Install(); keys.Install();
                Application.DoEvents();
                mouse.Dispose(); keys.Dispose(); raw.Uninstall();
            }
            raw.Dispose();
            return true;
        }
        catch (Exception ex) { Console.WriteLine(ex); return false; }
    });
    Check(ok, "los ganchos del mouse, del teclado y el mouse crudo se instalan y se retiran 3 veces sin fallar");
    var clipOk = OnUi<bool>(() =>
    {
        var clip = new ClipboardWatcher(); _ = clip.Handle;
        clip.Sharing = true; var on = clip.Sharing; clip.Sharing = false; var off = !clip.Sharing; clip.Dispose();
        return on && off;
    });
    Check(clipOk, "el portapapeles solo se escucha cuando se enciende y deja de escucharse al apagarlo");
    // One pasted text with the code and the address of somebody on another network (Tailscale, another city)
    var invited = OnUi<EdgeBridge>(() => { var clip = new ClipboardWatcher(); _ = clip.Handle; var cfg = new AppConfig { MachineName = "REMOTO", Role = PeerRole.Server, SessionCode = "ZZZZZZ" }; var b = new EdgeBridge(cfg, clip) { SystemEffects = false }; b.JoinSession("K7M-4QX@100.73.208.1"); return b; });
    var inviteState = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(invited.ViewState(""))).RootElement;
    Check(inviteState.GetProperty("joinCode").GetString() == "K7M-4QX" && inviteState.GetProperty("peerAddress").GetString() == "100.73.208.1", "una invitación «CODIGO@direccion» se separa sola en código y dirección");    var fresh = new AppConfig();
    Check(!fresh.ShareClipboard && !fresh.AutoReceive, "de fábrica no se comparte el portapapeles ni se reciben archivos solos");
}

// ---------- Phase 4: a room on the internet through the relay (the two sides only connect OUT; the relay sees only encrypted data) ----------
Console.WriteLine("== Sala en línea a través del servidor puente ==");
{
    var relayPort = 58300;
    var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    var relay = builder.Build();
    TecLink.Relay.RelayServer.Map(relay);
    relay.Urls.Add($"http://127.0.0.1:{relayPort}");
    await relay.StartAsync();
    var relayHost = $"127.0.0.1:{relayPort}";
    var secret = TecLink.Core.Networking.Relay.OnlineInvite.NewSecret();
    var onlineHost = Rig.Make(Ui, "NUBE-HOST", true, port + 30, "J4Q7PX", hostOnlineRelay: relayHost, hostOnlineSecret: secret);
    var invite = TecLink.Core.Networking.Relay.OnlineInvite.Format("J4Q7PX", secret, relayHost);
    Check(await Until(() => onlineHost.Bridge.OnlineInviteText == invite, 5), "el dueño arma la invitación en línea (código + secreto + servidor)");
    Check(TecLink.Core.Networking.Relay.OnlineInvite.TryParse(invite, out var pc, out var ps, out var ph) && pc == "J4Q7PX" && ps == secret && ph == relayHost, "la invitación se vuelve a leer completa");
    await Task.Delay(1500); // the room must be open on the relay before anybody comes
    var a = Rig.Make(Ui, "NUBE-A", false, port + 30, "J4Q7PX", invite: invite);
    var b = Rig.Make(Ui, "NUBE-B", false, port + 30, "J4Q7PX", invite: invite);
    Check(await Until(() => onlineHost.MemberCount() == 3, 40), "dos invitados entran por el puente y el dueño los ve (3 PCs)");
    Check(await Until(() => a.MemberCount() == 3 && b.MemberCount() == 3, 20), "cada invitado ve a las 3 PCs");
    a.Bridge.SendChat("hola por internet");
    Check(await Until(() => onlineHost.Chats.Any(c => c.Text == "hola por internet") && b.Chats.Any(c => c.Text == "hola por internet" && c.From == "NUBE-A")), "el chat llega al dueño y al otro invitado a través del puente");
    onlineHost.Bridge.SendChat("respondo desde el dueño");
    Check(await Until(() => a.Chats.Any(c => c.Text == "respondo desde el dueño") && b.Chats.Any(c => c.Text == "respondo desde el dueño")), "el dueño también les escribe");
    b.Bridge.SendCameraFrame(new byte[2000]);
    Check(await Until(() => onlineHost.Cams.Any(c => c.Length == 2000) && a.Cams.Any(c => c.Length == 2000)), "la cámara viaja cifrada por el puente");
    // somebody with a wrong secret (a guessed or old invitation) gets nothing
    var wrong = TecLink.Core.Networking.Relay.OnlineInvite.Format("J4Q7PX", TecLink.Core.Networking.Relay.OnlineInvite.NewSecret(), relayHost);
    var intruder = Rig.Make(Ui, "INTRUSO", false, port + 30, "J4Q7PX", invite: wrong);
    Check(await Until(() => intruder.JoinNotice().Length > 0, 20), "una invitación equivocada recibe un aviso claro");
    await Task.Delay(1000);
    Check(onlineHost.MemberCount() == 3, "el intruso no entra a la sala");
    foreach (var p in new[] { onlineHost, a, b, intruder }) p.Cts.Cancel();
    await relay.StopAsync();
}

// ---------- Phase 5 (only when TECLINK_TEST_RELAY is set): the same, but through a relay that really is on the internet ----------
if (Environment.GetEnvironmentVariable("TECLINK_TEST_RELAY") is { Length: > 0 } publicRelay)
{
    Console.WriteLine("== Sala en línea por el servidor puente PÚBLICO (" + publicRelay + ") ==");
    var secret2 = TecLink.Core.Networking.Relay.OnlineInvite.NewSecret();
    var h = Rig.Make(Ui, "INTERNET-HOST", true, port + 40, "M6T2RW", hostOnlineRelay: publicRelay, hostOnlineSecret: secret2);
    await Task.Delay(3000);
    var g = Rig.Make(Ui, "INTERNET-G", false, port + 40, "M6T2RW", invite: TecLink.Core.Networking.Relay.OnlineInvite.Format("M6T2RW", secret2, publicRelay));
    Check(await Until(() => h.MemberCount() == 2 && g.MemberCount() == 2, 60), "el invitado entra a la sala por el servidor público");
    g.Bridge.SendChat("hola desde internet de verdad");
    Check(await Until(() => h.Chats.Any(c => c.Text == "hola desde internet de verdad"), 20), "el chat llega por el servidor público");
    h.Cts.Cancel(); g.Cts.Cancel();
}
Console.WriteLine(failures == 0 ? $"PASS: {checks} comprobaciones de la sala multi-PC" : $"FALLÓ: {failures} de {checks}");
Environment.Exit(failures == 0 ? 0 : 1);

sealed class Peer
{
    public required EdgeBridge Bridge; public required string Name; public required CancellationTokenSource Cts; public Task Run = Task.CompletedTask;
    public readonly List<(string From, string Text)> Chats = new();
    public readonly List<(string Id, int Length)> Voice = new(), Cams = new();
    public JsonDocument State() => JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(Bridge.ViewState("")));
    public JsonElement Members() => State().RootElement.GetProperty("members").Clone();
    public int MemberCount() => Members().GetArrayLength();
    public string JoinNotice() => State().RootElement.GetProperty("joinNotice").GetString() ?? "";
}

static class Rig
{
    public static Peer Make(Func<Func<object>, object> ui, string name, bool host, int port, string code, string? invite = null, string? hostOnlineRelay = null, string? hostOnlineSecret = null)
    {
        var cts = new CancellationTokenSource();
        var bridge = (EdgeBridge)ui(() =>
        {
            var clip = new ClipboardWatcher(); _ = clip.Handle;
            var cfg = new AppConfig { MachineName = name, Port = port, SessionCode = host ? code : "ZZZZZZ", Role = host ? PeerRole.Server : PeerRole.Client, JoinCode = host ? "" : code, PeerHost = invite is null ? "127.0.0.1" : "" };
            if (hostOnlineRelay is not null) { cfg.OnlineEnabled = true; cfg.RelayHost = hostOnlineRelay; cfg.RelayHostPinned = true; cfg.OnlineSecret = hostOnlineSecret!; }
            var b = new EdgeBridge(cfg, clip);
            if (invite is not null) b.JoinSession(invite);
            return b;
        });
        var peer = new Peer { Bridge = bridge, Name = name, Cts = cts };
        bridge.ChatReceived += (from, text) => { lock (peer.Chats) peer.Chats.Add((from, text)); };
        bridge.VoiceReceived += (id, pcm) => { lock (peer.Voice) peer.Voice.Add((id, pcm.Length)); };
        bridge.CameraFrameReceived += (id, frame) => { lock (peer.Cams) peer.Cams.Add((id, frame.Length)); };
        bridge.SystemEffects = false; // a test never hooks the machine it runs on
        bridge.NetworkActive = true;
        peer.Run = Task.Run(() => bridge.RunNetworkingAsync(cts.Token));
        return peer;
    }
}

