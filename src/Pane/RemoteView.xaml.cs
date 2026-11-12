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
        // Viewing this same PC: playing its own sound back would feed straight into the capture (echo).
        if (pc.Host is "127.0.0.1" or "localhost" or "::1")
        {
            _soundOn = false;
            SoundBtn.Content = "Muted";
            SoundBtn.Opacity = 0.6;
        }
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
        var client = new RemoteClient { StartWithAudio = _soundOn };
        _client = client;
        _client.Connected += (w, h) => Dispatcher.Invoke(() => OnConnected(w, h));
        _client.FrameDecoded += rects => Dispatcher.BeginInvoke(() => OnFrame(client, rects), DispatcherPriority.Send);
        // Invoke (not BeginInvoke) so the bitmap is resized before any frame of the new size is queued.
        _client.ScreenChanged += (w, h) => Dispatcher.Invoke(() => ResizeScreen(w, h));
        _client.MonitorsChanged += (mons, cur) => Dispatcher.BeginInvoke(() => OnMonitors(mons, cur));
        _client.CodecChanged += name => Dispatcher.BeginInvoke(() => _codecName = name);
        _client.ClipboardImageReceived += png => Dispatcher.BeginInvoke(() => SetClipboardImage(png));
        _client.ClipboardFilesReceived += paths => Dispatcher.BeginInvoke(() => SetClipboardFiles(paths));
        _client.Disconnected += reason => Dispatcher.Invoke(() => OnDisconnected(reason));
        _client.DirListing += (path, entries) => Dispatcher.Invoke(() => OnDirListing(path, entries));
        _client.ClipboardReceived += t => Dispatcher.BeginInvoke(() =>
        {
            try { Clipboard.SetText(t); _clipSeq = GetClipboardSequenceNumber(); } catch { }
        });
        _client.TransferStatus += s => Dispatcher.Invoke(() => ShowToast(s));
        _client.Start(string.IsNullOrWhiteSpace(_pc.Host) ? _pc.Id : _pc.Host, Net.TcpPort, key);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    private uint _clipSeq = GetClipboardSequenceNumber();
    private DispatcherTimer? _clipTimer;

    /// <summary>Send the local clipboard (files, text or an image) to the host whenever it changes, while this tab is visible.</summary>
    private void StartClipboardWatch()
    {
        _clipTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _clipTimer.Tick -= ClipTick;
        _clipTimer.Tick += ClipTick;
        _clipTimer.Start();
    }

    private void ClipTick(object? s, EventArgs e)
    {
        if (_closed || !IsVisible) return;
        uint seq = GetClipboardSequenceNumber();
        if (seq == _clipSeq) return;
        _clipSeq = seq;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (paths.Count == 0) return;
                if (MeasureBytes(paths) > MaxClipBytes)
                {
                    ShowToast("Too big to copy through the clipboard \u2014 drag it onto the screen instead");
                    return;
                }
                _ = _client?.SendClipFilesAsync(paths);
            }
            else if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText();
                if (!string.IsNullOrEmpty(t) && t.Length <= 1_000_000) _client?.SendClipboard(t);
            }
            else if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img == null) return;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using var ms = new MemoryStream();
                enc.Save(ms);
                if (ms.Length <= 32 * 1024 * 1024) _client?.SendClipboardImage(ms.ToArray());
            }
        }
        catch { }
    }

    private const long MaxClipBytes = 512L * 1024 * 1024;

    private static long MeasureBytes(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p)) total += new FileInfo(p).Length;
                else if (Directory.Exists(p))
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    {
                        total += new FileInfo(f).Length;
                        if (total > MaxClipBytes) return total;
                    }
            }
            catch { }
        }
        return total;
    }

    private void SetClipboardImage(byte[] png)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            Clipboard.SetImage(bmp);
            _clipSeq = GetClipboardSequenceNumber();
        }
        catch { }
    }

    private void SetClipboardFiles(List<string> paths)
    {
        try
        {
            var sc = new System.Collections.Specialized.StringCollection();
            sc.AddRange(paths.ToArray());
            Clipboard.SetFileDropList(sc);
            _clipSeq = GetClipboardSequenceNumber();
            ShowToast($"Copied {paths.Count} item(s) from the other PC \u2014 paste with Ctrl+V");
        }
        catch { }
    }

    // --- monitors / sound ---

    private List<MonitorInfo> _monitors = new();
    private int _monitorIdx = -1;     // the display the user picked (kept across reconnects)
    private bool _soundOn = true;
    private string _codecName = "JPEG";

    private void OnMonitors(List<MonitorInfo> mons, int current)
    {
        _monitors = mons;
        MonitorBtn.Visibility = mons.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_monitorIdx >= 0 && _monitorIdx < mons.Count && current != _monitorIdx)
        {
            _client?.SelectMonitor(_monitorIdx); // reconnect: go back to the display the user chose
            return;
        }
        UpdateMonitorLabel(current);
    }

    private void UpdateMonitorLabel(int current)
    {
        MonitorBtn.Content = _monitors.Count > 1 ? $"Screen {current + 1} of {_monitors.Count}" : "Screen 1";
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (_monitors.Count < 2 || _client == null) return;
        int cur = _monitorIdx >= 0 ? _monitorIdx : Math.Max(0, _monitors.FindIndex(m => m.Primary));
        _monitorIdx = (cur + 1) % _monitors.Count;
        UpdateMonitorLabel(_monitorIdx);
        _client.SelectMonitor(_monitorIdx);
    }

    private void Sound_Click(object sender, RoutedEventArgs e)
    {
        _soundOn = !_soundOn;
        SoundBtn.Content = _soundOn ? "Sound on" : "Muted";
        SoundBtn.Opacity = _soundOn ? 1.0 : 0.6;
        if (_client != null) _client.AudioOn = _soundOn;
    }

    /// <summary>The host switched display or resolution: recreate the bitmap at the new size.</summary>
    private void ResizeScreen(int w, int h)
    {
        if (w <= 0 || h <= 0 || (w == _w && h == _h && _bmp != null)) return;
        _w = w; _h = h;
        _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        Screen.Source = _bmp;
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
        StartClipboardWatch();
        if (_qualityIdx != 0) ApplyQuality(); // re-assert the chosen preset after a reconnect
        Focus();
    }

    private void OnFrame(RemoteClient client, List<DecodedRect> rects)
    {
        // Pixels are already decoded off-thread; this is just a fast memcpy into the bitmap.
        foreach (var r in rects)
        {
            if (_bmp != null && r.X + r.W <= _w && r.Y + r.H <= _h)
            {
                try { _bmp.WritePixels(new Int32Rect(r.X, r.Y, r.W, r.H), r.Bgra, r.Stride, 0); }
                catch { /* next full frame heals it */ }
            }
            if (r.Pooled)
            {
                System.Threading.Interlocked.Decrement(ref client.UiBacklog);
                client.Recycle(r);
            }
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

    // --- handoff ---

    private void Handoff_Click(object sender, RoutedEventArgs e) => HandoffPick();

    /// <summary>Show a folder picker and hand off the chosen project. Used by the button and the drag fallback.</summary>
    public void HandoffPick()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick a project to hand off to the other PC",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Projects")
        };
        if (dlg.ShowDialog() == true)
            BeginHandoff(dlg.FolderName);
    }

    /// <summary>Start a handoff of <paramref name="folder"/> to this session's PC.</summary>
    public void BeginHandoff(string folder)
    {
        if (_client == null || string.IsNullOrEmpty(folder)) return;
        ShowToast("Handing off " + Path.GetFileName(folder.TrimEnd('\\', '/')) + "…");
        _ = _client.HandoffAsync(folder);
    }

    // --- quality preset ---

    private static readonly (string Name, int High, int Low)[] QualityPresets =
        { ("Balanced", 88, 60), ("Sharp", 96, 85), ("Smooth", 65, 40) };
    private int _qualityIdx;

    private void Quality_Click(object sender, RoutedEventArgs e)
    {
        _qualityIdx = (_qualityIdx + 1) % QualityPresets.Length;
        ApplyQuality();
    }

    private void ApplyQuality()
    {
        var q = QualityPresets[_qualityIdx];
        QualityBtn.Content = q.Name;
        _client?.SendQuality(q.High, q.Low);
        _client?.RequestFullFrame();
    }

    // --- stats overlay ---

    private DispatcherTimer? _statsTimer;
    private bool _statsOn;
    private long _lastFrames, _lastBytes;
    private DateTime _lastStat = DateTime.UtcNow;

    private void StatsToggle_Click(object sender, RoutedEventArgs e)
    {
        _statsOn = !_statsOn;
        StatsText.Visibility = _statsOn ? Visibility.Visible : Visibility.Collapsed;
        StatsToggle.Opacity = _statsOn ? 1.0 : 0.6;
        if (_statsOn && _statsTimer == null)
        {
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += StatsTick;
            _statsTimer.Start();
        }
    }

    private void StatsTick(object? sender, EventArgs e)
    {
        if (!_statsOn || _client == null) return;
        long f = _client.TotalFrames, b = _client.TotalBytes;
        double dt = (DateTime.UtcNow - _lastStat).TotalSeconds;
        if (dt > 0)
        {
            double fps = (f - _lastFrames) / dt;
            double kbs = (b - _lastBytes) / 1024.0 / dt;
            StatsText.Text = $"{fps:N0} fps  \u00b7  {kbs:N0} KB/s  \u00b7  {_codecName}";
        }
        _lastFrames = f; _lastBytes = b; _lastStat = DateTime.UtcNow;
    }

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
        _statsTimer?.Stop();
        _client?.Dispose();
    }
}
