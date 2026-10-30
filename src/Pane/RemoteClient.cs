using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Pane.Protocol;

namespace Pane;

/// <summary>
/// Client side of one remote session: connects, authenticates with the pairing key, receives frames,
/// sends input, and handles file transfer (list/download/upload). All events are raised from a
/// background task; the UI marshals them to the dispatcher itself.
/// </summary>
public sealed class RemoteClient : IDisposable
{
    public event Action<int, int>? Connected;              // screen width, height
    public event Action<List<FrameRect>>? FrameReceived;
    public event Action<string>? Disconnected;             // reason

    public event Action<string, List<FsEntry>>? DirListing; // path, entries
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
                FrameReceived?.Invoke(Messages.ReadFrame(msg));
                break;
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
