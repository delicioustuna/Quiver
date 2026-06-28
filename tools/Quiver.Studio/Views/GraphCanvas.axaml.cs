using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Views;

public partial class GraphCanvas : UserControl
{
    public GraphCanvas()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is GraphCanvasViewModel vm)
                CanvasPanel.Attach(vm);
        };
    }

    private void OnFitClick(object? sender, RoutedEventArgs e)
    {
        CanvasPanel.FitToScreen();
    }

    private async void OnExportSvgClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphCanvasViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export SVG",
            DefaultExtension = "svg",
            SuggestedFileName = "graph",
            FileTypeChoices =
            [
                new FilePickerFileType("SVG") { Patterns = ["*.svg"] },
            ],
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (path is null) return;

        var svg = vm.ExportSvg();
        await File.WriteAllTextAsync(path, svg);
    }

    private async void OnExportPngClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphCanvasViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export PNG",
            DefaultExtension = "png",
            SuggestedFileName = "graph",
            FileTypeChoices =
            [
                new FilePickerFileType("PNG") { Patterns = ["*.png"] },
            ],
        });
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (path is null) return;

        vm.ExportPng(path);
    }
}
