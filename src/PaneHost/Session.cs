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

            var captureLoop = Task.Run(() => CaptureLoop(channel, capture, ct), ct);
            var inputLoop = Task.Run(() => InputLoop(channel, injector, capture, ct), ct);
            await Task.WhenAny(captureLoop, inputLoop);
        }
        Log.Info($"Client {_remote} disconnected.");
    }

    private static async Task CaptureLoop(SecureChannel channel, ScreenCapture capture, CancellationToken ct)
    {
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / 60); // cap at ~60fps
        while (!ct.IsCancellationRequested)
        {
            var start = DateTime.UtcNow;
            List<FrameRect> rects;
            try { rects = capture.Capture(100); }
            catch (Exception ex) { Log.Info($"Capture error: {ex.Message}"); break; }

            if (rects.Count > 0)
                await channel.SendAsync(Messages.Frame(rects), ct);

            var elapsed = DateTime.UtcNow - start;
            if (elapsed < frameInterval)
                await Task.Delay(frameInterval - elapsed, ct);
        }
    }

    private static async Task InputLoop(SecureChannel channel, InputInjector injector, ScreenCapture capture, CancellationToken ct)
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
            }
        }
    }

    public void Dispose() => _client.Dispose();
}
