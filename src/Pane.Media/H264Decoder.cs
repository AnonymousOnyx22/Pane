using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Pane.Media;

/// <summary>
/// Low-latency H.264 decoder over Microsoft's built-in decoder MFT (synchronous, system memory).
/// Feed it one Annex-B access unit at a time; each decoded picture is handed to a callback as a
/// locked NV12 buffer so the caller can convert it in place without another copy.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    /// <summary>Called with (nv12 pointer, luma stride, plane height, picture width, picture height).</summary>
    public delegate void FrameHandler(byte* nv12, int stride, int planeHeight, int width, int height);

    private readonly IMFTransform _mft;
    private int _outW, _outH, _stride, _outSize;
    private bool _providesSamples;
    private long _t;

    public H264Decoder()
    {
        Mf.Startup();
        _mft = Mf.CreateByClsid(Mf.CLSID_MSH264DecoderMFT);
        try { _mft.Attributes.Set(Mf.LOW_LATENCY, 1u); } catch { }
        Mf.SetCodecValue(_mft, Mf.LOW_LATENCY, true);

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(Mf.MT_MAJOR_TYPE, Mf.MediaType_Video);
            inType.Set(Mf.MT_SUBTYPE, Mf.VideoFormat_H264);
            inType.Set(Mf.MT_INTERLACE_MODE, 2u);
            _mft.SetInputType(0, inType, 0);
        }
        SelectOutputType();
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>Pick NV12 output (again after a stream/format change) and refresh the buffer geometry.</summary>
    private void SelectOutputType()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _mft.GetOutputAvailableType(0, i); }
            catch { throw new InvalidOperationException("H.264 decoder offers no NV12 output."); }
            using (t)
            {
                if (t.GetGUID(Mf.MT_SUBTYPE) != Mf.VideoFormat_NV12) continue;
                _mft.SetOutputType(0, t, 0);
                break;
            }
        }
        using (var cur = _mft.GetOutputCurrentType(0))
        {
            ulong fs = 0;
            try { fs = cur.GetUInt64(Mf.MT_FRAME_SIZE); } catch { }
            _outW = (int)(fs >> 32); _outH = (int)(fs & 0xFFFFFFFF);
            _stride = _outW;
            try { int s = (int)cur.GetUInt32(Mf.MT_DEFAULT_STRIDE); if (s > 0) _stride = s; } catch { }
        }
        var info = _mft.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (Mf.OUTPUT_STREAM_PROVIDES_SAMPLES | Mf.OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        _outSize = Math.Max(info.Size, Math.Max(1, _stride * _outH * 3 / 2));
    }

    /// <summary>Decode one access unit. Returns how many pictures were delivered to <paramref name="onFrame"/>.</summary>
    public int Decode(byte[] au, int offset, int count, FrameHandler onFrame)
    {
        var sample = Mf.MakeSample(count, out var buf);
        try
        {
            buf.Lock(out var p, out _, out _);
            System.Runtime.InteropServices.Marshal.Copy(au, offset, p, count);
            buf.Unlock();
            buf.CurrentLength = count;
            sample.SampleTime = _t; sample.SampleDuration = 166_666; _t += 166_666;
            try { _mft.ProcessInput(0, sample, 0); }
            catch (SharpGenException ex) when (ex.HResult == Mf.E_NOTACCEPTING)
            {
                // Output is pending: drain it, then retry the input once.
                int n0 = Drain(onFrame);
                _mft.ProcessInput(0, sample, 0);
                return n0 + Drain(onFrame);
            }
            return Drain(onFrame);
        }
        finally { buf.Dispose(); sample.Dispose(); }
    }

    private int Drain(FrameHandler onFrame)
    {
        int delivered = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            IMFSample? mine = null; IMFMediaBuffer? mineBuf = null;
            if (!_providesSamples) mine = Mf.MakeSample(_outSize, out mineBuf);
            var odb = new OutputDataBuffer { StreamID = 0, Sample = mine! };
            try
            {
                var res = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
                if (res.Code == Mf.E_STREAM_CHANGE) { SelectOutputType(); continue; }
                if (res.Failure) return delivered; // NEED_MORE_INPUT (normal) or a real error
                var s = odb.Sample;
                if (s == null) continue;
                using (var cb = s.ConvertToContiguousBuffer())
                {
                    cb.Lock(out var ptr, out _, out var len);
                    try
                    {
                        // Plane height can exceed the picture (e.g. 1088 for 1080): derive it from the length.
                        int planeH = _stride > 0 ? len * 2 / 3 / _stride : _outH;
                        if (planeH < _outH) planeH = _outH;
                        onFrame((byte*)ptr, _stride, planeH, _outW, _outH);
                        delivered++;
                    }
                    finally { cb.Unlock(); }
                }
                if (mine == null || s.NativePointer != mine.NativePointer) s.Dispose(); // same COM object as ours: released once below
            }
            finally
            {
                try { odb.Events?.Dispose(); } catch { }
                mineBuf?.Dispose();
                mine?.Dispose();
            }
        }
        return delivered;
    }

    public void Dispose()
    {
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        try { _mft.Dispose(); } catch { }
    }
}
