using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Pane.Protocol;

namespace PaneHost;

/// <summary>
/// Periodically broadcasts a UDP presence packet so Pane can find this host on the LAN.
/// Sends from each real IPv4 adapter (bound to that adapter's address) so the packet's source IP is a
/// routable LAN address rather than whatever the default route happens to be — a secondary or virtual
/// adapter with a link-local (169.254.x.x) address would otherwise advertise an unreachable IP.
/// </summary>
public sealed class DiscoveryBroadcaster
{
    private readonly DiscoveryPacket _packet;

    public DiscoveryBroadcaster(Guid hostId, int tcpPort, string name)
        => _packet = new DiscoveryPacket(hostId, tcpPort, name);

    public async Task RunAsync(CancellationToken ct)
    {
        var data = _packet.Encode();
        var target = new IPEndPoint(IPAddress.Broadcast, Net.DiscoveryPort);
        Log.Info($"Broadcasting presence as '{_packet.Name}' on UDP {Net.DiscoveryPort}.");

        while (!ct.IsCancellationRequested)
        {
            foreach (var local in LocalBroadcastAddresses())
            {
                try
                {
                    using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                    await udp.SendAsync(data, data.Length, target);
                }
                catch { /* adapter may have gone away; try the rest */ }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch { break; }
        }
    }

    private static IEnumerable<IPAddress> LocalBroadcastAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 127) continue;               // loopback
                if (bytes[0] == 169 && bytes[1] == 254) continue; // link-local / APIPA
                yield return ua.Address;
            }
        }
    }
}
