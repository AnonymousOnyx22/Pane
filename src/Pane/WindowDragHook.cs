using System;
using System.Runtime.InteropServices;

namespace Pane;

/// <summary>
/// Detects "drag an app window and drop it onto Pane": a low-level mouse hook watches for a left-button
/// press that starts on some other top-level window, a drag (the window moves), and a release while the
/// cursor is over the target (Pane) window. Windows never physically hands the window over, but this lets
/// Pane react to the gesture. Raises <see cref="OnDroppedOnTarget"/> with the dragged window's handle.
/// </summary>
public sealed class WindowDragHook : IDisposable
{
    public Func<IntPtr>? GetTargetWindow;      // returns the Pane window handle
    public Action<IntPtr>? OnDroppedOnTarget;  // dragged window handle, dropped onto Pane

    private IntPtr _hook;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _candidate;
    private RECT _startRect;
    private bool _tracking;

    private const int WH_MOUSE_LL = 14, HC_ACTION = 0;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const uint GA_ROOT = 2;

    public WindowDragHook() => _proc = HookProc;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            int msg = (int)wParam;
            if (msg == WM_LBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr target = GetTargetWindow?.Invoke() ?? IntPtr.Zero;
                IntPtr under = Root(WindowFromPoint(info.pt));
                if (under != IntPtr.Zero && under != target && IsWindowVisible(under) && GetWindowTextLength(under) > 0)
                {
                    _candidate = under;
                    GetWindowRect(under, out _startRect);
                    _tracking = true;
                }
                else _tracking = false;
            }
            else if (msg == WM_LBUTTONUP && _tracking)
            {
                _tracking = false;
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr target = GetTargetWindow?.Invoke() ?? IntPtr.Zero;
                IntPtr upUnder = Root(WindowFromPoint(info.pt));
                if (upUnder == target && target != IntPtr.Zero && GetWindowRect(_candidate, out var now))
                {
                    bool moved = Math.Abs(now.left - _startRect.left) + Math.Abs(now.top - _startRect.top) > 15;
                    if (moved)
                        try { OnDroppedOnTarget?.Invoke(_candidate); } catch { }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static IntPtr Root(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
}
