using System.Net;
using System.Net.Sockets;
using Pane.Protocol;

namespace PaneHost;

/// <summary>Periodically broadcasts a UDP presence packet so Pane can find this host on the LAN.</summary>
public sealed class DiscoveryBroadcaster
{
    private readonly DiscoveryPacket _packet;

    public DiscoveryBroadcaster(Guid hostId, int tcpPort, string name)
        => _packet = new DiscoveryPacket(hostId, tcpPort, name);

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        var data = _packet.Encode();
        var target = new IPEndPoint(IPAddress.Broadcast, Net.DiscoveryPort);
        Log.Info($"Broadcasting presence as '{_packet.Name}' on UDP {Net.DiscoveryPort}.");
        while (!ct.IsCancellationRequested)
        {
            try { await udp.SendAsync(data, data.Length, target); }
            catch (Exception ex) { Log.Info($"Broadcast error: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch { break; }
        }
    }
}
