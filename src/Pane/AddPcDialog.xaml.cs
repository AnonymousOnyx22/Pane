using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Pane;

public partial class AddPcDialog : Window
{
    public SavedPc? Result { get; private set; }

    public AddPcDialog(string name = "", string host = "", string key = "")
    {
        InitializeComponent();
        NameBox.Text = name;
        HostBox.Text = host;
        KeyBox.Text = key;
        Opacity = 0;
        Loaded += (_, __) =>
        {
            (string.IsNullOrEmpty(host) ? HostBox : KeyBox).Focus();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180))));
            var s = new DoubleAnimation(0.96, 1, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = ease };
            DlgScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, s);
            DlgScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, s);
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        var key = KeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host)) { Error("Enter the PC's IP address or hostname."); return; }
        if (!Protocol.PairingKey.TryDecode(key, out _)) { Error("That pairing key does not look right."); return; }

        Result = new SavedPc
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? host : NameBox.Text.Trim(),
            Host = host,
            Key = key
        };
        DialogResult = true;
    }

    private void Error(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
