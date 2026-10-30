using System;
using System.Runtime.InteropServices;

namespace Pane;

/// <summary>
/// A system-wide low-level keyboard hook. While a remote session is the active tab and Pane is the
/// foreground window, this lets keys the OS would normally handle itself — the Windows key, Alt+Tab,
/// Alt+Esc, Ctrl+Esc — be forwarded to the remote machine and suppressed locally, so a session feels
/// like sitting at the real keyboard. The mouse is never hooked, so clicking another window is always
/// an escape hatch. <see cref="OnKey"/> returns true to forward+suppress, false to let the key through.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public Func<uint, bool, bool, bool>? OnKey; // (vk, down, extended) -> handled

    private IntPtr _hook;
    private readonly LowLevelKeyboardProc _proc; // kept alive against GC

    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_EXTENDED = 0x01;

    public KeyboardHook() => _proc = HookProc;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        // For a low-level keyboard hook the module handle just needs to be the current module;
        // GetModuleHandle(null) returns the exe base and works for single-file apps too.
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION && OnKey != null)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (down || up)
            {
                bool extended = (info.flags & LLKHF_EXTENDED) != 0;
                try { if (OnKey(info.vkCode, down, extended)) return (IntPtr)1; }
                catch { /* never let a hook callback throw */ }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
