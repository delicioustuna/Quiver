using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quiver.Studio.Views;

public partial class AddRelationshipDialog : Window
{
    public string? ResultType { get; private set; }

    public AddRelationshipDialog()
    {
        InitializeComponent();
    }

    public AddRelationshipDialog(IReadOnlyList<string> existingTypes) : this()
    {
        TypeBox.ItemsSource = existingTypes;
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var type = TypeBox.Text?.Trim();
        if (string.IsNullOrEmpty(type)) return;
        ResultType = type;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
