using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quiver.Studio.Views;

public partial class SettingsPage : UserControl
{
    public event Action? CloseRequested;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }
}
