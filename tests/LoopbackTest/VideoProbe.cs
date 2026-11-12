using Pane.Media;

/// <summary>Decodes one VideoFrame message the same way the client does (H.264 -> NV12 -> BGRA).</summary>
static class VideoProbe
{
    public static unsafe (int pictures, int width, int height) Decode(H264Decoder dec, byte[] msg, int screenW, int screenH, byte[] bgra)
    {
        int pw = 0, ph = 0;
        int n = dec.Decode(msg, 1, msg.Length - 1, (p, stride, planeH, w, h) =>
        {
            pw = w; ph = h;
            Nv12.ToBgra(p, stride, planeH, Math.Min(w, screenW), Math.Min(h, screenH), bgra);
        });
        return (n, pw, ph);
    }
}
