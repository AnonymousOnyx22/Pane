using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pane.Protocol;

namespace Pane;

/// <summary>A decoded screen rectangle: BGRA32 pixels ready to blit at (X,Y).</summary>
public readonly record struct DecodedRect(int X, int Y, int W, int H, byte[] Bgra, int Stride);

/// <summary>
/// Client side of one remote session: connects, authenticates with the pairing key, receives frames,
/// sends input, and handles file transfer (list/download/upload). All events are raised from a
/// background task; the UI marshals them to the dispatcher itself.
/// </summary>
public sealed class RemoteClient : IDisposable
{
    public event Action<int, int>? Connected;              // screen width, height
    public event Action<List<DecodedRect>>? FrameDecoded;  // pixels decoded OFF the UI thread
    public event Action<string>? Disconnected;             // reason

    public event Action<string, List<FsEntry>>? DirListing; // path, entries
    public event Action<string>? ClipboardReceived;         // text pushed from the host
    public event Action<string>? TransferStatus;            // human-readable progress/notice

    /// <summary>Where pulled files are written. Defaults to Downloads\Pane Transfers.</summary>
    public string DownloadDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Pane Transfers");

    private TcpClient? _tcp;
    private SecureChannel? _channel;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _down;

    private FileStream? _dlStream;
    private int _dlFiles;

    private long _frames, _bytes;
    public long TotalFrames => Interlocked.Read(ref _frames);
    public long TotalBytes => Interlocked.Read(ref _bytes);

    public async void Start(string host, int port, byte[] key)
    {
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(host, port, _cts.Token);
            _channel = await SecureChannel.ClientHandshakeAsync(_tcp.GetStream(), key, _cts.Token);

            var first = await _channel.ReceiveAsync(_cts.Token);
            if (first == null || Messages.TypeOf(first) != MsgType.ScreenInfo)
                throw new Exception("Host did not send screen info.");
            var (w, h) = Messages.ReadScreenInfo(first);
            Connected?.Invoke(w, h);

            while (!_cts.IsCancellationRequested)
            {
                var msg = await _channel.ReceiveAsync(_cts.Token);
                if (msg == null) break;
                Handle(msg);
            }
            Fail("Connection closed.");
        }
        catch (OperationCanceledException) { Fail("Disconnected."); }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void Handle(byte[] msg)
    {
        switch (Messages.TypeOf(msg))
        {
            case MsgType.Frame:
                {
                    // Decode JPEG here on the receive thread. Only the cheap WritePixels then runs on the UI
                    // thread, so WPF's compositor stays free to animate at 60fps instead of being saturated.
                    Interlocked.Increment(ref _frames);
                    Interlocked.Add(ref _bytes, msg.Length);
                    var rects = Messages.ReadFrame(msg);
                    var decoded = rects.AsParallel().AsOrdered().Select(Decode)
                        .Where(d => d.HasValue).Select(d => d!.Value).ToList();
                    if (decoded.Count > 0) FrameDecoded?.Invoke(decoded);
                    break;
                }
            case MsgType.Clipboard:
                ClipboardReceived?.Invoke(Messages.ReadClipboard(msg)); break;
            case MsgType.DirListing:
                { var (path, entries) = Fs.ReadListing(msg); DirListing?.Invoke(path, entries); break; }
            case MsgType.FileBegin:
                BeginDownloadFile(msg); break;
            case MsgType.FileChunk:
                { var seg = Fs.ReadChunk(msg); try { _dlStream?.Write(seg.Array!, seg.Offset, seg.Count); } catch { } break; }
            case MsgType.FileEnd:
                try { _dlStream?.Dispose(); } catch { } _dlStream = null; break;
            case MsgType.TransferDone:
                TransferStatus?.Invoke($"Downloaded {_dlFiles} item(s) to {DownloadDir}"); _dlFiles = 0; break;
            case MsgType.FsError:
                TransferStatus?.Invoke("Transfer error: " + Fs.ReadError(msg)); break;
        }
    }

    private static DecodedRect? Decode(FrameRect r)
    {
        if (r.W <= 0 || r.H <= 0) return null;
        try
        {
            BitmapSource src = BitmapFrame.Create(new MemoryStream(r.Compressed),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (src.Format != PixelFormats.Bgra32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int stride = r.W * 4;
            var px = new byte[stride * r.H];
            src.CopyPixels(px, stride, 0);
            return new DecodedRect(r.X, r.Y, r.W, r.H, px, stride);
        }
        catch { return null; }
    }

    private void BeginDownloadFile(byte[] msg)
    {
        try { _dlStream?.Dispose(); } catch { }
        _dlStream = null;
        var (relPath, _) = Fs.ReadBegin(msg);
        try
        {
            var dest = Path.Combine(DownloadDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            _dlStream = File.Create(dest);
            _dlFiles++;
            TransferStatus?.Invoke("Downloading " + relPath);
        }
        catch (Exception ex) { TransferStatus?.Invoke("Download failed: " + ex.Message); }
    }

    private void Fail(string reason)
    {
        if (_down) return;
        _down = true;
        Disconnected?.Invoke(reason);
    }

    private async void Send(byte[] msg)
    {
        var ch = _channel;
        if (ch == null || _cts.IsCancellationRequested) return;
        try { await ch.SendAsync(msg, _cts.Token); }
        catch { Fail("Send failed."); }
    }

    public void SendMouseMove(float nx, float ny) => Send(Messages.MouseMove(nx, ny));
    public void SendMouseButton(int button, bool down, float nx, float ny) => Send(Messages.MouseButton(button, down, nx, ny));
    public void SendMouseWheel(int delta, bool horizontal, float nx, float ny) => Send(Messages.MouseWheel(delta, horizontal, nx, ny));
    public void SendKey(ushort vk, bool down, bool extended) => Send(Messages.Key(vk, down, extended));
    public void SendQuality(int high, int low) => Send(Messages.SetQuality(high, low));
    public void SendClipboard(string text) => Send(Messages.Clipboard(text));
    public void RequestFullFrame() => Send(Messages.RequestFullFrame());

    // --- file transfer ---

    public void RequestListDir(string path) => Send(Fs.ListDir(path));
    public void RequestDownload(string path) => Send(Fs.Download(path));

    /// <summary>Upload local files/folders to the host's drop folder. Runs on a background task.</summary>
    public Task UploadAsync(IEnumerable<string> localPaths) => Task.Run(async () =>
    {
        var ch = _channel;
        if (ch == null) return;
        try
        {
            int count = 0;
            foreach (var p in localPaths)
            {
                if (File.Exists(p)) { await SendOneAsync(ch, p, Path.GetFileName(p)); count++; }
                else if (Directory.Exists(p))
                {
                    var baseName = new DirectoryInfo(p.TrimEnd('\\', '/')).Name;
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.Combine(baseName, Path.GetRelativePath(p, f));
                        await SendOneAsync(ch, f, rel); count++;
                    }
                }
            }
            TransferStatus?.Invoke($"Sent {count} file(s) to the other PC's Desktop\\Pane Transfers");
        }
        catch (Exception ex) { TransferStatus?.Invoke("Upload failed: " + ex.Message); }
    });

    private async Task SendOneAsync(SecureChannel ch, string fullPath, string relPath)
    {
        TransferStatus?.Invoke("Sending " + relPath);
        using var fs = File.OpenRead(fullPath);
        await ch.SendAsync(Fs.UploadBegin(relPath, fs.Length), _cts.Token);
        var buf = new byte[Fs.ChunkSize];
        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(0, buf.Length), _cts.Token)) > 0)
            await ch.SendAsync(Fs.UploadChunk(buf, n), _cts.Token);
        await ch.SendAsync(Fs.UploadEnd(), _cts.Token);
    }

    // --- handoff ---

    private static readonly HashSet<string> HandoffSkip =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", ".vs", "dist", "build", "__pycache__" };

    /// <summary>
    /// Hand the project off to the other PC: push the folder (rebased to each PC's user profile) and its
    /// Claude Code session, then open VS Code on it there. Runs on a background task.
    /// </summary>
    public Task HandoffAsync(string localFolder) => Task.Run(async () =>
    {
        var ch = _channel;
        if (ch == null) return;
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!localFolder.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            {
                TransferStatus?.Invoke("Handoff needs a folder under your user profile (C:\\Users\\...).");
                return;
            }

            int count = 0;
            foreach (var f in EnumerateProject(localFolder))
            {
                await SendHandoffFile(ch, f, Path.GetRelativePath(profile, f));
                count++;
            }

            // The matching Claude Code session (so the same conversation is available on the other PC).
            var claudeDir = Path.Combine(profile, ".claude", "projects", EncodeProject(localFolder));
            if (Directory.Exists(claudeDir))
                foreach (var f in Directory.EnumerateFiles(claudeDir, "*", SearchOption.AllDirectories))
                {
                    await SendHandoffFile(ch, f, Path.GetRelativePath(profile, f));
                    count++;
                }

            await ch.SendAsync(Fs.HandoffFinish(Path.GetRelativePath(profile, localFolder)), _cts.Token);
            TransferStatus?.Invoke($"Handed off {count} files \u2014 opening VS Code on the other PC");
        }
        catch (Exception ex) { TransferStatus?.Invoke("Handoff failed: " + ex.Message); }
    });

    private static IEnumerable<string> EnumerateProject(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subs, files;
            try { subs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in subs)
                if (!HandoffSkip.Contains(Path.GetFileName(s))) stack.Push(s);
        }
    }

    /// <summary>Encode a folder path the way Claude Code names its project session dir.</summary>
    private static string EncodeProject(string full)
    {
        if (full.Length >= 2 && full[1] == ':') full = char.ToLowerInvariant(full[0]) + full[1..];
        return full.Replace(':', '-').Replace('\\', '-').Replace('/', '-');
    }

    private async Task SendHandoffFile(SecureChannel ch, string fullPath, string rel)
    {
        TransferStatus?.Invoke("Sending " + rel);
        using var fs = File.OpenRead(fullPath);
        await ch.SendAsync(Fs.HandoffFile(rel, fs.Length), _cts.Token);
        var buf = new byte[Fs.ChunkSize];
        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(0, buf.Length), _cts.Token)) > 0)
            await ch.SendAsync(Fs.UploadChunk(buf, n), _cts.Token);
        await ch.SendAsync(Fs.UploadEnd(), _cts.Token);
    }

    public void Dispose()
    {
        _down = true;
        try { _cts.Cancel(); } catch { }
        try { _dlStream?.Dispose(); } catch { }
        _channel?.Dispose();
        _tcp?.Dispose();
        _cts.Dispose();
    }
}
