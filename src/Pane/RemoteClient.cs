using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Pane.Protocol;

namespace Pane;

/// <summary>
/// Client side of one remote session: connects, authenticates with the pairing key, receives frames,
/// and sends input. All events are raised from a background task; the UI marshals them to the
/// dispatcher itself.
/// </summary>
public sealed class RemoteClient : IDisposable
{
    public event Action<int, int>? Connected;              // screen width, height
    public event Action<List<FrameRect>>? FrameReceived;
    public event Action<string>? Disconnected;             // reason

    private TcpClient? _tcp;
    private SecureChannel? _channel;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _down;

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
                if (Messages.TypeOf(msg) == MsgType.Frame)
                    FrameReceived?.Invoke(Messages.ReadFrame(msg));
            }
            Fail("Connection closed.");
        }
        catch (OperationCanceledException) { Fail("Disconnected."); }
        catch (Exception ex) { Fail(ex.Message); }
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

    public void Dispose()
    {
        _down = true;
        try { _cts.Cancel(); } catch { }
        _channel?.Dispose();
        _tcp?.Dispose();
        _cts.Dispose();
    }
}
