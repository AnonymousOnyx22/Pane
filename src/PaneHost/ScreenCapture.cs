using System.IO.Compression;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Pane.Protocol;

namespace PaneHost;

/// <summary>
/// Captures the primary monitor with the Desktop Duplication API and returns only the changed
/// rectangles, each as Deflate-compressed BGRA32. Rebuilds the duplication object automatically when
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

    public void Initialize()
    {
        DisposeDevice();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        IDXGIAdapter1? chosenAdapter = null;
        IDXGIOutput1? chosenOutput = null;
        for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
        {
            for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
            {
                var desc = output.Description;
                var o1 = output.QueryInterface<IDXGIOutput1>();
                bool isPrimary = desc.DesktopCoordinates.Left == 0 && desc.DesktopCoordinates.Top == 0;
                if (desc.AttachedToDesktop && (chosenOutput == null || isPrimary))
                {
                    chosenOutput?.Dispose();
                    chosenAdapter?.Dispose();
                    chosenOutput = o1;
                    chosenAdapter = adapter;
                    if (isPrimary) { goto found; }
                }
                else { o1.Dispose(); }
                output.Dispose();
            }
            if (chosenAdapter != adapter) adapter.Dispose();
        }
    found:
        if (chosenAdapter == null || chosenOutput == null)
            throw new InvalidOperationException("No attached display output found.");

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
        chosenOutput.Dispose();
        chosenAdapter.Dispose();
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a new frame. Returns the changed rectangles, or an
    /// empty list if nothing pixel-changed within the timeout. Throws only on unexpected failures;
    /// recoverable access loss is handled internally by re-initializing.
    /// </summary>
    public List<FrameRect> Capture(int timeoutMs)
    {
        // Rebuild the duplication after a desktop switch (UAC secure desktop, lock screen, resolution
        // change, session reconnect). While the secure desktop is up, DuplicateOutput keeps failing, so we
        // just keep retrying on later calls and return no changes — the client holds its last frame and the
        // session stays alive, then resumes automatically when the normal desktop returns.
        if (_needReinit)
        {
            try { Initialize(); }
            catch
            {
                if (!_loggedLost) { Log.Info("Display unavailable (UAC/secure desktop or switch); waiting to resume..."); _loggedLost = true; }
                Thread.Sleep(150);
                return new List<FrameRect>();
            }
            Log.Info("Display reacquired; resuming capture.");
        }

        try
        {
        var result = _dupl.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return new List<FrameRect>();
        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            _needReinit = true;
            return new List<FrameRect>();
        }
        result.CheckError();

        try
        {
            // A cursor-only update (mouse moved but the desktop image is unchanged) returns success with
            // no usable desktop image: LastPresentTime == 0, and the resource may be null. There is nothing
            // new to send, so skip it. Without this guard, dereferencing the resource threw NullReference.
            bool hasNewImage = desktopResource != null && frameInfo.LastPresentTime != 0;
            if (!hasNewImage && !_needFullFrame)
                return new List<FrameRect>();
            if (desktopResource == null)
                return new List<FrameRect>();

            using var tex = desktopResource.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(_staging, tex);

            var rects = new List<Vortice.RawRect>();
            if (_needFullFrame)
            {
                rects.Add(new Vortice.RawRect(0, 0, Width, Height));
                _needFullFrame = false;
            }
            else
            {
                CollectDirtyRects(frameInfo, rects);
                if (rects.Count == 0) return new List<FrameRect>();
                // Many rects (e.g. dragging a window) are cheaper and smoother to send as one region
                // than as dozens of tiny JPEGs.
                if (rects.Count > 8)
                    rects = new List<Vortice.RawRect> { BoundingBox(rects) };
            }

            return ExtractAndCompress(rects);
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
            return new List<FrameRect>();
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

    private unsafe List<FrameRect> ExtractAndCompress(List<Vortice.RawRect> rects)
    {
        var output = new List<FrameRect>(rects.Count);
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
                output.Add(new FrameRect(x, y, w, h, EncodeJpeg(packed, w, h)));
            }
        }
        finally { _context.Unmap(_staging, 0); }
        return output;
    }

    // JPEG is ~10-20x smaller than raw/Deflated BGRA and far faster to move, which is what keeps big
    // changes (dragging/maximizing a window) smooth instead of choking the link.
    private static readonly System.Drawing.Imaging.ImageCodecInfo JpegCodec =
        Array.Find(System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders(), c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)!;
    private static readonly System.Drawing.Imaging.EncoderParameters JpegParams = MakeJpegParams(80L);

    private static System.Drawing.Imaging.EncoderParameters MakeJpegParams(long quality)
    {
        var p = new System.Drawing.Imaging.EncoderParameters(1);
        p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        return p;
    }

    private static unsafe byte[] EncodeJpeg(byte[] bgra, int w, int h)
    {
        fixed (byte* p = bgra)
        {
            using var bmp = new System.Drawing.Bitmap(w, h, w * 4,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb, (IntPtr)p);
            using var ms = new MemoryStream();
            bmp.Save(ms, JpegCodec, JpegParams);
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
