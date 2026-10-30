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
bool measure = args.Length > 3 && args[3].Equals("measure", StringComparison.OrdinalIgnoreCase);

var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

try
{
    Console.WriteLine($"Connecting to {host}:{port} ...");
    using var tcp = new TcpClient { NoDelay = true };
    await tcp.ConnectAsync(host, port, timeout);

    var hsClock = Stopwatch.StartNew();
    using var channel = await SecureChannel.ClientHandshakeAsync(tcp.GetStream(), key, timeout);
    Console.WriteLine($"PASS: encrypted handshake (pairing key accepted) in {hsClock.ElapsedMilliseconds} ms.");

    var first = await channel.ReceiveAsync(timeout);
    if (first == null || Messages.TypeOf(first) != MsgType.ScreenInfo)
    {
        Console.WriteLine("FAIL: expected ScreenInfo first.");
        return 1;
    }
    var (w, h) = Messages.ReadScreenInfo(first);
    Console.WriteLine($"PASS: screen info received: {w}x{h}.");
    if (w <= 0 || h <= 0) { Console.WriteLine("FAIL: bad screen dimensions."); return 1; }

    if (measure)
    {
        // Passive measurement: no input is sent, so the remote desktop is not disturbed. Measures
        // time-to-first-frame and sustained throughput/frame rate over ~6 seconds of real activity.
        await channel.SendAsync(Messages.RequestFullFrame(), timeout);
        var clock = Stopwatch.StartNew();
        long firstFrameMs = -1, mFrames = 0, mBytes = 0, mRects = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(6))
        {
            var m = await channel.ReceiveAsync(timeout);
            if (m == null) { Console.WriteLine("Session closed during measurement."); break; }
            if (Messages.TypeOf(m) != MsgType.Frame) continue;
            if (firstFrameMs < 0) firstFrameMs = clock.ElapsedMilliseconds;
            mFrames++; mBytes += m.Length;
            mRects += Messages.ReadFrame(m).Count;
        }
        double secs = clock.Elapsed.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine("=== RESPONSE MEASUREMENT ===");
        Console.WriteLine($"Time to first frame : {firstFrameMs} ms");
        Console.WriteLine($"Frames in {secs:N1}s     : {mFrames}  ({mFrames / secs:N1} fps)");
        Console.WriteLine($"Updated regions     : {mRects}");
        Console.WriteLine($"Data received       : {mBytes / 1024.0 / 1024.0:N2} MiB  ({mBytes / 1024.0 / 1024.0 / secs:N2} MiB/s)");
        Console.WriteLine($"Avg per frame       : {(mFrames > 0 ? mBytes / mFrames / 1024.0 : 0):N1} KiB");
        return 0;
    }

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
            // Frames are JPEG now: verify the magic bytes (FFD8 ... FFD9).
            var j = r.Compressed;
            if (j.Length < 4 || j[0] != 0xFF || j[1] != 0xD8 || j[^2] != 0xFF || j[^1] != 0xD9)
            {
                var head = string.Join(" ", j.Take(8).Select(b => b.ToString("X2")));
                var tail = string.Join(" ", j.Skip(Math.Max(0, j.Length - 8)).Select(b => b.ToString("X2")));
                Console.WriteLine($"FAIL: rect {r.W}x{r.H} not valid JPEG ({j.Length} bytes). head=[{head}] tail=[{tail}]");
                File.WriteAllBytes("frame-dump.bin", j);
                return 1;
            }
            totalPixels += (long)r.W * r.H * 4;
        }
        if (frames >= 1 && totalRects >= 1) break;
    }

    if (frames == 0) { Console.WriteLine("FAIL: no frames received in 8s."); return 1; }
    Console.WriteLine($"PASS: received {frames} frame(s), {totalRects} rect(s), " +
                      $"{totalCompressed / 1024.0:N0} KiB JPEG -> {totalPixels / 1024.0 / 1024.0:N1} MiB pixels " +
                      $"(valid JPEG).");

    // Benign input: move the pointer to screen center. Proves SendInput path works end to end.
    await channel.SendAsync(Messages.MouseMove(0.5f, 0.5f), timeout);
    Console.WriteLine("PASS: input message sent (mouse move to center).");

    // Soak: send cursor-only updates while draining frames for 5s. This is the case that used to crash
    // the host capture loop (cursor-only acquire with no desktop image). The session must stay alive.
    var soak = Stopwatch.StartNew();
    int soakFrames = 0;
    var mover = Task.Run(async () =>
    {
        var rnd = new Random();
        while (soak.Elapsed < TimeSpan.FromSeconds(5))
        {
            await channel.SendAsync(Messages.MouseMove((float)rnd.NextDouble(), (float)rnd.NextDouble()), timeout);
            await Task.Delay(120);
        }
    });
    while (soak.Elapsed < TimeSpan.FromSeconds(5))
    {
        var m = await channel.ReceiveAsync(timeout);
        if (m == null) { Console.WriteLine("FAIL: host dropped the session during mouse activity."); return 1; }
        if (Messages.TypeOf(m) == MsgType.Frame) soakFrames++;
    }
    await mover;
    Console.WriteLine($"PASS: session survived 5s of mouse activity ({soakFrames} frame(s), no capture crash).");

    Console.WriteLine();
    Console.WriteLine("ALL PASS");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("FAIL: " + ex.Message);
    return 1;
}

