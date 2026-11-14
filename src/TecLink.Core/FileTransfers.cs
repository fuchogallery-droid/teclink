using TecLink.Core.Networking;
using TecLink.Core.Protocol;

namespace TecLink.Core;

/// <summary>
/// Sends files and whole folders to the peer and stores what the peer sends here.
/// Incoming messages are handled synchronously on the network thread, so a slow disk
/// slows the socket down instead of piling chunks up in memory.
/// </summary>
public sealed class FileTransfers
{
    private const int ChunkSize = 128 * 1024;
    private readonly Func<PeerConnection?> _connection;
    private readonly SemaphoreSlim _sendTurn = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Incoming> _incoming = new();
    private readonly Dictionary<string, CancellationTokenSource> _outgoing = new();
    private long _lastProgress;

    private sealed class Incoming
    {
        public readonly Dictionary<string, string> Roots = new(StringComparer.OrdinalIgnoreCase);
        public FileStream? Stream;
        public string? CurrentPath;
        public long Received, Total;
        public int Items;
        public bool Failed;
    }

    public string ReceiveFolder { get; }
    public string LastStatus { get; private set; } = "";
    public event Action<string>? Status;
    /// <summary>Raised when a transfer from the peer finished: (items, folder).</summary>
    public event Action<int, string>? Received;

    public FileTransfers(Func<PeerConnection?> connection, string receiveFolder)
    {
        _connection = connection;
        ReceiveFolder = receiveFolder;
    }

    private void Report(string text) { LastStatus = text; Status?.Invoke(text); }

    private void Progress(string verb, long done, long total)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastProgress) < 300) return;
        Interlocked.Exchange(ref _lastProgress, now);
        Report($"{verb} {(total > 0 ? done * 100 / total : 100)} % ({Readable(done)} de {Readable(total)})");
    }

    public static string Readable(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Relative path as sent on the wire → a safe relative path below the receive
    /// folder. Rejects rooted paths, drive letters and any attempt to climb out.</summary>
    public static string SafeRelativePath(string wirePath)
    {
        var parts = (wirePath ?? "").Split('/', '\\').Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0 || parts.Length > 64) throw new InvalidDataException("Ruta de archivo vacía o demasiado profunda.");
        var invalid = Path.GetInvalidFileNameChars();
        for (var n = 0; n < parts.Length; n++)
        {
            var clean = new string(parts[n].Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray()).TrimEnd(' ', '.');
            if (clean.Length == 0 || clean is "." or "..") throw new InvalidDataException("Ruta de archivo no permitida.");
            parts[n] = clean;
        }
        return Path.Combine(parts);
    }

    public static string UniqueName(string folder, string name)
    {
        if (!File.Exists(Path.Combine(folder, name)) && !Directory.Exists(Path.Combine(folder, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (!File.Exists(Path.Combine(folder, candidate)) && !Directory.Exists(Path.Combine(folder, candidate))) return candidate;
        }
    }

    public async Task SendAsync(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        if (_connection() is null) { Report("No se envió: la otra PC no está conectada o necesita actualizarse."); return; }
        await _sendTurn.WaitAsync();
        var id = Guid.NewGuid().ToString("N");
        var cancel = new CancellationTokenSource();
        lock (_gate) _outgoing[id] = cancel;
        try
        {
            Report("Preparando envío…");
            // (source path or null for a directory, wire path, size)
            var entries = new List<(string? Source, string Wire, long Size)>();
            foreach (var path in paths)
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (File.Exists(full)) entries.Add((full, Path.GetFileName(full), new FileInfo(full).Length));
                else if (Directory.Exists(full))
                {
                    var root = Path.GetFileName(full);
                    if (string.IsNullOrEmpty(root)) root = "Unidad " + full[0];
                    entries.Add((null, root, 0));
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var directory in Directory.EnumerateDirectories(full, "*", options))
                        entries.Add((null, root + "/" + Path.GetRelativePath(full, directory).Replace('\\', '/'), 0));
                    foreach (var file in Directory.EnumerateFiles(full, "*", options))
                        entries.Add((file, root + "/" + Path.GetRelativePath(full, file).Replace('\\', '/'), new FileInfo(file).Length));
                }
            }
            if (entries.Count == 0) { Report("No se encontró nada que enviar."); return; }

            var total = entries.Sum(e => e.Size);
            long sent = 0;
            var buffer = new byte[ChunkSize];
            foreach (var entry in entries)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var connection = _connection() ?? throw new IOException("Se perdió la conexión.");
                await connection.SendAsync(WireMessage.FileStart(id, entry.Wire, entry.Size, total, entry.Source is null));
                if (entry.Source is null) continue;
                await using (var stream = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ChunkSize, true))
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cancel.Token)) > 0)
                    {
                        connection = _connection() ?? throw new IOException("Se perdió la conexión.");
                        await connection.SendAsync(WireMessage.FileData(id, buffer.AsSpan(0, read).ToArray()));
                        sent += read;
                        Progress("Enviando", sent, total);
                    }
                }
                await connection.SendAsync(WireMessage.FileEnd(id));
            }
            if (_connection() is { } last) await last.SendAsync(WireMessage.TransferDone(id, true));
            Report($"Enviado: {entries.Count(e => e.Source is not null)} archivo(s), {Readable(total)}. Esperando confirmación…");
        }
        catch (OperationCanceledException) { /* the receiver already reported why */ }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_connection() is { } connection) _ = connection.SendAsync(WireMessage.TransferDone(id, false, ex.Message));
            Report("No se pudo enviar: " + ex.Message);
        }
        finally
        {
            lock (_gate) _outgoing.Remove(id);
            cancel.Dispose();
            _sendTurn.Release();
        }
    }

    public sealed record FileEntry(string Name, long Size, long Modified, bool IsDirectory);

    /// <summary>What this PC holds in its TecLink folder (top level only), newest first.</summary>
    public List<FileEntry> List()
    {
        var result = new List<FileEntry>();
        if (!Directory.Exists(ReceiveFolder)) return result;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden };
        foreach (var info in new DirectoryInfo(ReceiveFolder).EnumerateFileSystemInfos("*", options).Take(500))
            result.Add(new FileEntry(info.Name, info is FileInfo file ? file.Length : 0, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(), info is DirectoryInfo));
        return result.OrderByDescending(e => e.Modified).ToList();
    }

    /// <summary>A name from the panel or the peer → full path inside the TecLink folder, or null.</summary>
    public string? PathInside(string name)
    {
        if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name is "." or "..") return null;
        var path = Path.Combine(ReceiveFolder, name);
        return File.Exists(path) || Directory.Exists(path) ? path : null;
    }

    /// <summary>Copies a file or folder to this PC's desktop (never overwrites).</summary>
    public string CopyToDesktop(string path)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var target = Path.Combine(desktop, UniqueName(desktop, Path.GetFileName(path)));
        if (File.Exists(path)) File.Copy(path, target);
        else CopyDirectory(path, target);
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var dir in Directory.EnumerateDirectories(source, "*", options)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", options)) File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }

    /// <summary>The peer asked for something from our folder: "fetch:name" or "desktop:name".</summary>
    public void RunCommand(string command)
    {
        var split = (command ?? "").IndexOf(':');
        if (split < 0) return;
        var verb = command![..split]; var name = command[(split + 1)..];
        var path = PathInside(name);
        if (path is null) { Report("Ya no está en la otra PC: " + name); return; }
        try
        {
            if (verb == "fetch") _ = SendAsync(new[] { path });
            else if (verb == "desktop") { CopyToDesktop(path); Report("Puesto en tu escritorio por la otra PC: " + name); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report("No se pudo: " + ex.Message); }
    }

    /// <summary>Returns true when the message belonged to file transfer.</summary>
    public bool Handle(WireMessage message)
    {
        if (message.Type is not (MessageType.FileStart or MessageType.FileData or MessageType.FileEnd or MessageType.TransferDone)) return false;
        var id = message.RequestId ?? "";

        if (message.Type == MessageType.TransferDone)
        {
            CancellationTokenSource? sending;
            bool ours;
            lock (_gate) ours = _outgoing.TryGetValue(id, out sending);
            if (ours || !HasIncoming(id))
            {
                // The receiver's verdict on something we sent.
                if (message.Accepted) Report("Entregado en la otra PC: " + message.Text);
                else { try { sending?.Cancel(); } catch (ObjectDisposedException) { } Report("La otra PC no pudo guardar: " + message.Text); }
                return true;
            }
        }

        Incoming? state;
        lock (_gate)
        {
            if (!_incoming.TryGetValue(id, out state))
            {
                if (message.Type != MessageType.FileStart) return true; // leftovers of a failed transfer
                _incoming[id] = state = new Incoming { Total = message.Total };
            }
        }
        if (state.Failed) { if (message.Type == MessageType.TransferDone) Forget(id, state); return true; }

        try
        {
            switch (message.Type)
            {
                case MessageType.FileStart:
                    Close(state);
                    var relative = SafeRelativePath(message.Text ?? "");
                    var separator = relative.IndexOf(Path.DirectorySeparatorChar);
                    var root = separator < 0 ? relative : relative[..separator];
                    Directory.CreateDirectory(ReceiveFolder);
                    if (!state.Roots.TryGetValue(root, out var mapped))
                        state.Roots[root] = mapped = UniqueName(ReceiveFolder, root);
                    var target = Path.Combine(ReceiveFolder, separator < 0 ? mapped : mapped + relative[separator..]);
                    if (message.IsDirectory) Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        state.Stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize);
                        state.CurrentPath = target;
                        state.Items++;
                    }
                    break;
                case MessageType.FileData:
                    if (state.Stream is null) throw new InvalidDataException("Llegaron datos sin archivo abierto.");
                    state.Stream.Write(message.Data);
                    state.Received += message.Data.Length;
                    Progress("Recibiendo", state.Received, state.Total);
                    break;
                case MessageType.FileEnd:
                    Close(state);
                    break;
                case MessageType.TransferDone:
                    if (message.Accepted)
                    {
                        Close(state);
                        Forget(id, state);
                        var summary = $"{state.Items} archivo(s), {Readable(state.Received)}";
                        _ = _connection()?.SendAsync(WireMessage.TransferDone(id, true, summary));
                        Report($"Recibido: {summary} en {ReceiveFolder}");
                        Received?.Invoke(state.Items, ReceiveFolder);
                    }
                    else
                    {
                        Discard(state);
                        Forget(id, state);
                        Report("La otra PC canceló el envío: " + message.Text);
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            state.Failed = true;
            Discard(state);
            _ = _connection()?.SendAsync(WireMessage.TransferDone(id, false, ex.Message));
            Report("No se pudo guardar lo recibido: " + ex.Message);
        }
        return true;
    }

    private bool HasIncoming(string id) { lock (_gate) return _incoming.ContainsKey(id); }
    private void Forget(string id, Incoming state) { lock (_gate) _incoming.Remove(id); }

    private static void Close(Incoming state)
    {
        state.Stream?.Dispose();
        state.Stream = null;
        state.CurrentPath = null;
    }

    private static void Discard(Incoming state)
    {
        var partial = state.CurrentPath;
        Close(state);
        if (partial is not null) try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The link dropped: stop sending and discard the file that was half written.</summary>
    public void Abort()
    {
        bool any;
        lock (_gate)
        {
            any = _incoming.Count > 0 || _outgoing.Count > 0;
            foreach (var cancel in _outgoing.Values) try { cancel.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var state in _incoming.Values) Discard(state);
            _incoming.Clear();
        }
        if (any) Report("Transferencia interrumpida: se perdió la conexión.");
    }
}
