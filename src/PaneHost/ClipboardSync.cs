using System.Runtime.InteropServices;
using System.Text;
using Pane.Protocol;

namespace PaneHost;

/// <summary>
/// Two-way clipboard sync for one session: text, images (as PNG) and files. Polls the clipboard
/// sequence number (cheap) and pushes changes to the client; applies what the client sends. Things this
/// side wrote are remembered by sequence number so they are never echoed back. Win32 only.
/// Priority when several formats are present: files, then text, then image.
/// </summary>
public sealed class ClipboardSync
{
    private const uint CF_DIB = 8, CF_UNICODETEXT = 13, CF_HDROP = 15;
    private const int MaxChars = 1_000_000;
    private const int MaxImageBytes = 32 * 1024 * 1024;

    private uint _lastSeq = GetClipboardSequenceNumber();

    /// <summary>Runs until cancelled, sending local clipboard changes through the channel.</summary>
    public async Task RunAsync(SecureChannel channel, FileService files, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(400, ct); } catch { return; }
            uint seq = GetClipboardSequenceNumber();
            if (seq == _lastSeq) continue;
            _lastSeq = seq;
            try
            {
                if (IsClipboardFormatAvailable(CF_HDROP))
                {
                    var paths = ReadFiles();
                    if (paths.Count == 0) continue;
                    long bytes = FileService.MeasureBytes(paths);
                    if (bytes > FileService.MaxClipBytes) { Log.Info("Clipboard files too large to sync; skipped."); continue; }
                    await files.SendClipFilesAsync(paths);
                }
                else if (IsClipboardFormatAvailable(CF_UNICODETEXT))
                {
                    var text = ReadText();
                    if (string.IsNullOrEmpty(text) || text.Length > MaxChars) continue;
                    await channel.SendAsync(Messages.Clipboard(text), ct);
                }
                else if (IsClipboardFormatAvailable(CF_DIB))
                {
                    var png = ReadImagePng();
                    if (png == null || png.Length > MaxImageBytes) continue;
                    await channel.SendAsync(Messages.ClipboardImage(png), ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Info($"Clipboard sync error: {ex.Message}"); }
        }
    }

    // --- apply from client ---

    public void ApplyText(string text)
    {
        if (text.Length > MaxChars) return;
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        Write(CF_UNICODETEXT, bytes);
    }

    public void ApplyImage(ArraySegment<byte> png)
    {
        try
        {
            using var ms = new MemoryStream(png.Array!, png.Offset, png.Count);
            using var bmp = new System.Drawing.Bitmap(ms);
            using var bmpMs = new MemoryStream();
            bmp.Save(bmpMs, System.Drawing.Imaging.ImageFormat.Bmp);
            var file = bmpMs.ToArray();
            // A DIB is a BMP file without its 14-byte BITMAPFILEHEADER.
            Write(CF_DIB, file.AsSpan(14).ToArray());
        }
        catch (Exception ex) { Log.Info($"Clipboard image apply failed: {ex.Message}"); }
    }

    public void ApplyFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        // DROPFILES header (20 bytes) followed by a double-NUL-terminated UTF-16 path list.
        var list = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var data = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0);   // pFiles: offset of the list
        BitConverter.GetBytes(1).CopyTo(data, 16);   // fWide: UTF-16
        list.CopyTo(data, 20);
        Write(CF_HDROP, data);
    }

    // --- Win32 read helpers ---

    private static string? ReadText() => WithClipboard(() =>
    {
        var h = GetClipboardData(CF_UNICODETEXT);
        if (h == IntPtr.Zero) return null;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
    });

    private static List<string> ReadFiles() => WithClipboard(() =>
    {
        var result = new List<string>();
        var h = GetClipboardData(CF_HDROP);
        if (h == IntPtr.Zero) return result;
        uint n = DragQueryFile(h, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < n; i++)
        {
            uint len = DragQueryFile(h, i, null, 0);
            var sb = new StringBuilder((int)len + 1);
            DragQueryFile(h, i, sb, len + 1);
            result.Add(sb.ToString());
        }
        return result;
    }) ?? new List<string>();

    private static byte[]? ReadImagePng()
    {
        var dib = WithClipboard(() =>
        {
            var h = GetClipboardData(CF_DIB);
            if (h == IntPtr.Zero) return null;
            var size = (int)GlobalSize(h);
            var p = GlobalLock(h);
            if (p == IntPtr.Zero || size <= 40) return null;
            try { var b = new byte[size]; Marshal.Copy(p, b, 0, size); return b; }
            finally { GlobalUnlock(h); }
        });
        if (dib == null) return null;
        try
        {
            // Rebuild a BMP file around the DIB so System.Drawing can read it, then re-encode as PNG.
            int headerSize = BitConverter.ToInt32(dib, 0);
            int bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int clrUsed = BitConverter.ToInt32(dib, 32);
            int masks = (compression == 3 && headerSize == 40) ? 12 : 0;            // BI_BITFIELDS masks
            int palette = (clrUsed != 0 ? clrUsed : (bitCount <= 8 ? 1 << bitCount : 0)) * 4;
            int offBits = 14 + headerSize + masks + palette;
            var file = new byte[14 + dib.Length];
            file[0] = (byte)'B'; file[1] = (byte)'M';
            BitConverter.GetBytes(file.Length).CopyTo(file, 2);
            BitConverter.GetBytes(offBits).CopyTo(file, 10);
            dib.CopyTo(file, 14);
            using var bmp = new System.Drawing.Bitmap(new MemoryStream(file));
            using var png = new MemoryStream();
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            return png.ToArray();
        }
        catch { return null; }
    }

    private static T? WithClipboard<T>(Func<T?> body)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try { return body(); }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20);
        }
        return default;
    }

    private void Write(uint format, byte[] data)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var h = GlobalAlloc(0x0002 /*GMEM_MOVEABLE*/, (UIntPtr)data.Length);
                    var p = GlobalLock(h);
                    Marshal.Copy(data, 0, p, data.Length);
                    GlobalUnlock(h);
                    SetClipboardData(format, h);
                }
                finally { CloseClipboard(); }
                _lastSeq = GetClipboardSequenceNumber(); // don't echo it straight back
                return;
            }
            Thread.Sleep(20);
        }
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint fmt, IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint fmt);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? file, uint cch);
}
