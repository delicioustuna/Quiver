using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.TextMate;
using Quiver.Studio.Models;
using Quiver.Studio.Services;
using Quiver.Studio.ViewModels;
using TextMateSharp.Grammars;

namespace Quiver.Studio.Views;

public partial class QueryEditorView : UserControl
{
    private TextMate.Installation? _textMateInstallation;
    private IntellisenseService? _intellisenseService;
    private CompletionWindow? _completionWindow;
    private CancellationTokenSource? _completionCts;

    public QueryEditorView()
    {
        InitializeComponent();

        QueryTextEditor.Text = "// Globals: db, tx (read-only), g, schema\n"
                             + "// Press F5 to execute\n\n"
                             + "g.Nodes().ToList()";

        ApplyTextMateTheme(isDark: false);
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyTextMateTheme(ActualThemeVariant == ThemeVariant.Dark);
        };

        QueryTextEditor.TextArea.TextEntered += OnTextEntered;
        QueryTextEditor.TextArea.KeyDown += OnEditorKeyDown;

        Loaded += (_, _) =>
        {
            this.FindAncestorOfType<MainWindow>()?.RegisterQueryEditor(this);
        };
    }

    public void SetIntellisenseService(IntellisenseService service) =>
        _intellisenseService = service;

    public string GetQueryText() => QueryTextEditor.Text;
    public void SetQueryText(string text) => QueryTextEditor.Text = text;

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

    public event Action? ExecuteRequested;

    private void OnExecuteClick(object? sender, RoutedEventArgs e) =>
        ExecuteRequested?.Invoke();

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == ".")
            _ = ShowCompletionAsync(immediate: true);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = ShowCompletionAsync(immediate: true);
            e.Handled = true;
        }
    }

    private async Task ShowCompletionAsync(bool immediate)
    {
        if (_intellisenseService is null)
            return;

        _completionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _completionCts = cts;

        try
        {
            if (!immediate)
                await Task.Delay(150, cts.Token);

            var code = QueryTextEditor.Text;
            var caretOffset = QueryTextEditor.CaretOffset;

            var entries = await _intellisenseService.GetCompletionsAsync(code, caretOffset, cts.Token);
            if (entries.Count == 0 || cts.Token.IsCancellationRequested)
                return;

            _completionWindow?.Close();
            _completionWindow = new CompletionWindow(QueryTextEditor.TextArea);
            var data = _completionWindow.CompletionList.CompletionData;
            foreach (var entry in entries)
                data.Add(new RoslynCompletionData(entry));

            _completionWindow.Show();
            _completionWindow.Closed += (_, _) => _completionWindow = null;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
