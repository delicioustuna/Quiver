using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quiver.Studio.Views;

public partial class AddNodeDialog : Window
{
    public string? ResultLabel { get; private set; }

    public AddNodeDialog()
    {
        InitializeComponent();
    }

    public AddNodeDialog(IReadOnlyList<string> existingLabels) : this()
    {
        LabelBox.ItemsSource = existingLabels;
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var label = LabelBox.Text?.Trim();
        if (string.IsNullOrEmpty(label)) return;
        ResultLabel = label;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
