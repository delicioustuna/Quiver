using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Quiver.Studio.Docking;
using Quiver.Studio.Models;
using Quiver.Studio.Services;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Views;

public partial class MainWindow : Window
{
    private SettingsService? _settingsService;
    private IntellisenseService? _intellisenseService;
    private QueryExecutionService? _queryExecutionService;
    private ApiDocumentationService? _apiDocService;
    private StudioDockFactory? _dockFactory;
    private QueryEditorView? _queryEditor;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;

            vm.Results.TopLevel = this;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.QueryHistory.LoadRequested += OnHistoryLoadRequested;

            // Order matters: Factory before Layout, InitLayout before binding.
            // DockControl.Layout fires Initialize() which requires Factory to be set.
            _dockFactory = new StudioDockFactory();
            var layout = _dockFactory.CreateLayout(vm);
            _dockFactory.InitLayout(layout);

            DockControl.Factory = _dockFactory;
            vm.DockFactory = _dockFactory;
            vm.DockLayout = layout;

            RebuildRecentFilesMenu();

            if (Application.Current is not null && vm.IsDarkTheme)
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        };

        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        TitleBar.DoubleTapped += OnTitleBarDoubleTapped;
    }

    private static bool IsMenuSource(object? source)
    {
        for (var c = source as Control; c is not null; c = c.Parent as Control)
        {
            if (c is Menu or MenuItem)
                return true;
        }
        return false;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsMenuSource(e.Source) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsMenuSource(e.Source))
            return;
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    public void Initialize(
        SettingsService settingsService,
        IntellisenseService intellisenseService,
        QueryExecutionService queryExecutionService,
        ApiDocumentationService apiDocService)
    {
        _settingsService = settingsService;
        _intellisenseService = intellisenseService;
        _queryExecutionService = queryExecutionService;
        _apiDocService = apiDocService;
        RestoreWindowState();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = WarmupServicesAsync();
    }

    private async Task WarmupServicesAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        vm.InitializationStatus = "Roslyn エンジン初期化中…";
        _queryExecutionService?.Warmup();

        vm.InitializationStatus = "IntelliSense 初期化中…";
        if (_intellisenseService is not null)
            await _intellisenseService.InitializeAsync();

        vm.InitializationStatus = "API ドキュメント構築中…";
        if (_apiDocService is not null)
        {
            await _apiDocService.InitializeAsync();
            vm.ApiDocumentation.Refresh();
        }

        vm.InitializationStatus = null;
    }

    public void RegisterQueryEditor(QueryEditorView editor)
    {
        _queryEditor = editor;
        if (_intellisenseService is not null)
            editor.SetIntellisenseService(_intellisenseService);
        editor.ExecuteRequested += () => _ = ExecuteQueryAsync();
        editor.ShowApiDocRequested += OnShowApiDocRequested;
    }

    private void OnShowApiDocRequested(string docId)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (_dockFactory is not null && !_dockFactory.IsPanelVisible("apiDoc"))
            _dockFactory.TogglePanel("apiDoc");

        vm.ApiDocumentation.NavigateToDocId(docId);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsDarkTheme):
                if (DataContext is MainWindowViewModel vm)
                {
                    var variant = vm.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
                    if (Application.Current is not null)
                        Application.Current.RequestedThemeVariant = variant;
                }
                break;

            case nameof(MainWindowViewModel.RecentFiles):
                RebuildRecentFilesMenu();
                break;
        }
    }

    private void RebuildRecentFilesMenu()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        RecentFilesMenu.Items.Clear();
        foreach (var entry in vm.RecentFiles)
        {
            var item = new MenuItem { Header = entry.Path, Tag = entry.Path };
            item.Click += OnRecentFileItemClick;
            RecentFilesMenu.Items.Add(item);
        }
    }

    private void OnRecentFileItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path } && DataContext is MainWindowViewModel vm)
        {
            try { vm.OpenDatabase(path); }
            catch { }
        }
    }

    private void OnViewMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem viewMenu || _dockFactory is null) return;

        foreach (var item in viewMenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string panelId)
            {
                var visible = _dockFactory.IsPanelVisible(panelId);
                item.Icon = visible
                    ? new TextBlock { Text = "✓", FontSize = 14 }
                    : null;
            }
        }
    }

    private void OnHistoryLoadRequested(string code)
    {
        _queryEditor?.SetQueryText(code);
    }

    private async void OnOpenDatabaseClick(object? sender, RoutedEventArgs e) =>
        await OpenDatabaseAsync();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTogglePanelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string panelId })
            _dockFactory?.TogglePanel(panelId);
    }

    private async void OnExecuteClick(object? sender, RoutedEventArgs e) =>
        await ExecuteQueryAsync();

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
                    _dockFactory?.TogglePanel("settings");
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }

    private async Task ExecuteQueryAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var code = _queryEditor?.GetQueryText();
        if (!string.IsNullOrWhiteSpace(code))
            await vm.QueryEditor.ExecuteAsync(code);
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
                Title = Studio.Resources.Strings.Error,
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
                            Text = $"{Studio.Resources.Strings.ErrorOpenDatabase}\n{ex.Message}",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                    },
                },
            };
            await dialog.ShowDialog(this);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveWindowState();
        base.OnClosing(e);
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

        _settingsService.Settings.WindowState = new WindowStateData
        {
            X = Position.X,
            Y = Position.Y,
            Width = (int)ClientSize.Width,
            Height = (int)ClientSize.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        };
        _settingsService.SaveImmediate();
    }
}
