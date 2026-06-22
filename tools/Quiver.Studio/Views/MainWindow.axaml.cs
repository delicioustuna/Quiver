using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnOpenDatabaseClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Quiver Database",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Quiver Database") { Patterns = ["*.quiver"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        if (DataContext is not MainWindowViewModel vm) return;

        try
        {
            vm.OpenDatabase(path);
        }
        catch (Exception ex)
        {
            var dialog = new Window
            {
                Title = "Error",
                Width = 400,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(16),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Failed to open database:\n{ex.Message}",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                    },
                },
            };
            await dialog.ShowDialog(this);
        }
    }

    private void OnCloseDatabaseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.CloseDatabase();
    }
}
