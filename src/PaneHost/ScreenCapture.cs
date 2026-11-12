using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Pane.Protocol;

namespace PaneHost;

/// <summary>A changed region captured as raw BGRA32 (stride = W*4), awaiting JPEG encoding.</summary>
public readonly record struct RawTile(int X, int Y, int W, int H, byte[] Bgra, bool Large);

/// <summary>
/// Captures the primary monitor with the Desktop Duplication API and returns only the changed regions
/// as raw BGRA tiles (encoded to JPEG by the caller). Rebuilds the duplication object automatically when
/// access is lost (resolution change, UAC/secure desktop, session switch).
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int ScreenX { get; private set; }
    public int ScreenY { get; private set; }

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGIOutputDuplication _dupl = null!;
    private ID3D11Texture2D _staging = null!;
    private bool _needFullFrame = true;
    private bool _needReinit;
    private bool _loggedLost;

    /// <summary>Index (into <see cref="ListMonitors"/>) of the display being captured.</summary>
    public int MonitorIndex { get; private set; } = -1;
    /// <summary>Bumped every time the capture target is (re)built, so the session can resend ScreenInfo.</summary>
    public int Generation { get; private set; }
    private volatile int _requestedMonitor = -1;
    private bool _haveStaging;

    /// <summary>All attached displays, in a stable order (adapter, then output).</summary>
    public static List<MonitorInfo> ListMonitors()
    {
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
        {
            using (adapter)
            {
                for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        if (!d.AttachedToDesktop) continue;
                        var c = d.DesktopCoordinates;
                        list.Add(new MonitorInfo(c.Left, c.Top, c.Right - c.Left, c.Bottom - c.Top, c.Left == 0 && c.Top == 0));
                    }
                }
            }
        }
        return list;
    }

    /// <summary>Ask the capture thread to switch displays (applied on its next capture call).</summary>
    public void SelectMonitor(int index) => _requestedMonitor = index;

    public void Initialize() => Initialize(MonitorIndex);

    private void Initialize(int wanted)
    {
        DisposeDevice();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // Walk outputs in the same order as ListMonitors; take the wanted one, else the primary.
        IDXGIAdapter1? chosenAdapter = null;
        IDXGIOutput1? chosenOutput = null;
        int chosenIndex = -1, idx = 0;
        bool exact = false;
        for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
        {
            for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
            {
                var desc = output.Description;
                if (desc.AttachedToDesktop)
                {
                    bool isPrimary = desc.DesktopCoordinates.Left == 0 && desc.DesktopCoordinates.Top == 0;
                    bool take = !exact && (idx == wanted || isPrimary || chosenOutput == null);
                    if (take)
                    {
                        chosenOutput?.Dispose();
                        if (chosenAdapter != null && !ReferenceEquals(chosenAdapter, adapter)) chosenAdapter.Dispose();
                        chosenOutput = output.QueryInterface<IDXGIOutput1>();
                        chosenAdapter = adapter;
                        chosenIndex = idx;
                        exact = idx == wanted;
                    }
                    idx++;
                }
                output.Dispose();
            }
            if (!ReferenceEquals(chosenAdapter, adapter)) adapter.Dispose();
        }
        if (chosenAdapter == null || chosenOutput == null)
            throw new InvalidOperationException("No attached display output found.");
        MonitorIndex = chosenIndex;

        D3D11.D3D11CreateDevice(chosenAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out _device!, out _context!).CheckError();

        var odesc = chosenOutput.Description.DesktopCoordinates;
        ScreenX = odesc.Left;
        ScreenY = odesc.Top;
        Width = odesc.Right - odesc.Left;
        Height = odesc.Bottom - odesc.Top;

        _dupl = chosenOutput.DuplicateOutput(_device);

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });

        _needFullFrame = true;
        _needReinit = false;
        _loggedLost = false;
        _haveStaging = false;
        Generation++;
        chosenOutput.Dispose();
        chosenAdapter.Dispose();
    }

    /// <summary>Apply a pending monitor switch or a lost-device rebuild. Returns false while the display is unavailable.</summary>
    private bool EnsureReady()
    {
        int req = _requestedMonitor;
        if (req >= 0)
        {
            _requestedMonitor = -1;
            if (req != MonitorIndex)
            {
                int prev = MonitorIndex;
                try { Initialize(req); Log.Info($"Switched capture to monitor {MonitorIndex} ({Width}x{Height})."); }
                catch (Exception ex) { Log.Info($"Monitor switch failed: {ex.Message}"); MonitorIndex = prev; _needReinit = true; }
            }
        }
        if (_needReinit)
        {
            try { Initialize(MonitorIndex); }
            catch
            {
                if (!_loggedLost) { Log.Info("Display unavailable (UAC/secure desktop or switch); waiting to resume..."); _loggedLost = true; }
                Thread.Sleep(150);
                return false;
            }
            Log.Info("Display reacquired; resuming capture.");
        }
        return true;
    }

    /// <summary>
    /// H.264 path: wait up to <paramref name="timeoutMs"/> for a new desktop image and hand the whole frame
    /// (mapped BGRA pointer, row pitch) to <paramref name="consume"/>. When a full frame is forced but the
    /// screen is idle, re-delivers the last captured image so a keyframe can still be produced.
    /// Returns false if nothing was delivered.
    /// </summary>
    public bool CaptureFull(int timeoutMs, Action<IntPtr, int> consume)
    {
        if (!EnsureReady()) return false;
        try
        {
            var result = _dupl.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            {
                if (_needFullFrame && _haveStaging) { _needFullFrame = false; MapAndConsume(consume); return true; }
                return false;
            }
            if (result == Vortice.DXGI.ResultCode.AccessLost) { _needReinit = true; return false; }
            result.CheckError();
            try
            {
                bool hasNewImage = desktopResource != null && frameInfo.LastPresentTime != 0;
                if (!hasNewImage)
                {
                    if (_needFullFrame && _haveStaging) { _needFullFrame = false; MapAndConsume(consume); return true; }
                    return false;
                }
                using var tex = desktopResource!.QueryInterface<ID3D11Texture2D>();
                _context.CopyResource(_staging, tex);
                _haveStaging = true;
                _needFullFrame = false;
                MapAndConsume(consume);
                return true;
            }
            finally
            {
                desktopResource?.Dispose();
                _dupl.ReleaseFrame();
            }
        }
        catch (Exception)
        {
            _needReinit = true;
            return false;
        }
    }

    private void MapAndConsume(Action<IntPtr, int> consume)
    {
        var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try { consume(map.DataPointer, (int)map.RowPitch); }
        finally { _context.Unmap(_staging, 0); }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a new frame and returns the changed regions as raw
    /// BGRA tiles (not yet JPEG-encoded, so the caller can encode on a worker thread while this captures
    /// the next frame). Empty if nothing changed. Recoverable access loss is handled by re-initializing.
    /// </summary>
    public List<RawTile> CaptureRaw(int timeoutMs)
    {
        // Rebuild after a desktop switch (UAC secure desktop, lock screen, resolution change) or apply a
        // monitor switch. While the secure desktop is up we return no changes and the client holds its
        // last frame; capture resumes automatically when the normal desktop returns.
        if (!EnsureReady()) return new List<RawTile>();

        try
        {
        var result = _dupl.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            // Idle screen but a full frame was requested: resend the last image we have.
            if (_needFullFrame && _haveStaging)
            {
                _needFullFrame = false;
                return ExtractRaw(new List<Vortice.RawRect> { new(0, 0, Width, Height) });
            }
            return new List<RawTile>();
        }
        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            _needReinit = true;
            return new List<RawTile>();
        }
        result.CheckError();

        try
        {
            // A cursor-only update (mouse moved but the desktop image is unchanged) returns success with
            // no usable desktop image: LastPresentTime == 0, and the resource may be null. There is nothing
            // new to send, so skip it. Without this guard, dereferencing the resource threw NullReference.
            bool hasNewImage = desktopResource != null && frameInfo.LastPresentTime != 0;
            if (!hasNewImage && !_needFullFrame)
                return new List<RawTile>();
            if (desktopResource == null)
                return new List<RawTile>();

            using var tex = desktopResource.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(_staging, tex);
            _haveStaging = true;

            var rects = new List<Vortice.RawRect>();
            if (_needFullFrame)
            {
                rects.Add(new Vortice.RawRect(0, 0, Width, Height));
                _needFullFrame = false;
            }
            else
            {
                CollectDirtyRects(frameInfo, rects);
                if (rects.Count == 0) return new List<RawTile>();
                // Many rects (e.g. dragging a window) are cheaper and smoother to send as one region
                // than as dozens of tiny JPEGs.
                if (rects.Count > 8)
                    rects = new List<Vortice.RawRect> { BoundingBox(rects) };
            }

            return ExtractRaw(rects);
        }
        finally
        {
            desktopResource?.Dispose();
            _dupl.ReleaseFrame();
        }
        }
        catch (Exception)
        {
            // Any device/duplication failure (device removed, desktop switch mid-call): rebuild next time
            // instead of killing the session.
            _needReinit = true;
            return new List<RawTile>();
        }
    }

    private Vortice.RawRect BoundingBox(List<Vortice.RawRect> rects)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var r in rects)
        {
            left = Math.Min(left, r.Left); top = Math.Min(top, r.Top);
            right = Math.Max(right, r.Right); bottom = Math.Max(bottom, r.Bottom);
        }
        return new Vortice.RawRect(Math.Clamp(left, 0, Width), Math.Clamp(top, 0, Height),
                                   Math.Clamp(right, 0, Width), Math.Clamp(bottom, 0, Height));
    }

    private void CollectDirtyRects(OutduplFrameInfo info, List<Vortice.RawRect> rects)
    {
        uint bufBytes = info.TotalMetadataBufferSize;
        if (bufBytes == 0) return;

        // Move rects: repaint their destination.
        int moveStride = System.Runtime.InteropServices.Marshal.SizeOf<OutduplMoveRect>();
        var moveBuf = new OutduplMoveRect[bufBytes / (uint)moveStride + 1];
        _dupl.GetFrameMoveRects((uint)(moveBuf.Length * moveStride), moveBuf, out uint moveBytes);
        int moveCount = (int)(moveBytes / (uint)moveStride);
        for (int i = 0; i < moveCount; i++)
            rects.Add(moveBuf[i].DestinationRect);

        var dirtyBuf = new Vortice.RawRect[bufBytes / 16 + 1];
        _dupl.GetFrameDirtyRects((uint)(dirtyBuf.Length * 16), dirtyBuf, out uint dirtyBytes);
        int dirtyCount = (int)(dirtyBytes / 16);
        for (int i = 0; i < dirtyCount; i++)
            rects.Add(dirtyBuf[i]);
    }

    private unsafe List<RawTile> ExtractRaw(List<Vortice.RawRect> rects)
    {
        var output = new List<RawTile>(rects.Count);
        var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* basePtr = (byte*)map.DataPointer;
            int pitch = (int)map.RowPitch;
            foreach (var r in rects)
            {
                int x = Math.Clamp(r.Left, 0, Width);
                int y = Math.Clamp(r.Top, 0, Height);
                int w = Math.Clamp(r.Right, 0, Width) - x;
                int h = Math.Clamp(r.Bottom, 0, Height) - y;
                if (w <= 0 || h <= 0) continue;

                int rowBytes = w * 4;
                var packed = new byte[rowBytes * h];
                fixed (byte* dst = packed)
                {
                    for (int row = 0; row < h; row++)
                    {
                        byte* srcRow = basePtr + (long)(y + row) * pitch + (long)x * 4;
                        Buffer.MemoryCopy(srcRow, dst + (long)row * rowBytes, rowBytes, rowBytes);
                    }
                }
                // Big (moving) regions encode at lower quality (faster + smaller); flag that here so the
                // worker thread can pick the quality. Copied out of the GPU map, so the staging texture is
                // free for the next capture the moment Unmap returns.
                bool large = (long)w * h > (long)Width * Height / 4;
                // Split tall regions into horizontal strips so the caller can JPEG-encode them on several
                // cores at once. A single GDI+ encode of a full screen is the main 60fps bottleneck.
                int strips = h >= 256 ? Math.Min(Environment.ProcessorCount, Math.Min(8, h / 64)) : 1;
                if (strips <= 1) { output.Add(new RawTile(x, y, w, h, packed, large)); continue; }
                int baseH = h / strips;
                for (int si = 0; si < strips; si++)
                {
                    int sy = si * baseH;
                    int sh = si == strips - 1 ? h - sy : baseH;
                    var part = new byte[rowBytes * sh];
                    Buffer.BlockCopy(packed, sy * rowBytes, part, 0, part.Length);
                    output.Add(new RawTile(x, y + sy, w, sh, part, large));
                }
            }
        }
        finally { _context.Unmap(_staging, 0); }
        return output;
    }

    /// <summary>JPEG-encode one raw tile. Pure/CPU-only, so it can run on a worker thread.</summary>
    public static FrameRect Encode(RawTile t)
    {
        var qual = t.Large ? JpegLow : JpegHigh;
        return new FrameRect(t.X, t.Y, t.W, t.H, EncodeJpeg(t.Bgra, t.W, t.H, qual));
    }

    // JPEG is ~10-20x smaller than raw/Deflated BGRA and far faster to move, which is what keeps big
    // changes (dragging/maximizing a window) smooth instead of choking the link.
    private static readonly System.Drawing.Imaging.ImageCodecInfo JpegCodec =
        Array.Find(System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders(), c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)!;
    private static volatile System.Drawing.Imaging.EncoderParameters JpegHigh = MakeJpegParams(88L);
    private static volatile System.Drawing.Imaging.EncoderParameters JpegLow = MakeJpegParams(60L);

    /// <summary>Client-chosen JPEG quality (1-100) for static and moving regions.</summary>
    public static void SetQuality(int high, int low)
    {
        JpegHigh = MakeJpegParams(Math.Clamp(high, 10, 100));
        JpegLow = MakeJpegParams(Math.Clamp(low, 10, 100));
    }

    private static System.Drawing.Imaging.EncoderParameters MakeJpegParams(long quality)
    {
        var p = new System.Drawing.Imaging.EncoderParameters(1);
        p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        return p;
    }

    private static unsafe byte[] EncodeJpeg(byte[] bgra, int w, int h, System.Drawing.Imaging.EncoderParameters qual)
    {
        fixed (byte* p = bgra)
        {
            using var bmp = new System.Drawing.Bitmap(w, h, w * 4,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb, (IntPtr)p);
            using var ms = new MemoryStream();
            bmp.Save(ms, JpegCodec, qual);
            return ms.ToArray();
        }
    }

    public void ForceFullFrame() => _needFullFrame = true;

    private void DisposeDevice()
    {
        _staging?.Dispose();
        _dupl?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    public void Dispose() => DisposeDevice();
}
