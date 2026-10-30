using System.Net.Sockets;
using Pane.Protocol;

namespace PaneHost;

/// <summary>Handles one connected client: authenticates, streams frames, and injects received input.</summary>
public sealed class Session : IDisposable
{
    private readonly TcpClient _client;
    private readonly byte[] _pairingKey;
    private readonly string _remote;

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
        {
            capture.Initialize();
            var injector = new InputInjector(capture.ScreenX, capture.ScreenY, capture.Width, capture.Height);
            await channel.SendAsync(Messages.ScreenInfo(capture.Width, capture.Height), ct);

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var files = new FileService(channel, sessionCts.Token);
            var captureLoop = Task.Run(() => CaptureLoop(channel, capture, sessionCts.Token), sessionCts.Token);
            var inputLoop = Task.Run(() => InputLoop(channel, injector, capture, files, sessionCts.Token), sessionCts.Token);
            await Task.WhenAny(captureLoop, inputLoop);
            // One loop ended (usually the client disconnected). Stop the other and let both finish before
            // the capture device is disposed, so the capture loop never touches a disposed device.
            sessionCts.Cancel();
            try { await Task.WhenAll(captureLoop, inputLoop); } catch { }
        }
        Log.Info($"Client {_remote} disconnected.");
    }

    private static async Task CaptureLoop(SecureChannel channel, ScreenCapture capture, CancellationToken ct)
    {
        // AcquireNextFrame blocks until the next presented frame (or the timeout), so it paces the loop to
        // the monitor's own refresh. An extra Task.Delay only adds latency, so there isn't one — this keeps
        // motion as smooth as the link allows.
        while (!ct.IsCancellationRequested)
        {
            List<FrameRect> rects;
            try { rects = capture.Capture(200); }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Log.Info($"Capture error: {ex.Message}");
                break;
            }

            if (rects.Count > 0)
                await channel.SendAsync(Messages.Frame(rects), ct);
        }
    }

    private static async Task InputLoop(SecureChannel channel, InputInjector injector, ScreenCapture capture, FileService files, CancellationToken ct)
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
                    capture.ForceFullFrame(); break;

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
            }
        }
    }

    public void Dispose() => _client.Dispose();
}
