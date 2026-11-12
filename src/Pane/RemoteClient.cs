using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pane.Media;
using Pane.Protocol;

namespace Pane;

/// <summary>A decoded screen rectangle: BGRA32 pixels ready to blit at (X,Y).</summary>
public readonly record struct DecodedRect(int X, int Y, int W, int H, byte[] Bgra, int Stride, bool Pooled = false);

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
    public event Action<byte[]>? ClipboardImageReceived;    // PNG pushed from the host
    public event Action<List<string>>? ClipboardFilesReceived; // local paths of files copied on the host
    public event Action<int, int>? ScreenChanged;           // host switched monitor / resolution
    public event Action<List<MonitorInfo>, int>? MonitorsChanged; // all host displays + the captured one
    public event Action<string>? CodecChanged;              // e.g. "H.264 · NVIDIA H.264 Encoder MFT"
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

    // Video
    private H264Decoder? _decoder;
    private bool _h264Failed;
    private volatile int _w, _h;
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _framePool = new();
    /// <summary>Frames handed to the UI but not drawn yet. Video frames are skipped (not queued) when
    /// the UI falls behind, so latency never builds up.</summary>
    public int UiBacklog;

    // Audio
    private readonly AudioPlayer _audio = new();
    private bool _audioOn = true;

    // Clipboard files arriving from the host go to a temp batch folder instead of Downloads.
    private string? _clipDir;
    private readonly SemaphoreSlim _uploadLock = new(1, 1); // one outgoing file stream at a time

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
            _w = w; _h = h;
            Connected?.Invoke(w, h);

            // Ask for H.264 when this PC can decode it (the host falls back to JPEG on its own if it
            // cannot encode), and for sound if it is enabled.
            if (!_h264Failed)
            {
                try { _decoder = new H264Decoder(); Send(Messages.SetCodec(Codec.H264)); }
                catch { _decoder = null; _h264Failed = true; }
            }
            if (_audioOn) Send(Messages.SetAudio(true));

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
            case MsgType.VideoFrame:
                Interlocked.Increment(ref _frames);
                Interlocked.Add(ref _bytes, msg.Length);
                DecodeVideo(msg);
                break;
            case MsgType.ScreenInfo:
                {
                    var (w, h) = Messages.ReadScreenInfo(msg);
                    _w = w; _h = h;
                    ScreenChanged?.Invoke(w, h);
                    break;
                }
            case MsgType.MonitorList:
                { var (mons, cur) = Messages.ReadMonitorList(msg); MonitorsChanged?.Invoke(mons, cur); break; }
            case MsgType.CodecInfo:
                {
                    var (c, name) = Messages.ReadCodecInfo(msg);
                    CodecChanged?.Invoke(c == Codec.H264 ? "H.264 \u00b7 " + ShortEncoderName(name) : "JPEG");
                    break;
                }
            case MsgType.Audio:
                {
                    Interlocked.Add(ref _bytes, msg.Length);
                    var (rate, ch) = Messages.ReadAudioHeader(msg);
                    _audio.Push(rate, ch, msg, 6, msg.Length - 6);
                    break;
                }
            case MsgType.Clipboard:
                ClipboardReceived?.Invoke(Messages.ReadClipboard(msg)); break;
            case MsgType.ClipboardImage:
                ClipboardImageReceived?.Invoke(Messages.Payload(msg).ToArray()); break;
            case MsgType.HostClipBegin:
                _clipDir = Path.Combine(Path.GetTempPath(), "Pane Clipboard", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                break;
            case MsgType.HostClipDone:
                {
                    var dir = _clipDir; _clipDir = null;
                    if (dir == null) break;
                    var paths = new List<string>();
                    foreach (var n in Fs.ReadNames(msg))
                    {
                        var full = Path.Combine(dir, n);
                        if (!File.Exists(full) && !Directory.Exists(full)) { try { Directory.CreateDirectory(full); } catch { continue; } }
                        paths.Add(full);
                    }
                    if (paths.Count > 0) ClipboardFilesReceived?.Invoke(paths);
                    break;
                }
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
            bool clip = _clipDir != null;
            var dest = Path.Combine(_clipDir ?? DownloadDir, SafeRelative(relPath));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            _dlStream = File.Create(dest);
            if (!clip) { _dlFiles++; TransferStatus?.Invoke("Downloading " + relPath); }
        }
        catch (Exception ex) { TransferStatus?.Invoke("Download failed: " + ex.Message); }
    }

    /// <summary>Keep a host-supplied relative path inside the destination folder.</summary>
    private static string SafeRelative(string rel)
    {
        var parts = rel.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
                       .Where(x => x != "." && x != ".." && !(x.Length >= 2 && x[1] == ':'));
        var joined = string.Join('\\', parts);
        return joined.Length == 0 ? "file.bin" : joined;
    }

    private static string ShortEncoderName(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA GPU";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel GPU";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return "AMD GPU";
        if (name.Contains("software", StringComparison.OrdinalIgnoreCase)) return "CPU";
        return name;
    }

    /// <summary>Decode one H.264 access unit (always, to keep the decoder's reference frames intact) and,
    /// unless the UI is behind, convert it to BGRA and hand it over.</summary>
    private unsafe void DecodeVideo(byte[] msg)
    {
        var dec = _decoder;
        if (dec == null) return;
        int w = _w, h = _h;
        try
        {
            dec.Decode(msg, 1, msg.Length - 1, (p, stride, planeH, pw, ph) =>
            {
                if (Volatile.Read(ref UiBacklog) >= 2) return; // UI behind: drop this picture, never queue
                int cw = Math.Min(w, pw), chh = Math.Min(h, ph);
                if (cw <= 0 || chh <= 0) return;
                int need = cw * chh * 4;
                if (!_framePool.TryDequeue(out var buf) || buf.Length != need) buf = new byte[need];
                Nv12.ToBgra(p, stride, planeH, cw, chh, buf);
                Interlocked.Increment(ref UiBacklog);
                FrameDecoded?.Invoke(new List<DecodedRect> { new(0, 0, cw, chh, buf, cw * 4, Pooled: true) });
            });
        }
        catch
        {
            // A broken decoder: drop to JPEG for the rest of this connection.
            _h264Failed = true;
            try { _decoder?.Dispose(); } catch { }
            _decoder = null;
            Send(Messages.SetCodec(Codec.Jpeg));
        }
    }

    /// <summary>Return a full-frame buffer once the UI has copied it.</summary>
    public void Recycle(DecodedRect r)
    {
        if (r.Pooled && _framePool.Count < 4) _framePool.Enqueue(r.Bgra);
    }

    public void SelectMonitor(int index) => Send(Messages.SelectMonitor(index));

    /// <summary>Set before Start: whether to ask for sound when the session opens.</summary>
    public bool StartWithAudio { get => _audioOn; set { _audioOn = value; _audio.Muted = !value; } }

    public bool AudioOn
    {
        get => _audioOn;
        set
        {
            _audioOn = value;
            _audio.Muted = !value;
            Send(Messages.SetAudio(value));
        }
    }

    public void SendClipboardImage(byte[] png) => Send(Messages.ClipboardImage(png));

    /// <summary>Copy local files/folders to the host's clipboard (sent to a temp folder there, then put
    /// on its clipboard so Ctrl+V pastes them).</summary>
    public Task SendClipFilesAsync(IReadOnlyList<string> paths) => Task.Run(async () =>
    {
        var ch = _channel;
        if (ch == null) return;
        await _uploadLock.WaitAsync();
        try
        {
            var names = new List<string>();
            int count = 0;
            foreach (var p in paths)
            {
                if (File.Exists(p))
                {
                    names.Add(Path.GetFileName(p));
                    await SendClipFileAsync(ch, p, Path.GetFileName(p)); count++;
                }
                else if (Directory.Exists(p))
                {
                    var baseName = new DirectoryInfo(p.TrimEnd('\\', '/')).Name;
                    names.Add(baseName);
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    {
                        await SendClipFileAsync(ch, f, Path.Combine(baseName, Path.GetRelativePath(p, f))); count++;
                    }
                }
            }
            await ch.SendAsync(Fs.ClipFilesDone(names), _cts.Token);
            TransferStatus?.Invoke($"Copied {count} file(s) to the other PC's clipboard \u2014 paste with Ctrl+V there");
        }
        catch (Exception ex) { TransferStatus?.Invoke("Clipboard copy failed: " + ex.Message); }
        finally { _uploadLock.Release(); }
    });

    private async Task SendClipFileAsync(SecureChannel ch, string fullPath, string rel)
    {
        using var fs = File.OpenRead(fullPath);
        await ch.SendAsync(Fs.ClipFile(rel, fs.Length), _cts.Token);
        var buf = new byte[Fs.ChunkSize];
        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(0, buf.Length), _cts.Token)) > 0)
            await ch.SendAsync(Fs.UploadChunk(buf, n), _cts.Token);
        await ch.SendAsync(Fs.UploadEnd(), _cts.Token);
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
        await _uploadLock.WaitAsync();
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
        finally { _uploadLock.Release(); }
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
        await _uploadLock.WaitAsync();
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
        finally { _uploadLock.Release(); }
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
        try { _decoder?.Dispose(); } catch { }
        _decoder = null;
        _audio.Dispose();
        _channel?.Dispose();
        _tcp?.Dispose();
        _cts.Dispose();
    }
}
