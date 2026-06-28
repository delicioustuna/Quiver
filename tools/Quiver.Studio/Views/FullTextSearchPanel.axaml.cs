using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Views;

public partial class FullTextSearchPanel : UserControl
{
    public FullTextSearchPanel()
    {
        InitializeComponent();
    }

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FullTextSearchViewModel vm)
            await vm.ExecuteAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is FullTextSearchViewModel vm && !vm.IsExecuting)
        {
            _ = vm.ExecuteAsync();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
