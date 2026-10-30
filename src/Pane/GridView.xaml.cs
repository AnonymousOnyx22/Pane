using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Pane;

/// <summary>The first tab: rounded tiles for saved PCs, a plus tile to add one, and any hosts found on the LAN.</summary>
public partial class GridView : UserControl
{
    private const double TileW = 220, TileH = 128, Gap = 16;

    private readonly ObservableCollection<SavedPc> _pcs;
    private readonly ObservableCollection<DiscoveredHost> _found;

    public event Action<SavedPc>? Open;
    public event Action? AddRequested;
    public event Action<DiscoveredHost>? AddDiscovered;

    public GridView(ObservableCollection<SavedPc> pcs, ObservableCollection<DiscoveredHost> found)
    {
        InitializeComponent();
        _pcs = pcs;
        _found = found;
        _pcs.CollectionChanged += OnChanged;
        _found.CollectionChanged += OnChanged;
        foreach (var p in _pcs) p.PropertyChanged += OnPcChanged;
        RebuildPcs();
        RebuildFound();
    }

    private void OnChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (var o in e.NewItems) if (o is SavedPc p) p.PropertyChanged += OnPcChanged;
        if (s == _pcs) RebuildPcs(); else RebuildFound();
    }

    private void OnPcChanged(object? s, PropertyChangedEventArgs e) => RebuildPcs();

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private void RebuildPcs()
    {
        PcPanel.Children.Clear();
        int i = 0;
        foreach (var pc in _pcs)
        {
            var t = BuildPcTile(pc);
            PcPanel.Children.Add(t);
            Entrance(t, i++);
        }
        var add = BuildAddTile();
        PcPanel.Children.Add(add);
        Entrance(add, i);
    }

    private void RebuildFound()
    {
        FoundPanel.Children.Clear();
        int i = 0;
        foreach (var h in _found)
        {
            var t = BuildFoundTile(h);
            FoundPanel.Children.Add(t);
            Entrance(t, i++);
        }
        FoundLabel.Visibility = _found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Fade + rise each tile in, staggered by index for a smooth cascade.</summary>
    private void Entrance(Border tile, int index)
    {
        tile.Opacity = 0;
        var move = new TranslateTransform(0, 14);
        EnsureTransforms(tile, move);
        double begin = 40 * index;
        tile.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
            new Duration(TimeSpan.FromMilliseconds(320))) { BeginTime = TimeSpan.FromMilliseconds(begin), EasingFunction = EaseOut });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0,
            new Duration(TimeSpan.FromMilliseconds(360))) { BeginTime = TimeSpan.FromMilliseconds(begin), EasingFunction = EaseOut });
    }

    private static ScaleTransform EnsureTransforms(Border tile, TranslateTransform move)
    {
        var scale = new ScaleTransform(1, 1);
        tile.RenderTransformOrigin = new Point(0.5, 0.5);
        tile.RenderTransform = new TransformGroup { Children = { scale, move } };
        tile.Tag = scale; // keep the scale handy for hover animation
        return scale;
    }

    private Border TileShell()
    {
        var surface = ((SolidColorBrush)FindResource("Surface")).Color;
        return new Border
        {
            Width = TileW,
            Height = TileH,
            Margin = new Thickness(0, 0, Gap, Gap),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(surface), // fresh brush so Color can be animated per tile
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
    }

    private Border BuildPcTile(SavedPc pc)
    {
        var shell = TileShell();
        Hoverable(shell);

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var monitor = new TextBlock
        {
            Text = "▢",
            FontSize = 30,
            Foreground = (Brush)FindResource("FgDim"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Grid.SetRow(monitor, 0);

        var name = new TextBlock
        {
            Text = pc.Name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Fg"),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(name, 1);

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetRow(statusRow, 2);
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = pc.Online ? (Brush)FindResource("DotOnline") : (Brush)FindResource("DotOffline")
        };
        if (pc.Online)
        {
            dot.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString("#32D74B"),
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.9
            };
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.4,
                new Duration(TimeSpan.FromMilliseconds(950)))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = EaseOut });
        }
        var status = new TextBlock
        {
            Text = pc.StatusText,
            FontSize = 12,
            Margin = new Thickness(7, 0, 0, 0),
            Foreground = pc.Online ? (Brush)FindResource("DotOnline") : (Brush)FindResource("FgDim"),
            VerticalAlignment = VerticalAlignment.Center
        };
        statusRow.Children.Add(dot);
        statusRow.Children.Add(status);

        grid.Children.Add(monitor);
        grid.Children.Add(name);
        grid.Children.Add(statusRow);
        shell.Child = grid;

        shell.MouseLeftButtonUp += (_, __) => Open?.Invoke(pc);
        shell.MouseRightButtonUp += (_, e) => ShowPcMenu(pc, shell, e);
        return shell;
    }

    private void ShowPcMenu(SavedPc pc, UIElement anchor, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += (_, __) => _pcs.Remove(pc);
        menu.Items.Add(remove);
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private Border BuildAddTile()
    {
        var shell = TileShell();
        shell.Background = new SolidColorBrush(Colors.Transparent);
        Hoverable(shell);
        var plus = new TextBlock
        {
            Text = "+",
            FontSize = 34,
            Foreground = (Brush)FindResource("FgDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        shell.Child = plus;
        shell.MouseLeftButtonUp += (_, __) => AddRequested?.Invoke();
        return shell;
    }

    private Border BuildFoundTile(DiscoveredHost h)
    {
        var shell = TileShell();
        Hoverable(shell);
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock
        {
            Text = h.Name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Fg"),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(name, 0);
        var hint = new TextBlock
        {
            Text = h.Ip + "  ·  tap to add",
            FontSize = 12,
            Foreground = (Brush)FindResource("FgDim")
        };
        Grid.SetRow(hint, 1);
        grid.Children.Add(name);
        grid.Children.Add(hint);
        shell.Child = grid;
        shell.MouseLeftButtonUp += (_, __) => AddDiscovered?.Invoke(h);
        return shell;
    }

    private void Hoverable(Border b)
    {
        var baseColor = (b.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        var hoverColor = ((SolidColorBrush)FindResource("SurfaceHover")).Color;

        void Scale(double to)
        {
            if (b.Tag is ScaleTransform st)
            {
                var a = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = EaseOut };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
        }
        void Fill(Color to)
        {
            if (b.Background is SolidColorBrush sb)
                sb.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(to, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = EaseOut });
        }

        b.MouseEnter += (_, __) => { Scale(1.03); Fill(hoverColor); };
        b.MouseLeave += (_, __) => { Scale(1.0); Fill(baseColor); };
    }
}
