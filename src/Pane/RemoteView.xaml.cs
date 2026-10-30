using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pane.Protocol;

namespace Pane;

/// <summary>A live session tab: renders the host's screen and forwards mouse, keyboard and scroll.</summary>
public partial class RemoteView : UserControl, IDisposable
{
    private readonly SavedPc _pc;
    private RemoteClient? _client;
    private WriteableBitmap? _bmp;
    private int _w, _h;
    private readonly Stopwatch _moveClock = Stopwatch.StartNew();
    private long _lastMoveSentMs = -100;

    public RemoteView(SavedPc pc)
    {
        InitializeComponent();
        _pc = pc;
        Loaded += (_, __) => { Focus(); Keyboard.Focus(this); };
        WireInput();
        Connect();
    }

    private void Connect()
    {
        ShowStatus("Connecting...", false);
        if (!Protocol.PairingKey.TryDecode(_pc.Key, out var key))
        {
            ShowStatus("That pairing key is not valid.", true);
            return;
        }

        _client = new RemoteClient();
        _client.Connected += (w, h) => Dispatcher.Invoke(() => OnConnected(w, h));
        _client.FrameReceived += rects => Dispatcher.Invoke(() => OnFrame(rects));
        _client.Disconnected += reason => Dispatcher.Invoke(() => ShowStatus(reason, true));
        _client.Start(string.IsNullOrWhiteSpace(_pc.Host) ? _pc.Id : _pc.Host, Net.TcpPort, key);
    }

    private void OnConnected(int w, int h)
    {
        _w = w; _h = h;
        _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        Screen.Source = _bmp;
        Overlay.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void OnFrame(System.Collections.Generic.List<FrameRect> rects)
    {
        if (_bmp == null) return;
        foreach (var r in rects)
        {
            if (r.W <= 0 || r.H <= 0) continue;
            if (r.X + r.W > _w || r.Y + r.H > _h) continue;
            try
            {
                BitmapSource src = BitmapFrame.Create(new MemoryStream(r.Compressed),
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (src.Format != PixelFormats.Bgra32)
                    src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int stride = r.W * 4;
                var pixels = new byte[stride * r.H];
                src.CopyPixels(pixels, stride, 0);
                _bmp.WritePixels(new Int32Rect(r.X, r.Y, r.W, r.H), pixels, stride, 0);
            }
            catch { /* skip a corrupt rect; the next full frame heals it */ }
        }
    }

    // --- status overlay ---

    private void ShowStatus(string text, bool retry)
    {
        StatusText.Text = text;
        RetryBtn.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _client?.Dispose();
        _client = null;
        Connect();
    }

    // --- input forwarding ---

    private bool TryMap(Point p, out float nx, out float ny)
    {
        nx = ny = 0;
        if (_w == 0 || _h == 0) return false;
        double cw = Screen.ActualWidth, ch = Screen.ActualHeight;
        if (cw <= 0 || ch <= 0) return false;
        double scale = Math.Min(cw / _w, ch / _h);
        double dispW = _w * scale, dispH = _h * scale;
        double offX = (cw - dispW) / 2, offY = (ch - dispH) / 2;
        nx = (float)Math.Clamp((p.X - offX) / dispW, 0, 1);
        ny = (float)Math.Clamp((p.Y - offY) / dispH, 0, 1);
        return true;
    }

    private void WireInput()
    {
        Screen.MouseMove += (s, e) =>
        {
            long now = _moveClock.ElapsedMilliseconds;
            if (now - _lastMoveSentMs < 8) return;        // ~125 Hz cap
            if (TryMap(e.GetPosition(Screen), out var nx, out var ny))
            {
                _lastMoveSentMs = now;
                _client?.SendMouseMove(nx, ny);
            }
        };

        Screen.MouseDown += (s, e) =>
        {
            Focus(); Keyboard.Focus(this);
            Screen.CaptureMouse();
            if (TryMap(e.GetPosition(Screen), out var nx, out var ny))
                _client?.SendMouseButton(ButtonIndex(e.ChangedButton), true, nx, ny);
        };
        Screen.MouseUp += (s, e) =>
        {
            Screen.ReleaseMouseCapture();
            if (TryMap(e.GetPosition(Screen), out var nx, out var ny))
                _client?.SendMouseButton(ButtonIndex(e.ChangedButton), false, nx, ny);
        };
        Screen.MouseWheel += (s, e) =>
        {
            if (TryMap(e.GetPosition(Screen), out var nx, out var ny))
                _client?.SendMouseWheel(e.Delta, false, nx, ny);
        };

        PreviewKeyDown += (s, e) => { if (SendKey(e, true)) e.Handled = true; };
        PreviewKeyUp += (s, e) => { if (SendKey(e, false)) e.Handled = true; };
    }

    private static int ButtonIndex(MouseButton b) => b switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        _ => 2
    };

    private bool SendKey(KeyEventArgs e, bool down)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;
        _client?.SendKey((ushort)vk, down, IsExtended(key));
        return true;
    }

    private static bool IsExtended(Key k) => k switch
    {
        Key.Left or Key.Right or Key.Up or Key.Down => true,
        Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown => true,
        Key.RightCtrl or Key.RightAlt => true,
        Key.NumLock or Key.PrintScreen => true,
        _ => false
    };

    public void Dispose() => _client?.Dispose();
}
