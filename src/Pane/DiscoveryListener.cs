using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Pane.Protocol;

namespace Pane;

/// <summary>Listens for host UDP presence packets and reports which hosts are visible on the LAN.</summary>
public sealed class DiscoveryListener : IDisposable
{
    public event Action<DiscoveredHost>? HostSeen;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Net.DiscoveryPort));
        _ = ReceiveLoop(_udp, _cts.Token);
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch { continue; }

            if (DiscoveryPacket.TryParse(res.Buffer, out var pkt))
                HostSeen?.Invoke(new DiscoveredHost(pkt.HostId, pkt.Name, res.RemoteEndPoint.Address.ToString(), pkt.TcpPort));
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _udp?.Dispose();
    }
}

public readonly record struct DiscoveredHost(Guid HostId, string Name, string Ip, int TcpPort);
