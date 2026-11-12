namespace Pane.Media;

/// <summary>
/// BGRA32 &lt;-&gt; NV12 conversion (full-range BT.709, integer math, parallel over row pairs). The host
/// converts captured frames before H.264 encoding and the client converts decoded frames back. Both
/// sides use the same matrix, so colours round-trip regardless of what the bitstream's VUI claims.
/// </summary>
public static unsafe class Nv12
{
    /// <summary>Bytes needed for an NV12 image of the given (even) size.</summary>
    public static int Size(int w, int h) => w * h * 3 / 2;

    /// <summary>
    /// Convert a w×h BGRA image (row pitch <paramref name="srcPitch"/>) into an NV12 buffer of
    /// <paramref name="dw"/>×<paramref name="dh"/> (both even, ≥ w/h). Padding repeats the edge pixels.
    /// </summary>
    public static void FromBgra(byte* src, int srcPitch, int w, int h, byte* dst, int dw, int dh)
    {
        byte* yPlane = dst;
        byte* uvPlane = dst + (long)dw * dh;
        int pairs = (h + 1) / 2;
        Parallel.For(0, pairs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, p =>
        {
            int y0 = p * 2;
            int y1 = Math.Min(y0 + 1, h - 1);
            byte* r0 = src + (long)y0 * srcPitch;
            byte* r1 = src + (long)y1 * srcPitch;
            byte* yo0 = yPlane + (long)y0 * dw;
            byte* yo1 = yPlane + (long)(y0 + 1) * dw;
            byte* uvo = uvPlane + (long)p * dw;
            bool second = y0 + 1 < dh;
            int x = 0;
            for (; x + 1 < w; x += 2)
            {
                byte* a = r0 + x * 4; byte* b = a + 4; byte* c = r1 + x * 4; byte* d = c + 4;
                yo0[x] = Luma(a); yo0[x + 1] = Luma(b);
                if (second) { yo1[x] = Luma(c); yo1[x + 1] = Luma(d); }
                int bs = a[0] + b[0] + c[0] + d[0], gs = a[1] + b[1] + c[1] + d[1], rs = a[2] + b[2] + c[2] + d[2];
                uvo[x] = Clamp(((-29 * rs - 99 * gs + 128 * bs) >> 10) + 128);
                uvo[x + 1] = Clamp(((128 * rs - 116 * gs - 12 * bs) >> 10) + 128);
            }
            if (x < w) // odd width: last column
            {
                byte* a = r0 + x * 4; byte* c = r1 + x * 4;
                yo0[x] = Luma(a); if (second) yo1[x] = Luma(c);
                int bs = (a[0] + c[0]) * 2, gs = (a[1] + c[1]) * 2, rs = (a[2] + c[2]) * 2;
                uvo[x] = Clamp(((-29 * rs - 99 * gs + 128 * bs) >> 10) + 128);
                uvo[x + 1] = Clamp(((128 * rs - 116 * gs - 12 * bs) >> 10) + 128);
                x += 2;
            }
            // Horizontal padding: repeat the last real pixel.
            int lastEven = Math.Max(0, (w - 1) & ~1);
            for (int px = w; px < dw; px++)
            {
                yo0[px] = yo0[w - 1];
                if (second) yo1[px] = yo1[w - 1];
            }
            for (int px = x; px + 1 < dw; px += 2) { uvo[px] = uvo[lastEven]; uvo[px + 1] = uvo[lastEven + 1]; }
        });
        // Vertical padding: repeat the last real row of each plane.
        for (int y = h; y < dh; y++)
            Buffer.MemoryCopy(yPlane + (long)(h - 1) * dw, yPlane + (long)y * dw, dw, dw);
        for (int p = pairs; p < dh / 2; p++)
            Buffer.MemoryCopy(uvPlane + (long)(pairs - 1) * dw, uvPlane + (long)p * dw, dw, dw);
    }

    /// <summary>Convert the top-left w×h of an NV12 image (luma stride <paramref name="stride"/>, plane
    /// height <paramref name="planeH"/>) into tightly packed BGRA32.</summary>
    public static void ToBgra(byte* src, int stride, int planeH, int w, int h, byte[] dst)
    {
        byte* yPlane = src;
        byte* uvPlane = src + (long)stride * planeH;
        fixed (byte* dp = dst)
        {
            byte* d0 = dp;
            int rowBytes = w * 4;
            int pairs = (h + 1) / 2;
            Parallel.For(0, pairs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, p =>
            {
                byte* uv = uvPlane + (long)p * stride;
                for (int r = 0; r < 2; r++)
                {
                    int y = p * 2 + r;
                    if (y >= h) break;
                    byte* yr = yPlane + (long)y * stride;
                    byte* o = d0 + (long)y * rowBytes;
                    for (int x = 0; x < w; x++)
                    {
                        int Y = yr[x] << 8;
                        int U = uv[x & ~1] - 128, V = uv[(x & ~1) + 1] - 128;
                        o[0] = Clamp((Y + 475 * U) >> 8);
                        o[1] = Clamp((Y - 48 * U - 120 * V) >> 8);
                        o[2] = Clamp((Y + 403 * V) >> 8);
                        o[3] = 255;
                        o += 4;
                    }
                }
            });
        }
    }

    private static byte Luma(byte* px) => (byte)((54 * px[2] + 183 * px[1] + 18 * px[0] + 128) >> 8);
    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
