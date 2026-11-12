using System.Net.Sockets;
using System.Threading.Channels;
using Pane.Media;
using Pane.Protocol;

namespace PaneHost;

/// <summary>
/// Handles one connected client: authenticates, streams the screen (JPEG tiles or H.264, whichever the
/// client asks for), streams system audio on request, syncs the clipboard, and injects received input.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly TcpClient _client;
    private readonly byte[] _pairingKey;
    private readonly string _remote;

    // Settings the client can change mid-session (written by the input loop, read by the capture loop).
    private volatile byte _codec = Codec.Jpeg;
    private volatile bool _keyframe;
    private volatile int _bitrate = 30_000_000;

    public Session(TcpClient client, byte[] pairingKey)
    {
        _client = client;
        _pairingKey = pairingKey;
        _remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"Client connecting from {_remote}");
        _client.NoDelay = true;
        using var stream = _client.GetStream();

        SecureChannel channel;
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsCts.CancelAfter(TimeSpan.FromSeconds(10));
            channel = await SecureChannel.ServerHandshakeAsync(stream, _pairingKey, hsCts.Token);
        }
        catch (Exception ex)
        {
            Log.Info($"Handshake with {_remote} failed: {ex.Message}");
            return;
        }
        Log.Info($"Client {_remote} authenticated.");

        using (channel)
        using (var capture = new ScreenCapture())
        using (var audio = new LoopbackAudio())
        {
            capture.Initialize();
            var injector = new InputInjector(capture.ScreenX, capture.ScreenY, capture.Width, capture.Height);
            await channel.SendAsync(Messages.ScreenInfo(capture.Width, capture.Height), ct);
            await SendMonitorList(channel, capture, ct);

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = sessionCts.Token;
            var files = new FileService(channel, token);
            var clip = new ClipboardSync();

            // Audio: the capture thread queues packets; one sender keeps them in order.
            var audioQueue = System.Threading.Channels.Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            audio.Data += (rate, ch, pcm) => audioQueue.Writer.TryWrite(Messages.Audio(rate, ch, pcm));

            var captureLoop = Task.Run(() => CaptureLoop(channel, capture, injector, token), token);
            var inputLoop = Task.Run(() => InputLoop(channel, injector, capture, files, clip, audio, token), token);
            var clipLoop = Task.Run(() => clip.RunAsync(channel, files, token), token);
            var audioLoop = Task.Run(async () =>
            {
                await foreach (var pkt in audioQueue.Reader.ReadAllAsync(token))
                    await channel.SendAsync(pkt, token);
            }, token);

            await Task.WhenAny(captureLoop, inputLoop);
            // One loop ended (usually the client disconnected). Stop the others and let them finish before
            // the capture device is disposed, so the capture loop never touches a disposed device.
            sessionCts.Cancel();
            audio.Stop();
            try { await Task.WhenAll(captureLoop, inputLoop, clipLoop, audioLoop); } catch { }
        }
        Log.Info($"Client {_remote} disconnected.");
    }

    private static Task SendMonitorList(SecureChannel channel, ScreenCapture capture, CancellationToken ct)
    {
        List<MonitorInfo> mons;
        try { mons = ScreenCapture.ListMonitors(); } catch { mons = new(); }
        return channel.SendAsync(Messages.MonitorList(mons, capture.MonitorIndex), ct);
    }

    private async Task CaptureLoop(SecureChannel channel, ScreenCapture capture, InputInjector injector, CancellationToken ct)
    {
        // Pipelined: this thread captures (and for H.264, converts + encodes) the next frame while a worker
        // sends the previous one. AcquireNextFrame paces to the monitor's refresh; sends stay in order.
        Task sending = Task.CompletedTask;
        int gen = capture.Generation;
        byte active = Codec.Jpeg; // sessions start on JPEG; CodecInfo is only sent on a change
        H264Encoder? enc = null;
        byte[] nv12 = Array.Empty<byte>();
        int refresh = 0, encFailures = 0;
        bool haveFrame = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // --- codec changes ---
                byte codec = _codec;
                if (codec != active)
                {
                    active = codec;
                    enc?.Dispose(); enc = null;
                    capture.ForceFullFrame();
                    if (codec == Codec.Jpeg)
                    {
                        await sending;
                        await channel.SendAsync(Messages.CodecInfo(Codec.Jpeg, "JPEG"), ct);
                    }
                }

                if (codec == Codec.H264)
                {
                    if (_keyframe) { _keyframe = false; enc?.Dispose(); enc = null; }
                    if (enc == null)
                    {
                        try
                        {
                            enc = H264Encoder.Create(capture.Width, capture.Height, 60, _bitrate);
                            nv12 = new byte[Nv12.Size(enc.Width, enc.Height)];
                            haveFrame = false;
                            capture.ForceFullFrame();
                            Log.Info($"H.264 encoder: {enc.Name} at {capture.Width}x{capture.Height}, {_bitrate / 1_000_000} Mbps");
                            await sending;
                            await channel.SendAsync(Messages.CodecInfo(Codec.H264, enc.Name), ct);
                        }
                        catch (Exception ex)
                        {
                            Log.Info($"H.264 unavailable, using JPEG: {ex.Message}");
                            enc = null;
                            _codec = Codec.Jpeg;
                            continue;
                        }
                    }

                    var e = enc;
                    bool mismatch = false;
                    bool got = capture.CaptureFull(refresh > 0 ? 16 : 100, (ptr, pitch) =>
                    {
                        int w = capture.Width, h = capture.Height;
                        if (H264Encoder.Align16(w) != e.Width || H264Encoder.Align16(h) != e.Height) { mismatch = true; return; }
                        ConvertToNv12(ptr, pitch, w, h, nv12, e.Width, e.Height);
                    });

                    if (capture.Generation != gen || mismatch)
                    {
                        gen = capture.Generation;
                        await OnTargetChanged(channel, capture, injector, sending, ct);
                        enc.Dispose(); enc = null;
                        continue;
                    }

                    if (got) { haveFrame = true; refresh = 30; }
                    else if (refresh > 0 && haveFrame) refresh--;   // re-encode the still image so it sharpens
                    else continue;

                    List<byte[]> aus;
                    try { aus = enc.Encode(nv12); encFailures = 0; }
                    catch (Exception ex)
                    {
                        Log.Info($"Encode failed: {ex.Message}");
                        enc.Dispose(); enc = null;
                        if (++encFailures >= 3) _codec = Codec.Jpeg;
                        continue;
                    }
                    if (aus.Count == 0) continue;

                    try { await sending; } catch { break; }
                    sending = Task.Run(async () =>
                    {
                        foreach (var au in aus) await channel.SendAsync(Messages.VideoFrame(au), ct);
                    }, ct);
                }
                else
                {
                    List<RawTile> raw;
                    try { raw = capture.CaptureRaw(200); }
                    catch (Exception ex)
                    {
                        if (!ct.IsCancellationRequested) Log.Info($"Capture error: {ex.Message}");
                        break;
                    }
                    if (capture.Generation != gen)
                    {
                        gen = capture.Generation;
                        await OnTargetChanged(channel, capture, injector, sending, ct);
                        capture.ForceFullFrame();
                        continue; // this frame may predate the size change; the forced full frame follows
                    }
                    if (raw.Count == 0) continue;

                    try { await sending; } catch { break; }
                    sending = Task.Run(async () =>
                    {
                        var rects = raw.AsParallel().AsOrdered().Select(ScreenCapture.Encode).ToList();
                        await channel.SendAsync(Messages.Frame(rects), ct);
                    }, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Log.Info($"Capture loop ended: {ex.Message}"); }
        finally
        {
            try { await sending; } catch { }
            enc?.Dispose();
        }
    }

    /// <summary>The captured display changed (monitor switch or rebuild): retarget input and tell the client.</summary>
    private static async Task OnTargetChanged(SecureChannel channel, ScreenCapture capture, InputInjector injector, Task sending, CancellationToken ct)
    {
        injector.SetTarget(capture.ScreenX, capture.ScreenY, capture.Width, capture.Height);
        try { await sending; } catch { }
        await channel.SendAsync(Messages.ScreenInfo(capture.Width, capture.Height), ct);
        await SendMonitorList(channel, capture, ct);
    }

    private static unsafe void ConvertToNv12(IntPtr src, int pitch, int w, int h, byte[] dst, int dw, int dh)
    {
        fixed (byte* d = dst) Nv12.FromBgra((byte*)src, pitch, w, h, d, dw, dh);
    }

    private async Task InputLoop(SecureChannel channel, InputInjector injector, ScreenCapture capture, FileService files,
                                 ClipboardSync clip, LoopbackAudio audio, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await channel.ReceiveAsync(ct);
            if (msg == null || msg.Length == 0) break;
            switch (Messages.TypeOf(msg))
            {
                case MsgType.MouseMove:
                    { var (nx, ny) = Messages.ReadMouseMove(msg); injector.MouseMove(nx, ny); break; }
                case MsgType.MouseButton:
                    { var (b, d, nx, ny) = Messages.ReadMouseButton(msg); injector.MouseButton(b, d, nx, ny); break; }
                case MsgType.MouseWheel:
                    { var (dl, h, nx, ny) = Messages.ReadMouseWheel(msg); injector.MouseWheel(dl, h, nx, ny); break; }
                case MsgType.Key:
                    { var (vk, d, ext) = Messages.ReadKey(msg); injector.Key(vk, d, ext); break; }
                case MsgType.RequestFullFrame:
                    capture.ForceFullFrame();
                    if (_codec == Codec.H264) _keyframe = true;
                    break;
                case MsgType.SetQuality:
                    {
                        int high = msg[1], low = msg[2];
                        ScreenCapture.SetQuality(high, low);
                        _bitrate = high >= 95 ? 60_000_000 : high >= 85 ? 30_000_000 : 12_000_000;
                        if (_codec == Codec.H264) _keyframe = true; // rebuild the encoder at the new bitrate
                        break;
                    }
                case MsgType.SetCodec:
                    if (msg.Length > 1 && (msg[1] == Codec.Jpeg || msg[1] == Codec.H264)) _codec = msg[1];
                    break;
                case MsgType.SelectMonitor:
                    capture.SelectMonitor(Messages.ReadSelectMonitor(msg)); break;
                case MsgType.SetAudio:
                    if (msg.Length > 1 && msg[1] == 1) audio.Start(); else audio.Stop();
                    break;

                // Clipboard
                case MsgType.Clipboard:
                    clip.ApplyText(Messages.ReadClipboard(msg)); break;
                case MsgType.ClipboardImage:
                    clip.ApplyImage(Messages.Payload(msg)); break;
                case MsgType.ClipFile:
                    { var (rp, sz) = Fs.ReadBegin(msg); files.ClipFile(rp, sz); break; }
                case MsgType.ClipFilesDone:
                    clip.ApplyFiles(files.ClipFilesDone(Fs.ReadNames(msg))); break;

                // File transfer
                case MsgType.ListDir:
                    { var p = Fs.ReadPath(msg); _ = files.ListDirAsync(p); break; }
                case MsgType.Download:
                    { var p = Fs.ReadPath(msg); _ = Task.Run(() => files.DownloadAsync(p)); break; }
                case MsgType.UploadBegin:
                    { var (rp, sz) = Fs.ReadBegin(msg); files.UploadBegin(rp, sz); break; }
                case MsgType.UploadChunk:
                    files.UploadChunk(Fs.ReadChunk(msg)); break;
                case MsgType.UploadEnd:
                    files.UploadEnd(); break;
                case MsgType.HandoffFile:
                    { var (rel, sz) = Fs.ReadBegin(msg); files.HandoffFile(rel, sz); break; }
                case MsgType.HandoffFinish:
                    files.HandoffFinish(Fs.ReadPath(msg)); break;
            }
        }
    }

    public void Dispose() => _client.Dispose();
}
