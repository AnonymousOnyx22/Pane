using System.Runtime.InteropServices;
using System.Text;

/// <summary>Reads this PC's clipboard directly (the host is on the same machine in a loopback test).</summary>
static class ClipboardProbe
{
    public static string? ReadText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var h = GetClipboardData(13);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static List<string> ReadFiles()
    {
        var list = new List<string>();
        if (!OpenClipboard(IntPtr.Zero)) return list;
        try
        {
            var h = GetClipboardData(15);
            if (h == IntPtr.Zero) return list;
            uint n = DragQueryFile(h, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < n; i++)
            {
                uint len = DragQueryFile(h, i, null, 0);
                var sb = new StringBuilder((int)len + 1);
                DragQueryFile(h, i, sb, len + 1);
                list.Add(sb.ToString());
            }
        }
        finally { CloseClipboard(); }
        return list;
    }

    public static bool HasDib() => IsClipboardFormatAvailable(8);

    /// <summary>Put files on this PC's clipboard (CF_HDROP), as Explorer's Ctrl+C would.</summary>
    public static void SetFiles(IEnumerable<string> paths)
    {
        var list = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var data = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0);
        BitConverter.GetBytes(1).CopyTo(data, 16);
        list.CopyTo(data, 20);
        if (!OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();
            var h = GlobalAlloc(2, (UIntPtr)data.Length);
            var p = GlobalLock(h);
            Marshal.Copy(data, 0, p, data.Length);
            GlobalUnlock(h);
            SetClipboardData(15, h);
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint f, IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint f, UIntPtr n);

    /// <summary>A tiny valid PNG (solid colour) built by hand, so the test needs no imaging library.</summary>
    public static byte[] MakePng(int w, int h)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++) { raw.WriteByte(0); for (int x = 0; x < w; x++) { raw.WriteByte(200); raw.WriteByte(60); raw.WriteByte(30); } }
        var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true)) raw.WriteTo(zs);
        var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(png, "IHDR", ihdr); Chunk(png, "IDAT", z.ToArray()); Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len);
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(td);
        var crc = new byte[4]; WriteBE(crc, 0, (int)Crc(td)); s.Write(crc);
    }

    private static uint Crc(byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in b) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFF;
    }

    private static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint f);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint f);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr h, uint i, StringBuilder? f, uint cch);
}
