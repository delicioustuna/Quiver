using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using AvaloniaEdit.TextMate;
using Quiver.Studio.ViewModels;
using TextMateSharp.Grammars;

namespace Quiver.Studio.Views;

public partial class WorkspaceView : UserControl
{
    private TextMate.Installation? _textMateInstallation;

    public WorkspaceView()
    {
        InitializeComponent();

        QueryTextEditor.Text = "// Globals: db, tx (read-only), g, schema\n"
                             + "// Press F5 to execute\n\n"
                             + "// Graph view: queries returning NodeId trigger the Graph tab\n"
                             + "g.Nodes().ToList()";

        ApplyTextMateTheme(isDark: false);
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyTextMateTheme(ActualThemeVariant == ThemeVariant.Dark);
        };
    }

    public void SetQueryText(string text) => QueryTextEditor.Text = text;
    public string GetQueryText() => QueryTextEditor.Text;

    public void ApplyTextMateTheme(bool isDark)
    {
        try
        {
            _textMateInstallation?.Dispose();
            var registryOptions = new RegistryOptions(isDark ? ThemeName.DarkPlus : ThemeName.LightPlus);
            _textMateInstallation = QueryTextEditor.InstallTextMate(registryOptions);
            _textMateInstallation.SetGrammar(
                registryOptions.GetScopeByLanguageId(
                    registryOptions.GetLanguageByExtension(".cs").Id));
        }
        catch
        {
        }
    }

    public void RebuildResultColumns()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        ResultsGrid.Columns.Clear();
        for (var i = 0; i < vm.Results.Columns.Count; i++)
        {
            ResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = vm.Results.Columns[i],
                Binding = new Binding($"[{i}]"),
            });
        }
    }

    private void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        ExecuteRequested?.Invoke();
    }

    public event Action? ExecuteRequested;
}
