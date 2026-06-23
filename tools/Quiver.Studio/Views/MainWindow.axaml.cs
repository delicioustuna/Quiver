using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using Quiver.Studio.ViewModels;
using TextMateSharp.Grammars;

namespace Quiver.Studio.Views;

public partial class MainWindow : Window
{
    private readonly TextEditor _editor;

    public MainWindow()
    {
        InitializeComponent();

        _editor = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New,monospace"),
            FontSize = 13,
            ShowLineNumbers = true,
            Text = "// Globals: db, tx (read-only), g, schema\n"
                 + "// Press F5 to execute\n\n"
                 + "db.Diagnostics.GetStatistics()",
        };

        try
        {
            var registryOptions = new RegistryOptions(ThemeName.LightPlus);
            var installation = _editor.InstallTextMate(registryOptions);
            installation.SetGrammar(
                registryOptions.GetScopeByLanguageId(
                    registryOptions.GetLanguageByExtension(".cs").Id));
        }
        catch
        {
            // TextMate unavailable — plain text editing still works
        }

        EditorHost.Child = _editor;

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Results.TopLevel = this;
                vm.Results.PropertyChanged += OnResultsPropertyChanged;
            }
        };
    }

    private void RebuildResultColumns()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        ResultsGrid.Columns.Clear();
        for (var i = 0; i < vm.Results.Columns.Count; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = vm.Results.Columns[i],
                Binding = new Binding($"[{i}]") { Mode = BindingMode.OneWay },
            });
        }
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
        base.OnKeyDown(e);
    }

    private void OnResultsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultsViewModel.Columns))
            RebuildResultColumns();
    }

    private async Task ExecuteQueryAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var code = _editor.Text;
        if (string.IsNullOrWhiteSpace(code)) return;
        await vm.QueryEditor.ExecuteAsync(code);
    }
}
