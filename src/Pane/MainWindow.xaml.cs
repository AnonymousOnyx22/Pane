using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Pane;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public ObservableCollection<TabVm> Tabs { get; } = new();

    private object? _selectedContent;
    public object? SelectedContent
    {
        get => _selectedContent;
        set { _selectedContent = value; Changed(nameof(SelectedContent)); }
    }

    private readonly ObservableCollection<SavedPc> _pcs = new();
    private readonly ObservableCollection<DiscoveredHost> _found = new();
    private readonly Dictionary<string, DateTime> _lastSeen = new(); // key: host guid or ip
    private readonly DiscoveryListener _discovery = new();
    private readonly DispatcherTimer _presenceTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        foreach (var p in PcStore.Load()) _pcs.Add(p);
        _pcs.CollectionChanged += (_, __) => PcStore.Save(_pcs);
        foreach (var p in _pcs) p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SavedPc.Name)) PcStore.Save(_pcs); };

        var grid = new GridView(_pcs, _found);
        grid.Open += OpenSession;
        grid.AddRequested += OnAddRequested;
        grid.AddDiscovered += OnAddDiscovered;
        var gridTab = new TabVm("Grid", grid, closable: false);
        Tabs.Add(gridTab);
        SelectTab(gridTab);

        Opacity = 0;
        SizeChanged += (_, __) => UpdateClip();
        Loaded += (_, __) => { UpdateClip(); PlayIntro(); };

        _discovery.HostSeen += OnHostSeen;
        try { _discovery.Start(); } catch { /* another listener may hold the port */ }

        _presenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _presenceTimer.Tick += PrunePresence;
        _presenceTimer.Start();

        Closed += (_, __) => { _discovery.Dispose(); foreach (var t in Tabs) (t.Content as IDisposable)?.Dispose(); };
    }

    private void UpdateClip() => ClipGeo.Rect = new Rect(0, 0, ActualWidth, ActualHeight);

    // --- intro + transitions ---

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private void PlayIntro()
    {
        BeginAnimation(OpacityProperty, Anim(0, 1, 220));
        Animate(IntroScale, ScaleTransform.ScaleXProperty, 0.985, 1, 520);
        Animate(IntroScale, ScaleTransform.ScaleYProperty, 0.985, 1, 520);

        IntroWord.BeginAnimation(OpacityProperty, Anim(0, 1, 380));
        Animate(IntroWordScale, ScaleTransform.ScaleXProperty, 0.9, 1, 560);
        Animate(IntroWordScale, ScaleTransform.ScaleYProperty, 0.9, 1, 560);
        Animate(IntroWordMove, TranslateTransform.YProperty, 6, 0, 560);

        var fade = Anim(1, 0, 420, beginMs: 620);
        fade.Completed += (_, __) => Intro.Visibility = Visibility.Collapsed;
        Intro.BeginAnimation(OpacityProperty, fade);
    }

    private void AnimateContentIn()
    {
        ContentHost.BeginAnimation(OpacityProperty, Anim(0, 1, 200));
        Animate(ContentSlide, TranslateTransform.YProperty, 10, 0, 240);
    }

    private static DoubleAnimation Anim(double from, double to, double ms, double beginMs = 0) => new()
    {
        From = from,
        To = to,
        Duration = TimeSpan.FromMilliseconds(ms),
        BeginTime = TimeSpan.FromMilliseconds(beginMs),
        EasingFunction = EaseOut
    };

    private static void Animate(Animatable target, System.Windows.DependencyProperty prop, double from, double to, double ms)
        => target.BeginAnimation(prop, Anim(from, to, ms));

    // --- discovery / presence ---

    private void OnHostSeen(DiscoveredHost h)
    {
        Dispatcher.Invoke(() =>
        {
            var match = _pcs.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(p.Id) && p.Id == h.HostId.ToString()) ||
                string.Equals(p.Host, h.Ip, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                if (string.IsNullOrEmpty(match.Id)) match.Id = h.HostId.ToString();
                match.Online = true;
                _lastSeen[PresenceKey(match)] = DateTime.UtcNow;
            }
            else
            {
                _lastSeen["found:" + h.HostId] = DateTime.UtcNow;
                if (!_found.Any(f => f.HostId == h.HostId)) _found.Add(h);
            }
        });
    }

    private static string PresenceKey(SavedPc p) => !string.IsNullOrEmpty(p.Id) ? p.Id : p.Host.ToLowerInvariant();

    private void PrunePresence(object? s, EventArgs e)
    {
        var now = DateTime.UtcNow;
        foreach (var p in _pcs)
        {
            if (_lastSeen.TryGetValue(PresenceKey(p), out var seen) && (now - seen).TotalSeconds < 6)
                p.Online = true;
            else
                p.Online = false;
        }
        for (int i = _found.Count - 1; i >= 0; i--)
        {
            var h = _found[i];
            if (!_lastSeen.TryGetValue("found:" + h.HostId, out var seen) || (now - seen).TotalSeconds >= 6)
                _found.RemoveAt(i);
        }
    }

    // --- tabs ---

    private void OpenSession(SavedPc pc)
    {
        var existing = Tabs.FirstOrDefault(t => t.Content is RemoteView rv && ReferenceEquals(rv.Tag, pc));
        if (existing != null) { SelectTab(existing); return; }

        var view = new RemoteView(pc) { Tag = pc };
        var tab = new TabVm(pc.Name, view, closable: true);
        Tabs.Add(tab);
        SelectTab(tab);
    }

    private void SelectTab(TabVm tab)
    {
        foreach (var t in Tabs) t.IsSelected = ReferenceEquals(t, tab);
        SelectedContent = tab.Content;
        AnimateContentIn();
        if (tab.Content is RemoteView rv)
            Dispatcher.BeginInvoke(new Action(() => { rv.Focus(); Keyboard.Focus(rv); }), DispatcherPriority.Input);
    }

    private void CloseTab(TabVm tab)
    {
        if (!tab.Closable) return;
        int idx = Tabs.IndexOf(tab);
        (tab.Content as IDisposable)?.Dispose();
        Tabs.Remove(tab);
        var next = Tabs.ElementAtOrDefault(Math.Max(0, idx - 1)) ?? Tabs[0];
        SelectTab(next);
    }

    private void OnAddRequested()
    {
        var dlg = new AddPcDialog { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            dlg.Result.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SavedPc.Name)) PcStore.Save(_pcs); };
            _pcs.Add(dlg.Result);
        }
    }

    private void OnAddDiscovered(DiscoveredHost h)
    {
        var dlg = new AddPcDialog(h.Name, h.Ip) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            dlg.Result.Id = h.HostId.ToString();
            _pcs.Add(dlg.Result);
            _found.Remove(h);
        }
    }

    // --- title bar / window buttons ---

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1) DragMove();
    }

    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TabVm tab) SelectTab(tab);
        e.Handled = true;
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TabVm tab) CloseTab(tab);
        e.Handled = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
