using Avalonia.Controls;
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
}
