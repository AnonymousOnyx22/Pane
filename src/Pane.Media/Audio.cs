using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Pane.Media;

/// <summary>
/// Captures whatever the PC is playing (WASAPI loopback on the default output device) and hands it out
/// as interleaved 16-bit PCM, at most stereo. Restarts itself if the default device changes.
/// </summary>
public sealed class LoopbackAudio : IDisposable
{
    /// <summary>(sampleRate, channels, pcm16 bytes)</summary>
    public event Action<int, int, byte[]>? Data;

    private WasapiLoopbackCapture? _cap;
    private volatile bool _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        Open();
    }

    private void Open()
    {
        try
        {
            var cap = new WasapiLoopbackCapture();
            cap.DataAvailable += (_, e) => Convert(cap.WaveFormat, e.Buffer, e.BytesRecorded);
            cap.RecordingStopped += (_, _) =>
            {
                cap.Dispose();
                // Default device changed or was unplugged: reopen shortly while still wanted.
                if (_running) Task.Delay(1000).ContinueWith(_ => { if (_running) Open(); });
            };
            _cap = cap;
            cap.StartRecording();
        }
        catch
        {
            if (_running) Task.Delay(3000).ContinueWith(_ => { if (_running) Open(); });
        }
    }

    public void Stop()
    {
        _running = false;
        try { _cap?.StopRecording(); } catch { }
        _cap = null;
    }

    private void Convert(WaveFormat f, byte[] buf, int bytes)
    {
        if (bytes <= 0) return;
        int ch = f.Channels, outCh = Math.Min(ch, 2);
        int bps = f.BitsPerSample / 8;
        int frames = bytes / (bps * ch);
        if (frames == 0) return;
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (f is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var pcm = new byte[frames * outCh * 2];
        int o = 0;
        for (int i = 0; i < frames; i++)
        {
            int baseOff = i * bps * ch;
            for (int c = 0; c < outCh; c++)
            {
                int off = baseOff + c * bps;
                short s;
                if (isFloat && bps == 4)
                {
                    float v = BitConverter.ToSingle(buf, off);
                    s = (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);
                }
                else if (bps == 2) s = BitConverter.ToInt16(buf, off);
                else if (bps == 3) s = (short)((buf[off + 1]) | (buf[off + 2] << 8));
                else if (bps == 4) s = (short)(BitConverter.ToInt32(buf, off) >> 16);
                else s = 0;
                pcm[o++] = (byte)s; pcm[o++] = (byte)(s >> 8);
            }
        }
        Data?.Invoke(f.SampleRate, outCh, pcm);
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Plays streamed PCM16 with low, bounded latency: a small jitter buffer that is trimmed back whenever
/// it grows past ~150 ms, so audio never drifts behind the picture.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private BufferedWaveProvider? _buf;
    private WasapiOut? _out;
    private int _rate, _ch;
    private float _volume = 1f;
    private readonly object _lock = new();

    public bool Muted { get; set; }

    public void Push(int rate, int channels, byte[] data, int offset, int count)
    {
        if (Muted || count <= 0) return;
        lock (_lock)
        {
            if (_buf == null || rate != _rate || channels != _ch) Open(rate, channels);
            if (_buf == null) return;
            if (_buf.BufferedDuration > TimeSpan.FromMilliseconds(150)) _buf.ClearBuffer();
            _buf.AddSamples(data, offset, count);
        }
    }

    private void Open(int rate, int channels)
    {
        Close();
        try
        {
            _rate = rate; _ch = channels;
            _buf = new BufferedWaveProvider(new WaveFormat(rate, 16, channels))
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true,
            };
            _out = new WasapiOut(AudioClientShareMode.Shared, true, 40);
            _out.Init(_buf);
            _out.Volume = _volume;
            _out.Play();
        }
        catch { Close(); }
    }

    private void Close()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        _out = null; _buf = null;
    }

    public void Dispose() { lock (_lock) Close(); }
}
