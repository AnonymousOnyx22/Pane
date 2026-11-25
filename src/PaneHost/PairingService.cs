using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Pane.Protocol;

namespace PaneHost;

/// <summary>
/// Lets a new PC pair without anyone typing a key. A client connects to <see cref="Net.PairPort"/>, says
/// who it is, and the person at this PC sees "Allow this PC to control you?". On Allow the client is
/// handed the pairing key over the (encrypted) channel and stores it; after that it connects normally.
/// Only one prompt is shown at a time and a denied PC is ignored for a short while.
/// </summary>
public sealed class PairingService
{
    private readonly HostConfig _cfg;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _coolDown = new();

    public PairingService(HostConfig cfg) => _cfg = cfg;

    public async Task RunAsync(CancellationToken ct)
    {
        TcpListener l;
        try { l = new TcpListener(IPAddress.Any, Net.PairPort); l.Start(); }
        catch (Exception ex) { Log.Info($"Pairing listener could not start: {ex.Message}"); return; }
        Log.Info($"Listening for pairing requests on TCP {Net.PairPort}.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var c = await l.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleAsync(c, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { l.Stop(); }
    }

    private async Task HandleAsync(TcpClient c, CancellationToken ct)
    {
        using var _ = c;
        string ip = (c.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        try
        {
            if (_coolDown.TryGetValue(ip, out var until) && until > DateTime.UtcNow) return;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            using var ch = await SecureChannel.ServerHandshakeAsync(c.GetStream(), PairingKey.OpenPairingSecret, cts.Token);
            var req = await ch.ReceiveAsync(cts.Token);
            if (req == null || Messages.TypeOf(req) != MsgType.PairRequest) return;
            string name = Clean(Messages.ReadText(req));

            // One prompt at a time: a second request while one is showing is refused outright.
            if (!await _one.WaitAsync(0)) { await ch.SendAsync(Messages.PairDeny(), cts.Token); return; }
            bool allow;
            try
            {
                Log.Info($"Pairing request from '{name}' ({ip}).");
                allow = await Task.Run(() => Ask(name, ip), cts.Token);
            }
            finally { _one.Release(); }

            if (allow)
            {
                Log.Info($"Pairing approved for '{name}' ({ip}).");
                await ch.SendAsync(Messages.PairAccept(_cfg.PairingKey), cts.Token);
            }
            else
            {
                _coolDown[ip] = DateTime.UtcNow.AddSeconds(20);
                Log.Info($"Pairing refused for '{name}' ({ip}).");
                await ch.SendAsync(Messages.PairDeny(), cts.Token);
            }
        }
        catch (Exception ex) { Log.Info($"Pairing with {ip} ended: {ex.Message}"); }
    }

    private static string Clean(string s)
    {
        s = new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (s.Length > 40) s = s[..40];
        return s.Length == 0 ? "Unknown PC" : s;
    }

    /// <summary>Allow/Deny box on this PC's screen (Deny is the default button; no answer in 60s = Deny).</summary>
    private static bool Ask(string name, string ip)
    {
        const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_DEFBUTTON2 = 0x100,
                   MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        int r = MessageBoxTimeoutW(IntPtr.Zero,
            $"\"{name}\" ({ip}) wants to control this PC.\n\nOnly click Allow if that is your PC.",
            "Pane — allow this PC?",
            MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2 | MB_SETFOREGROUND | MB_TOPMOST, 0, 60_000);
        return r == 6; // IDYES
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxTimeoutW")]
    private static extern int MessageBoxTimeoutW(IntPtr hWnd, string text, string caption, uint type, ushort lang, uint ms);
}
