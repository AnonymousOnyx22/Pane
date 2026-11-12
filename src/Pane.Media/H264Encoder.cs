using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Pane.Media;

/// <summary>
/// Low-latency H.264 encoder over Media Foundation. Prefers a hardware encoder (NVENC / Quick Sync / AMF,
/// which are asynchronous MFTs) and falls back to Microsoft's software encoder (synchronous MFT).
/// Input is NV12 at a 16-aligned size; output is a list of Annex-B access units (one per input in
/// low-latency mode). A new encoder always starts with an IDR frame, so "force a keyframe" = recreate.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    public int Width { get; }          // encoded (16-aligned) size
    public int Height { get; }
    public string Name { get; private set; } = "";
    public bool Hardware { get; private set; }

    private IMFTransform _mft = null!;
    private bool _async;
    private bool _providesSamples;
    private int _outSize;
    private long _frame;
    private readonly int _fps;
    private byte[]? _seqHeader;

    // async (hardware) plumbing
    private IMFMediaEventGenerator? _events;
    private Thread? _pump;
    private readonly SemaphoreSlim _needInput = new(0);
    private readonly BlockingCollection<byte[]> _outputs = new();
    private volatile bool _closing;

    private H264Encoder(int w, int h, int fps) { Width = w; Height = h; _fps = fps; }

    public static int Align16(int v) => (v + 15) & ~15;

    /// <summary>Create an encoder for a <paramref name="width"/>×<paramref name="height"/> screen.
    /// Tries hardware first unless <paramref name="allowHardware"/> is false.</summary>
    public static H264Encoder Create(int width, int height, int fps, int bitrate, bool allowHardware = true)
    {
        Mf.Startup();
        int w = Align16(width), h = Align16(height);
        var errors = new List<string>();

        if (allowHardware)
        {
            IMFActivateCollection? list = null;
            try
            {
                list = MediaFactory.MFTEnumEx(Mf.CATEGORY_VIDEO_ENCODER,
                    Mf.ENUM_FLAG_HARDWARE | Mf.ENUM_FLAG_SORTANDFILTER,
                    new RegisterTypeInfo { GuidMajorType = Mf.MediaType_Video, GuidSubtype = Mf.VideoFormat_NV12 },
                    new RegisterTypeInfo { GuidMajorType = Mf.MediaType_Video, GuidSubtype = Mf.VideoFormat_H264 });
            }
            catch (Exception ex) { errors.Add("enum: " + ex.Message); }

            if (list != null)
            {
                foreach (var act in list)
                {
                    var enc = new H264Encoder(w, h, fps);
                    try
                    {
                        string name = "Hardware H.264";
                        try { name = act.GetAllocatedString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { }
                        var mft = act.ActivateObject<IMFTransform>();
                        enc.Init(mft, isAsync: true, bitrate);
                        enc.Name = name; enc.Hardware = true;
                        enc.SelfTest();
                        return enc;
                    }
                    catch (Exception ex) { errors.Add(ex.Message); enc.Dispose(); }
                }
            }
        }

        var sw = new H264Encoder(w, h, fps);
        try
        {
            sw.Init(Mf.CreateByClsid(Mf.CLSID_MSH264EncoderMFT), isAsync: false, bitrate);
            sw.Name = "Microsoft H.264 (software)";
            return sw;
        }
        catch (Exception ex)
        {
            sw.Dispose();
            errors.Add("software: " + ex.Message);
            throw new InvalidOperationException("No usable H.264 encoder: " + string.Join("; ", errors));
        }
    }

    private void Init(IMFTransform mft, bool isAsync, int bitrate)
    {
        _mft = mft;
        _async = isAsync;
        if (isAsync)
        {
            var attrs = mft.Attributes;
            attrs.Set(Mf.TRANSFORM_ASYNC_UNLOCK, 1u);
        }
        try { mft.Attributes.Set(Mf.LOW_LATENCY, 1u); } catch { }

        // Codec settings first: some encoders only honour rate control set before the media types.
        Mf.SetCodecValue(mft, Mf.LOW_LATENCY, true);
        Mf.SetCodecValue(mft, Mf.AVEncCommonRateControlMode, 0u /*CBR*/);
        Mf.SetCodecValue(mft, Mf.AVEncCommonMeanBitRate, (uint)bitrate);
        Mf.SetCodecValue(mft, Mf.AVEncMPVDefaultBPictureCount, 0u);
        Mf.SetCodecValue(mft, Mf.AVEncMPVGOPSize, (uint)(_fps * 10));
        Mf.SetCodecValue(mft, Mf.AVEncCommonQualityVsSpeed, 50u);

        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(Mf.MT_MAJOR_TYPE, Mf.MediaType_Video);
            outType.Set(Mf.MT_SUBTYPE, Mf.VideoFormat_H264);
            outType.Set(Mf.MT_AVG_BITRATE, (uint)bitrate);
            outType.Set(Mf.MT_FRAME_SIZE, Mf.Pack(Width, Height));
            outType.Set(Mf.MT_FRAME_RATE, Mf.Pack(_fps, 1));
            outType.Set(Mf.MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
            outType.Set(Mf.MT_INTERLACE_MODE, 2u /*progressive*/);
            outType.Set(Mf.MT_MPEG2_PROFILE, 100u /*High*/);
            try { mft.SetOutputType(0, outType, 0); }
            catch
            {
                outType.Set(Mf.MT_MPEG2_PROFILE, 77u /*Main*/);
                mft.SetOutputType(0, outType, 0);
            }
        }
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(Mf.MT_MAJOR_TYPE, Mf.MediaType_Video);
            inType.Set(Mf.MT_SUBTYPE, Mf.VideoFormat_NV12);
            inType.Set(Mf.MT_FRAME_SIZE, Mf.Pack(Width, Height));
            inType.Set(Mf.MT_FRAME_RATE, Mf.Pack(_fps, 1));
            inType.Set(Mf.MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
            inType.Set(Mf.MT_INTERLACE_MODE, 2u);
            mft.SetInputType(0, inType, 0);
        }

        try
        {
            using var cur = mft.GetOutputCurrentType(0);
            _seqHeader = cur.GetBlob(Mf.MT_MPEG_SEQUENCE_HEADER);
        }
        catch { _seqHeader = null; }

        var info = mft.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (Mf.OUTPUT_STREAM_PROVIDES_SAMPLES | Mf.OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        _outSize = Math.Max(info.Size, Width * Height * 3 / 2);

        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        if (isAsync)
        {
            _events = mft.QueryInterface<IMFMediaEventGenerator>();
            _pump = new Thread(PumpEvents) { IsBackground = true, Name = "H264 encoder events" };
            _pump.Start();
        }
    }

    /// <summary>Hardware MFTs can enumerate yet fail on first use (e.g. need a D3D device). Encode one
    /// grey frame to prove this one works with system-memory input before we commit to it.</summary>
    private void SelfTest()
    {
        var grey = new byte[Nv12.Size(Width, Height)];
        Array.Fill(grey, (byte)128);
        var aus = Encode(grey);
        if (aus.Count == 0)
        {
            // Some encoders need a couple of frames before the first output.
            for (int i = 0; i < 3 && aus.Count == 0; i++) aus = Encode(grey);
        }
        if (aus.Count == 0) throw new InvalidOperationException("encoder produced no output");
        // The grey frame is the stream's IDR: keep it and send it ahead of the first real frame, so
        // the decoder always starts on a keyframe.
        _pending = aus;
    }

    private List<byte[]>? _pending;

    /// <summary>Encode one NV12 frame (Width×Height). Returns the access units that came out.</summary>
    public List<byte[]> Encode(byte[] nv12)
    {
        var result = new List<byte[]>(1);
        if (_pending != null) { result.AddRange(_pending); _pending = null; }
        if (_async && !_needInput.Wait(1000)) return result; // encoder stalled; drop this frame

        var sample = Mf.MakeSample(nv12.Length, out var buf);
        try
        {
            buf.Lock(out var p, out _, out _);
            Marshal.Copy(nv12, 0, p, nv12.Length);
            buf.Unlock();
            buf.CurrentLength = nv12.Length;
            long dur = 10_000_000L / _fps;
            sample.SampleTime = _frame * dur;
            sample.SampleDuration = dur;
            _frame++;

            if (_async)
            {
                _mft.ProcessInput(0, sample, 0);
                // Low-latency hardware encoders return this frame's output within a few ms.
                if (_outputs.TryTake(out var au, 60)) result.Add(au);
                while (_outputs.TryTake(out var more)) result.Add(more);
            }
            else
            {
                _mft.ProcessInput(0, sample, 0);
                DrainSync(result);
            }
        }
        catch (SharpGenException ex) when (ex.HResult == Mf.E_NOTACCEPTING)
        {
            if (!_async) DrainSync(result);
        }
        finally { buf.Dispose(); sample.Dispose(); }
        return result;
    }

    private void DrainSync(List<byte[]> result)
    {
        while (true)
        {
            var au = PullOutput(out int hr);
            if (au != null) { result.Add(au); continue; }
            if (hr == Mf.E_NEED_MORE_INPUT || hr < 0) return;
        }
    }

    /// <summary>One ProcessOutput call. Returns the access unit, or null with the HRESULT.</summary>
    private byte[]? PullOutput(out int hr)
    {
        IMFSample? mine = null; IMFMediaBuffer? mineBuf = null;
        if (!_providesSamples) mine = Mf.MakeSample(_outSize, out mineBuf);
        var odb = new OutputDataBuffer { StreamID = 0, Sample = mine! };
        try
        {
            var res = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref odb, out _);
            hr = res.Code;
            if (res.Failure) return null;
            var s = odb.Sample;
            if (s == null) return null;
            var bytes = Mf.SampleBytes(s);
            if (mine == null || s.NativePointer != mine.NativePointer) s.Dispose(); // same COM object as ours: released once below
            return WithSequenceHeader(bytes);
        }
        finally
        {
            try { odb.Events?.Dispose(); } catch { }
            mineBuf?.Dispose();
            mine?.Dispose();
        }
    }

    /// <summary>Ensure IDR access units carry SPS/PPS inline (some encoders only put them in the
    /// media type), so a decoder can start from any keyframe.</summary>
    private byte[] WithSequenceHeader(byte[] au)
    {
        if (_seqHeader == null || _seqHeader.Length == 0) return au;
        if (!H264.ContainsNal(au, 5) || H264.ContainsNal(au, 7)) return au;
        var merged = new byte[_seqHeader.Length + au.Length];
        Buffer.BlockCopy(_seqHeader, 0, merged, 0, _seqHeader.Length);
        Buffer.BlockCopy(au, 0, merged, _seqHeader.Length, au.Length);
        return merged;
    }

    private void PumpEvents()
    {
        try
        {
            while (!_closing)
            {
                using var ev = _events!.GetEvent(0); // blocks until the MFT raises an event
                switch ((int)ev.EventType)
                {
                    case 601: _needInput.Release(); break;                        // METransformNeedInput
                    case 602:                                                      // METransformHaveOutput
                        var au = PullOutput(out _);
                        if (au != null) _outputs.Add(au);
                        break;
                    case 603: return;                                              // METransformDrainComplete
                }
            }
        }
        catch { /* shut down or device lost; Encode will time out and the caller recreates us */ }
    }

    public void Dispose()
    {
        _closing = true;
        try
        {
            if (_mft != null)
            {
                if (_async) { try { _mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); } catch { } }
                try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
            }
        }
        catch { }
        _pump?.Join(300);
        try { _events?.Dispose(); } catch { }
        try { _mft?.Dispose(); } catch { }
    }
}

/// <summary>Tiny Annex-B helpers.</summary>
public static class H264
{
    /// <summary>True if the Annex-B buffer contains a NAL unit of the given type (5 = IDR, 7 = SPS).</summary>
    public static bool ContainsNal(ReadOnlySpan<byte> data, int type)
    {
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if ((data[i + 3] & 0x1F) == type) return true;
                i += 2;
            }
        }
        return false;
    }
}
