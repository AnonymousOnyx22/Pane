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
bool filesTest = args.Length > 3 && args[3].Equals("files", StringComparison.OrdinalIgnoreCase);
bool handoffTest = args.Length > 3 && args[3].Equals("handoff", StringComparison.OrdinalIgnoreCase);

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

    var second = await channel.ReceiveAsync(timeout);
    if (second == null || Messages.TypeOf(second) != MsgType.MonitorList) { Console.WriteLine("FAIL: expected MonitorList after ScreenInfo."); return 1; }
    var (mons, curMon) = Messages.ReadMonitorList(second);
    curMon = Math.Max(0, curMon);
    Console.WriteLine($"PASS: monitor list: {mons.Count} display(s), capturing #{curMon} " +
                      $"({string.Join(", ", mons.Select(m => $"{m.W}x{m.H}{(m.Primary ? "*" : "")}"))}).");

    string mode = args.Length > 3 ? args[3].ToLowerInvariant() : "";

    if (mode == "h264")
    {
        // H.264 path: ask for video, decode every access unit with the same decoder the client uses,
        // and report throughput. Wiggles a small window-free change (a pixel-level cursor move is not
        // captured), so the frame count mostly reflects real screen activity plus the sharpening frames.
        await channel.SendAsync(Messages.SetCodec(Codec.H264), timeout);
        await channel.SendAsync(Messages.RequestFullFrame(), timeout);
        using var dec = new Pane.Media.H264Decoder();
        var clock = Stopwatch.StartNew();
        string encName = "?"; int aus = 0, pics = 0, picW = 0, picH = 0; long bytes = 0, firstMs = -1;
        bool sawIdr = false; double decMs = 0;
        var bgra = new byte[w * h * 4];
        while (clock.Elapsed < TimeSpan.FromSeconds(6))
        {
            var m = await channel.ReceiveAsync(timeout);
            if (m == null) { Console.WriteLine("FAIL: session closed."); return 1; }
            switch (Messages.TypeOf(m))
            {
                case MsgType.CodecInfo:
                    { var (c, n) = Messages.ReadCodecInfo(m); encName = n; if (c != Codec.H264) { Console.WriteLine("FAIL: host fell back to " + n); return 1; } break; }
                case MsgType.VideoFrame:
                    {
                        aus++; bytes += m.Length;
                        if (firstMs < 0) { firstMs = clock.ElapsedMilliseconds; sawIdr = Pane.Media.H264.ContainsNal(m.AsSpan(1), 5); }
                        var t0 = Stopwatch.GetTimestamp();
                        var (n, pw, phh) = VideoProbe.Decode(dec, m, w, h, bgra);
                        pics += n; if (n > 0) { picW = pw; picH = phh; }
                        decMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        break;
                    }
            }
        }
        Console.WriteLine($"Encoder: {encName}");
        Console.WriteLine(firstMs >= 0 ? $"PASS: first video frame after {firstMs} ms (starts with IDR: {sawIdr})." : "FAIL: no video frames.");
        Console.WriteLine(pics > 0 ? $"PASS: decoded {pics}/{aus} access units, picture {picW}x{picH} for a {w}x{h} screen." : "FAIL: nothing decoded.");
        Console.WriteLine($"      {bytes / 1024.0:N0} KiB total, avg decode+convert {(pics > 0 ? decMs / pics : 0):N2} ms/frame.");
        bool ok = firstMs >= 0 && sawIdr && pics > 0 && picW >= w && picH >= h;
        Console.WriteLine(); Console.WriteLine(ok ? "ALL PASS" : "FAILED");
        return ok ? 0 : 1;
    }

    if (mode == "monitor")
    {
        if (mons.Count < 2) { Console.WriteLine("SKIP: only one display."); return 0; }
        bool allOk = true;
        foreach (var codec in new[] { Codec.Jpeg, Codec.H264 })
        {
            await channel.SendAsync(Messages.SetCodec(codec), timeout);
            int target = curMon == 0 ? 1 : 0;
            await channel.SendAsync(Messages.SelectMonitor(target), timeout);
            int sw2 = 0, sh2 = 0, listCur = -1; bool frameAfter = false;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5) && !(sw2 > 0 && listCur == target && frameAfter))
            {
                var m = await channel.ReceiveAsync(timeout);
                if (m == null) break;
                var t = Messages.TypeOf(m);
                if (t == MsgType.ScreenInfo) (sw2, sh2) = Messages.ReadScreenInfo(m);
                else if (t == MsgType.MonitorList) listCur = Messages.ReadMonitorList(m).current;
                else if ((t == MsgType.Frame || t == MsgType.VideoFrame) && sw2 > 0) frameAfter = true;
            }
            var want = mons[target];
            bool ok = sw2 == want.W && sh2 == want.H && listCur == target && frameAfter;
            Console.WriteLine(ok ? $"PASS: [{(codec == Codec.H264 ? "H.264" : "JPEG")}] switched to display #{target} ({sw2}x{sh2}) and frames kept coming."
                                 : $"FAIL: [{codec}] after switch: screen {sw2}x{sh2}, list says #{listCur}, frames={frameAfter}.");
            allOk &= ok;
            curMon = target;
        }
        await channel.SendAsync(Messages.SelectMonitor(0), timeout);
        Console.WriteLine(); Console.WriteLine(allOk ? "ALL PASS" : "FAILED");
        return allOk ? 0 : 1;
    }

    if (mode == "audio")
    {
        // Play a quiet 1 s tone locally while asking the host for its sound; on loopback the host captures
        // that tone and must stream it back.
        await channel.SendAsync(Messages.SetAudio(true), timeout);
        using var tone = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
        var sig = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.03 };
        tone.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(new NAudio.Wave.SampleProviders.OffsetSampleProvider(sig) { Take = TimeSpan.FromSeconds(1.2) }));
        tone.Play();
        var clock = Stopwatch.StartNew();
        int pkts = 0; long pcmBytes = 0; int rate = 0, chans = 0, peak = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            var recv = channel.ReceiveAsync(timeout);
            var m = await recv;
            if (m == null) break;
            if (Messages.TypeOf(m) != MsgType.Audio) continue;
            (rate, chans) = Messages.ReadAudioHeader(m);
            pkts++; pcmBytes += m.Length - 6;
            for (int i = 6; i + 1 < m.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(m, i)));
        }
        await channel.SendAsync(Messages.SetAudio(false), timeout);
        bool ok = pkts > 0 && peak > 100;
        Console.WriteLine(ok ? $"PASS: received {pkts} audio packets, {pcmBytes / 1024} KiB PCM16 @ {rate} Hz x{chans}, peak {peak}."
                             : $"FAIL: audio packets={pkts}, peak={peak}.");
        Console.WriteLine(); Console.WriteLine(ok ? "ALL PASS" : "FAILED");
        return ok ? 0 : 1;
    }

    if (mode == "clipboard")
    {
        // Text: push text to the host, then read this PC's clipboard (same machine on loopback).
        var marker = "pane-clip-test-" + Guid.NewGuid().ToString("N")[..8];
        await channel.SendAsync(Messages.Clipboard(marker), timeout);
        await Task.Delay(500);
        var got = ClipboardProbe.ReadText();
        bool textOk = got == marker;
        Console.WriteLine(textOk ? "PASS: text reached the host clipboard." : $"FAIL: host clipboard has '{got}'.");

        // Files: push a small folder as a clipboard copy; the host must put it on its clipboard as CF_HDROP.
        var rnd = new Random(3); var content = new byte[200_000]; rnd.NextBytes(content);
        await channel.SendAsync(Fs.ClipFile("cliptest\\inner\\a.bin", content.Length), timeout);
        await channel.SendAsync(Fs.UploadChunk(content, content.Length), timeout);
        await channel.SendAsync(Fs.UploadEnd(), timeout);
        await channel.SendAsync(Fs.ClipFilesDone(new[] { "cliptest" }), timeout);
        await Task.Delay(500);
        var files = ClipboardProbe.ReadFiles();
        bool filesOk = files.Count == 1 && File.Exists(Path.Combine(files[0], "inner", "a.bin"))
                       && new FileInfo(Path.Combine(files[0], "inner", "a.bin")).Length == content.Length;
        Console.WriteLine(filesOk ? $"PASS: copied folder is on the host clipboard ({files[0]})." : $"FAIL: clipboard files = [{string.Join(", ", files)}].");
        if (files.Count == 1) try { Directory.Delete(Path.GetDirectoryName(files[0])!, true); } catch { }

        // Image: push a PNG; host must expose it as CF_DIB.
        var png = ClipboardProbe.MakePng(64, 48);
        await channel.SendAsync(Messages.ClipboardImage(png), timeout);
        await Task.Delay(500);
        bool imgOk = ClipboardProbe.HasDib();
        Console.WriteLine(imgOk ? "PASS: image reached the host clipboard (CF_DIB)." : "FAIL: no image on the host clipboard.");

        // Host -> client: copy a folder on "the host" (this PC) and expect it streamed to us as a clipboard
        // copy (HostClipBegin, file stream, HostClipDone). Nothing we pushed above may be echoed back.
        var src = Path.Combine(Path.GetTempPath(), "pane-hostclip-src", "outfolder");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        var payload = new byte[300_000]; new Random(9).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(src, "sub", "b.bin"), payload);
        ClipboardProbe.SetFiles(new[] { src });
        bool began = false, echoed = false; string[]? names = null; long got2 = 0;
        var tEnd = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < tEnd && names == null)
        {
            var m = await channel.ReceiveAsync(timeout);
            if (m == null) break;
            switch (Messages.TypeOf(m))
            {
                case MsgType.HostClipBegin: began = true; break;
                case MsgType.FileChunk: got2 += m.Length - 1; break;
                case MsgType.HostClipDone: names = Fs.ReadNames(m); break;
                case MsgType.Clipboard: case MsgType.ClipboardImage: echoed = true; break;
            }
        }
        try { Directory.Delete(Path.GetDirectoryName(src)!, true); } catch { }
        bool backOk = began && names is { Length: 1 } && names[0] == "outfolder" && got2 == payload.Length && !echoed;
        Console.WriteLine(backOk ? "PASS: folder copied on the host streamed to the client clipboard (no echo of our own pushes)."
                                 : $"FAIL: host->client clip: began={began} names=[{string.Join(",", names ?? Array.Empty<string>())}] bytes={got2} echoed={echoed}.");

        bool ok = textOk && filesOk && imgOk && backOk;
        Console.WriteLine(); Console.WriteLine(ok ? "ALL PASS" : "FAILED");
        return ok ? 0 : 1;
    }

    if (handoffTest)
    {
        // Send two handoff files addressed relative to the profile; the host writes them under ITS
        // profile. (We deliberately do NOT send HandoffFinish, so no VS Code launches during the test.)
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        async Task Push(string rel, byte[] content)
        {
            await channel.SendAsync(Fs.HandoffFile(rel, content.Length), timeout);
            for (int o = 0; o < content.Length; o += Fs.ChunkSize)
            {
                int n = Math.Min(Fs.ChunkSize, content.Length - o);
                var c = new byte[n]; Buffer.BlockCopy(content, o, c, 0, n);
                await channel.SendAsync(Fs.UploadChunk(c, n), timeout);
            }
            await channel.SendAsync(Fs.UploadEnd(), timeout);
        }
        var rnd = new Random(7);
        var c1 = new byte[500000]; rnd.NextBytes(c1);
        var c2 = new byte[1500]; rnd.NextBytes(c2);
        await Push("pane-handoff-test\\src\\a.bin", c1);
        await Push("pane-handoff-test\\notes.txt", c2);
        await Task.Delay(400);
        var p1 = Path.Combine(profile, "pane-handoff-test", "src", "a.bin");
        var p2 = Path.Combine(profile, "pane-handoff-test", "notes.txt");
        bool ok = File.Exists(p1) && File.Exists(p2)
                  && (await File.ReadAllBytesAsync(p1)).Length == c1.Length
                  && (await File.ReadAllBytesAsync(p2)).AsSpan().SequenceEqual(c2);
        try { Directory.Delete(Path.Combine(profile, "pane-handoff-test"), true); } catch { }
        Console.WriteLine(ok ? $"PASS: handoff files landed rebased under {profile} (subfolders preserved)."
                             : "FAIL: handoff files missing/mismatched.");
        Console.WriteLine();
        Console.WriteLine(ok ? "ALL PASS" : "FAILED");
        return ok ? 0 : 1;
    }

    if (filesTest)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pane-files-test");
        Directory.CreateDirectory(tmp);
        var srcPath = Path.Combine(tmp, "sample.bin");
        var rnd = new Random(1234);
        var content = new byte[3 * 1024 * 1024 + 777]; // ~3 MiB, spans several chunks
        rnd.NextBytes(content);
        await File.WriteAllBytesAsync(srcPath, content, timeout);

        // 1. UPLOAD: push the file; host should write it to Desktop\Pane Transfers\looptest\sample.bin
        await channel.SendAsync(Fs.UploadBegin("looptest\\sample.bin", content.Length), timeout);
        for (int off = 0; off < content.Length; off += Fs.ChunkSize)
        {
            int n = Math.Min(Fs.ChunkSize, content.Length - off);
            var chunk = new byte[n];
            Buffer.BlockCopy(content, off, chunk, 0, n);
            await channel.SendAsync(Fs.UploadChunk(chunk, n), timeout);
        }
        await channel.SendAsync(Fs.UploadEnd(), timeout);
        await Task.Delay(400);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var uploaded = Path.Combine(desktop, "Pane Transfers", "looptest", "sample.bin");
        if (File.Exists(uploaded) && (await File.ReadAllBytesAsync(uploaded)).Length == content.Length)
            Console.WriteLine($"PASS: upload landed at {uploaded} ({content.Length / 1024} KiB).");
        else { Console.WriteLine("FAIL: uploaded file missing or wrong size at " + uploaded); return 1; }

        // 2. LIST: list the temp dir; expect to see sample.bin
        await channel.SendAsync(Fs.ListDir(tmp), timeout);
        // 3. DOWNLOAD: pull the file back and reassemble
        await channel.SendAsync(Fs.Download(srcPath), timeout);

        bool listed = false; long dlBytes = 0; bool done = false; MemoryStream? dl = null;
        var tEnd = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < tEnd && !done)
        {
            var m = await channel.ReceiveAsync(timeout);
            if (m == null) break;
            switch (Messages.TypeOf(m))
            {
                case MsgType.DirListing:
                    { var (_, entries) = Fs.ReadListing(m); if (entries.Exists(e => e.Name == "sample.bin")) listed = true; break; }
                case MsgType.FileBegin: dl = new MemoryStream(); break;
                case MsgType.FileChunk: { var s = Fs.ReadChunk(m); dl?.Write(s.Array!, s.Offset, s.Count); break; }
                case MsgType.FileEnd: dlBytes = dl?.Length ?? 0; break;
                case MsgType.TransferDone: done = true; break;
                case MsgType.FsError: Console.WriteLine("FAIL: host FsError: " + Fs.ReadError(m)); return 1;
            }
        }
        Console.WriteLine(listed ? "PASS: directory listing included the file." : "FAIL: listing did not include the file.");
        if (!listed) return 1;
        if (dl != null && dl.ToArray().AsSpan().SequenceEqual(content))
            Console.WriteLine($"PASS: download round-tripped {dlBytes / 1024} KiB byte-for-byte.");
        else { Console.WriteLine($"FAIL: download mismatch ({dlBytes} bytes)."); return 1; }

        Console.WriteLine();
        Console.WriteLine("ALL PASS");
        return 0;
    }

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

