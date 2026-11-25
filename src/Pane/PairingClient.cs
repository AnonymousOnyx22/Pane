using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Pane.Protocol;

namespace Pane;

/// <summary>
/// First-time pairing without typing: asks the host (on its pairing port) to allow this PC, and waits for
/// the person at that PC to click Allow. On success the host's pairing key comes back for storage.
/// </summary>
public static class PairingClient
{
    public static async Task<(string? Key, string Error)> PairAsync(string host, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            await tcp.ConnectAsync(host, Net.PairPort, cts.Token).ConfigureAwait(false);
            using var ch = await SecureChannel.ClientHandshakeAsync(tcp.GetStream(), PairingKey.OpenPairingSecret, cts.Token)
                .ConfigureAwait(false);
            await ch.SendAsync(Messages.PairRequest(Environment.MachineName), cts.Token).ConfigureAwait(false);
            var reply = await ch.ReceiveAsync(cts.Token).ConfigureAwait(false);
            if (reply == null) return (null, "That PC closed the connection.");
            switch (Messages.TypeOf(reply))
            {
                case MsgType.PairAccept:
                    var key = Messages.ReadText(reply);
                    return PairingKey.TryDecode(key, out _) ? (key, "") : (null, "That PC sent an invalid key.");
                case MsgType.PairDeny:
                    return (null, "That PC said no (or nobody answered in time).");
                default:
                    return (null, "Unexpected reply from that PC.");
            }
        }
        catch (OperationCanceledException) { return (null, "Pairing timed out."); }
        catch (SocketException)
        {
            return (null, "Can't reach pairing on that PC. Its Pane host may be an older version; update it once from that PC.");
        }
        catch (Exception ex) { return (null, "Pairing failed: " + ex.Message); }
    }
}
