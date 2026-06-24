using Avalonia.Controls;
using Avalonia.Input;
using Quiver.Studio.Models;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Views;

public partial class QueryHistoryPanel : UserControl
{
    public QueryHistoryPanel()
    {
        InitializeComponent();
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is QueryHistoryEntry entry
            && DataContext is QueryHistoryViewModel vm)
        {
            vm.LoadEntryCommand.Execute(entry);
        }
    }
}
