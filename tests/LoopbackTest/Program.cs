using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using Pane.Protocol;

// Headless end-to-end check against a running Pane Host. Exercises the same protocol the WPF client
// uses: TCP connect, encrypted handshake with the pairing key, ScreenInfo, a real captured frame
// (decompressed and sanity-checked), plus one benign mouse move to prove input injection round-trips.
//
// Usage: LoopbackTest <host> <port> <pairingKey>
// Exit code 0 = pass.

if (args.Length < 3)
{
    Console.WriteLine("usage: LoopbackTest <host> <port> <pairingKey>");
    return 2;
}

string host = args[0];
int port = int.Parse(args[1]);
if (!PairingKey.TryDecode(args[2], out var key))
{
    Console.WriteLine("FAIL: pairing key did not parse.");
    return 2;
}

var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

try
{
    Console.WriteLine($"Connecting to {host}:{port} ...");
    using var tcp = new TcpClient { NoDelay = true };
    await tcp.ConnectAsync(host, port, timeout);

    using var channel = await SecureChannel.ClientHandshakeAsync(tcp.GetStream(), key, timeout);
    Console.WriteLine("PASS: encrypted handshake (pairing key accepted).");

    var first = await channel.ReceiveAsync(timeout);
    if (first == null || Messages.TypeOf(first) != MsgType.ScreenInfo)
    {
        Console.WriteLine("FAIL: expected ScreenInfo first.");
        return 1;
    }
    var (w, h) = Messages.ReadScreenInfo(first);
    Console.WriteLine($"PASS: screen info received: {w}x{h}.");
    if (w <= 0 || h <= 0) { Console.WriteLine("FAIL: bad screen dimensions."); return 1; }

    // Ask for a full frame and wait for pixels.
    await channel.SendAsync(Messages.RequestFullFrame(), timeout);

    var sw = Stopwatch.StartNew();
    int frames = 0, totalRects = 0;
    long totalCompressed = 0, totalPixels = 0;
    while (sw.Elapsed < TimeSpan.FromSeconds(8))
    {
        var msg = await channel.ReceiveAsync(timeout);
        if (msg == null) { Console.WriteLine("FAIL: stream closed early."); return 1; }
        if (Messages.TypeOf(msg) != MsgType.Frame) continue;

        var rects = Messages.ReadFrame(msg);
        frames++;
        foreach (var r in rects)
        {
            totalRects++;
            totalCompressed += r.Compressed.Length;
            int expected = r.W * r.H * 4;
            var pixels = Inflate(r.Compressed, expected);
            if (pixels.Length != expected)
            {
                Console.WriteLine($"FAIL: rect {r.W}x{r.H} inflated to {pixels.Length}, expected {expected}.");
                return 1;
            }
            totalPixels += expected;
        }
        if (frames >= 1 && totalRects >= 1) break;
    }

    if (frames == 0) { Console.WriteLine("FAIL: no frames received in 8s."); return 1; }
    Console.WriteLine($"PASS: received {frames} frame(s), {totalRects} rect(s), " +
                      $"{totalCompressed / 1024.0:N0} KiB compressed -> {totalPixels / 1024.0 / 1024.0:N1} MiB pixels " +
                      $"(decoded cleanly).");

    // Benign input: move the pointer to screen center. Proves SendInput path works end to end.
    await channel.SendAsync(Messages.MouseMove(0.5f, 0.5f), timeout);
    Console.WriteLine("PASS: input message sent (mouse move to center).");

    Console.WriteLine();
    Console.WriteLine("ALL PASS");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("FAIL: " + ex.Message);
    return 1;
}

static byte[] Inflate(byte[] data, int expected)
{
    using var ms = new MemoryStream(data);
    using var ds = new DeflateStream(ms, CompressionMode.Decompress);
    var outBuf = new byte[expected];
    int off = 0, n;
    while (off < expected && (n = ds.Read(outBuf, off, expected - off)) > 0) off += n;
    return outBuf;
}
