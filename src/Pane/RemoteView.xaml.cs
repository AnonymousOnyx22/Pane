using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private bool _closed;
    private readonly DispatcherTimer _reconnectTimer;

    public RemoteView(SavedPc pc)
    {
        InitializeComponent();
        _pc = pc;
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _reconnectTimer.Tick += (_, __) => { _reconnectTimer.Stop(); if (!_closed) Connect(); };
        Loaded += (_, __) => { Focus(); Keyboard.Focus(this); };
        WireInput();
        Connect();
    }

    private void Connect()
    {
        if (_closed) return;
        ShowStatus("Connecting...", false);
        if (!Protocol.PairingKey.TryDecode(_pc.Key, out var key))
        {
            ShowStatus("That pairing key is not valid.", true);
            return;
        }

        _client?.Dispose();
        _client = new RemoteClient();
        _client.Connected += (w, h) => Dispatcher.Invoke(() => OnConnected(w, h));
        _client.FrameReceived += rects => Dispatcher.Invoke(() => OnFrame(rects));
        _client.Disconnected += reason => Dispatcher.Invoke(() => OnDisconnected(reason));
        _client.DirListing += (path, entries) => Dispatcher.Invoke(() => OnDirListing(path, entries));
        _client.TransferStatus += s => Dispatcher.Invoke(() => ShowToast(s));
        _client.Start(string.IsNullOrWhiteSpace(_pc.Host) ? _pc.Id : _pc.Host, Net.TcpPort, key);
    }

    private void OnDisconnected(string reason)
    {
        if (_closed) return;
        // Auto-reconnect: keep retrying every 2s. The manual button is still there as a nudge.
        ShowStatus("Connection lost — reconnecting…", true);
        _reconnectTimer.Stop();
        _reconnectTimer.Start();
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
        _reconnectTimer.Stop();
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

    /// <summary>Called by the global keyboard hook to forward a raw key (incl. Win, Alt+Tab) to the host.</summary>
    public void ForwardKey(ushort vk, bool down, bool extended) => _client?.SendKey(vk, down, extended);

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

    // --- file transfer UI ---

    private bool _filesOpen;
    private string? _currentPath;
    private DispatcherTimer? _toastTimer;

    private void FilesToggle_Click(object sender, RoutedEventArgs e)
    {
        _filesOpen = !_filesOpen;
        var to = _filesOpen ? 300.0 : 0.0;
        var anim = new DoubleAnimation(FilesPanel.ActualWidth, to, new Duration(TimeSpan.FromMilliseconds(180)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        FilesPanel.BeginAnimation(WidthProperty, anim);
        if (_filesOpen && _client != null)
            _client.RequestListDir(_currentPath ?? "");
    }

    private void OnDirListing(string path, List<FsEntry> entries)
    {
        _currentPath = path;
        PathText.Text = string.IsNullOrEmpty(path) ? "This PC" : path;
        FileList.ItemsSource = entries.Select(en => new FsItem(en)).ToList();
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null || string.IsNullOrEmpty(_currentPath)) return;
        var parent = Directory.GetParent(_currentPath.TrimEnd('\\', '/'));
        _client.RequestListDir(parent?.FullName ?? "");
    }

    private void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not FsItem it || _client == null) return;
        if (it.Entry.IsDir) _client.RequestListDir(FullPath(it.Entry));
        else { _client.RequestDownload(FullPath(it.Entry)); ShowToast("Downloading " + it.Entry.Name + "…"); }
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FsItem it || _client == null) return;
        _client.RequestDownload(FullPath(it.Entry));
        ShowToast("Downloading " + it.Entry.Name + "…");
    }

    private string FullPath(FsEntry e) =>
        string.IsNullOrEmpty(_currentPath) ? e.Name : Path.Combine(_currentPath, e.Name);

    private void ScreenHost_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropHint.Visibility = Visibility.Visible;
            e.Effects = DragDropEffects.Copy;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void ScreenHost_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private void ScreenHost_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (_client == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (paths is { Length: > 0 }) { ShowToast("Sending " + paths.Length + " item(s)…"); _ = _client.UploadAsync(paths); }
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Stop();
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Start();
    }

    private void HideToast(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    private sealed class FsItem
    {
        public FsEntry Entry { get; }
        public FsItem(FsEntry e) => Entry = e;
        public override string ToString() =>
            Entry.IsDir ? Entry.Name.TrimEnd('\\', '/') + "\\" : Entry.Name + "   " + Human(Entry.Size);
        private static string Human(long n) =>
            n >= 1L << 30 ? $"{n / 1024.0 / 1024 / 1024:N1} GB" :
            n >= 1L << 20 ? $"{n / 1024.0 / 1024:N1} MB" :
            n >= 1L << 10 ? $"{n / 1024.0:N0} KB" : $"{n} B";
    }

    public void Dispose()
    {
        _closed = true;
        _reconnectTimer.Stop();
        _toastTimer?.Stop();
        _client?.Dispose();
    }
}
