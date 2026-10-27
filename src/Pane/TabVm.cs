using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pane;

/// <summary>One open tab: the grid, or a live remote session.</summary>
public sealed class TabVm : INotifyPropertyChanged
{
    public string Title { get; }
    public bool Closable { get; }
    public object Content { get; }

    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Changed(nameof(IsSelected)); } }
    }

    public TabVm(string title, object content, bool closable)
    {
        Title = title; Content = content; Closable = closable;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}
