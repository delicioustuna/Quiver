using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Quiver.Studio.Services;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Views;

public partial class MainWindow : Window
{
    private SettingsService? _settingsService;

    public MainWindow()
    {
        InitializeComponent();

        SettingsView.CloseRequested += () =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.IsSettingsOpen = false;
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Results.TopLevel = this;
                vm.Results.PropertyChanged += OnResultsPropertyChanged;
                vm.PropertyChanged += OnViewModelPropertyChanged;
                vm.QueryHistory.LoadRequested += OnHistoryLoadRequested;

                Workspace.ApplyTextMateTheme(vm.IsDarkTheme);
                if (Application.Current is not null && vm.IsDarkTheme)
                    Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            }
        };

        Workspace.ExecuteRequested += () => _ = ExecuteQueryAsync();
    }

    public void Initialize(SettingsService settingsService, IntellisenseService intellisenseService)
    {
        _settingsService = settingsService;
        Workspace.SetIntellisenseService(intellisenseService);
        RestoreWindowState();
    }

    private void RestoreWindowState()
    {
        var ws = _settingsService?.Settings.WindowState;
        if (ws is null) return;

        if (ws.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            Width = ws.Width;
            Height = ws.Height;
            Position = new PixelPoint(ws.X, ws.Y);
        }
    }

    private void SaveWindowState()
    {
        if (_settingsService is null) return;

        _settingsService.Settings.WindowState = new Models.WindowStateData
        {
            X = Position.X,
            Y = Position.Y,
            Width = (int)ClientSize.Width,
            Height = (int)ClientSize.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        };
        _settingsService.SaveImmediate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveWindowState();
        base.OnClosing(e);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsDarkTheme)) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var variant = vm.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant = variant;

        Workspace.ApplyTextMateTheme(vm.IsDarkTheme);
    }

    private void OnResultsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultsViewModel.Columns))
            Workspace.RebuildResultColumns();
    }

    private void OnHistoryLoadRequested(string code)
    {
        Workspace.SetQueryText(code);
    }

    private async void OnOpenDatabaseClick(object? sender, RoutedEventArgs e)
    {
        await OpenDatabaseAsync();
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsSettingsOpen = !vm.IsSettingsOpen;
    }

    private async Task OpenDatabaseAsync()
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
                    Margin = new Thickness(16),
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

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        await ExecuteQueryAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _ = ExecuteQueryAsync();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.O:
                    _ = OpenDatabaseAsync();
                    e.Handled = true;
                    return;
                case Key.W:
                    if (DataContext is MainWindowViewModel vm && vm.IsConnected)
                        vm.CloseDatabaseCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemComma:
                    if (DataContext is MainWindowViewModel vm2)
                        vm2.IsSettingsOpen = !vm2.IsSettingsOpen;
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }

    private async Task ExecuteQueryAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (vm.IsFullTextMode)
        {
            await vm.FullTextSearch.ExecuteAsync();
        }
        else
        {
            var code = Workspace.GetQueryText();
            if (string.IsNullOrWhiteSpace(code)) return;
            await vm.QueryEditor.ExecuteAsync(code);
        }
    }
}
