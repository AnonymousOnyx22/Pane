using System.Runtime.InteropServices;
using Pane.Protocol;

namespace PaneHost;

/// <summary>
/// Two-way plain-text clipboard sync for one session. Polls the clipboard sequence number (cheap) and
/// sends new text to the client; applies text received from the client. Win32 only, no WinForms needed.
/// </summary>
public sealed class ClipboardSync
{
    private const uint CF_UNICODETEXT = 13;
    private const int MaxChars = 1_000_000;
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

    private uint _lastSeq = GetClipboardSequenceNumber();

    /// <summary>Runs until cancelled, sending local clipboard changes through the channel.</summary>
    public async Task RunAsync(SecureChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(400, ct); } catch { return; }
            uint seq = GetClipboardSequenceNumber();
            if (seq == _lastSeq) continue;
            _lastSeq = seq;
            var text = ReadText();
            if (string.IsNullOrEmpty(text) || text.Length > MaxChars) continue;
            try { await channel.SendAsync(Messages.Clipboard(text), ct); } catch { return; }
        }
    }

    public void Apply(string text)
    {
        if (text.Length > MaxChars) return;
        WriteText(text);
        _lastSeq = GetClipboardSequenceNumber(); // do not echo it straight back
    }

    private static string? ReadText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        for (int i = 0; i < 5; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var h = GetClipboardData(CF_UNICODETEXT);
                    if (h == IntPtr.Zero) return null;
                    var p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20);
        }
        return null;
    }

    private static void WriteText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var bytes = (text.Length + 1) * 2;
                    var h = GlobalAlloc(0x0002 /*GMEM_MOVEABLE*/, (UIntPtr)bytes);
                    var p = GlobalLock(h);
                    Marshal.Copy((text + "\0").ToCharArray(), 0, p, text.Length + 1);
                    GlobalUnlock(h);
                    SetClipboardData(CF_UNICODETEXT, h);
                }
                finally { CloseClipboard(); }
                return;
            }
            Thread.Sleep(20);
        }
    }
}
