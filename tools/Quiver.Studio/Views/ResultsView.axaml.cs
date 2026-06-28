using Avalonia.Controls;
using Avalonia.Data;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Views;

public partial class ResultsView : UserControl
{
    public ResultsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ResultsViewModel vm)
                vm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultsViewModel.Columns))
            RebuildColumns();
    }

    private void RebuildColumns()
    {
        if (DataContext is not ResultsViewModel vm) return;

        ResultsGrid.Columns.Clear();
        for (var i = 0; i < vm.Columns.Count; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = vm.Columns[i],
                Binding = new Binding($"[{i}]"),
            });
        }
    }
}
