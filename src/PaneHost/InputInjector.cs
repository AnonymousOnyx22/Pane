using System.Runtime.InteropServices;

namespace PaneHost;

/// <summary>
/// Injects mouse and keyboard input with SendInput. Normalized (0..1) coordinates from the client are
/// mapped onto the captured monitor's screen rectangle, then expressed in the 0..65535 virtual-desktop
/// space SendInput expects for absolute moves.
/// </summary>
public sealed class InputInjector
{
    private readonly int _screenX, _screenY, _screenW, _screenH; // captured monitor, in pixels
    private int VirtW => GetSystemMetrics(SM_CXVIRTUALSCREEN);
    private int VirtH => GetSystemMetrics(SM_CYVIRTUALSCREEN);
    private int VirtX => GetSystemMetrics(SM_XVIRTUALSCREEN);
    private int VirtY => GetSystemMetrics(SM_YVIRTUALSCREEN);

    public InputInjector(int screenX, int screenY, int screenW, int screenH)
    {
        _screenX = screenX; _screenY = screenY; _screenW = screenW; _screenH = screenH;
    }

    private (int ax, int ay) ToAbsolute(float nx, float ny)
    {
        nx = Math.Clamp(nx, 0f, 1f);
        ny = Math.Clamp(ny, 0f, 1f);
        // Pixel on the captured monitor.
        double px = _screenX + nx * _screenW;
        double py = _screenY + ny * _screenH;
        int vw = Math.Max(1, VirtW), vh = Math.Max(1, VirtH);
        int ax = (int)Math.Round((px - VirtX) * 65535.0 / vw);
        int ay = (int)Math.Round((py - VirtY) * 65535.0 / vh);
        return (Math.Clamp(ax, 0, 65535), Math.Clamp(ay, 0, 65535));
    }

    public void MouseMove(float nx, float ny)
    {
        var (ax, ay) = ToAbsolute(nx, ny);
        SendMouse(ax, ay, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    public void MouseButton(int button, bool down, float nx, float ny)
    {
        var (ax, ay) = ToAbsolute(nx, ny);
        uint flags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE;
        flags |= button switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            _ => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        };
        SendMouse(ax, ay, 0, flags);
    }

    public void MouseWheel(int delta, bool horizontal, float nx, float ny)
    {
        var (ax, ay) = ToAbsolute(nx, ny);
        uint flags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE |
                     (horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL);
        SendMouse(ax, ay, delta, flags);
    }

    public void Key(ushort vk, bool down, bool extended)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = 0,
            dwFlags = (down ? 0u : KEYEVENTF_KEYUP) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u),
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private void SendMouse(int ax, int ay, int data, uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi = new MOUSEINPUT
        {
            dx = ax,
            dy = ay,
            mouseData = data,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // --- Win32 interop ---

    private const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public InputUnion U; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
