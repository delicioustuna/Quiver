using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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
    private CancellationTokenSource? _hoverCts;
    private Popup? _hoverPopup;
    private DispatcherTimer? _hoverTimer;
    private Point _lastPointerPosition;

    public event Action<string>? ShowApiDocRequested;

    public QueryEditorView()
    {
        InitializeComponent();

        QueryTextEditor.Text = "// Globals: db, tx (read-only), g, schema\n"
                             + "// Press F5 to execute\n\n"
                             + "g.Vertices().ToList()";

        ApplyTextMateTheme(isDark: false);
        ActualThemeVariantChanged += (_, _) =>
        {
            ApplyTextMateTheme(ActualThemeVariant == ThemeVariant.Dark);
        };

        QueryTextEditor.TextArea.TextEntered += OnTextEntered;
        QueryTextEditor.TextArea.KeyDown += OnEditorKeyDown;
        QueryTextEditor.PointerMoved += OnEditorPointerMoved;

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hoverTimer.Tick += OnHoverTimerTick;

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
        else if (e.Key == Key.F1)
        {
            _ = LookupApiDocAsync();
            e.Handled = true;
        }
    }

    private async Task LookupApiDocAsync()
    {
        if (_intellisenseService is null) return;

        var code = QueryTextEditor.Text;
        var caretOffset = QueryTextEditor.CaretOffset;

        var docId = await _intellisenseService.GetDocIdAtPositionAsync(code, caretOffset, CancellationToken.None);
        if (docId is not null)
            ShowApiDocRequested?.Invoke(docId);
    }

    private void OnEditorPointerMoved(object? sender, PointerEventArgs e)
    {
        var newPos = e.GetPosition(QueryTextEditor);
        var delta = _lastPointerPosition - newPos;
        if (Math.Abs(delta.X) > 2 || Math.Abs(delta.Y) > 2)
        {
            CloseHoverPopup();
            _lastPointerPosition = newPos;
            _hoverTimer?.Stop();
            _hoverTimer?.Start();
        }
    }

    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        _hoverTimer?.Stop();
        _ = ShowHoverTooltipAsync(_lastPointerPosition);
    }

    private async Task ShowHoverTooltipAsync(Point editorPos)
    {
        if (_intellisenseService is null) return;

        _hoverCts?.Cancel();
        var cts = new CancellationTokenSource();
        _hoverCts = cts;

        try
        {
            var pos = QueryTextEditor.GetPositionFromPoint(editorPos);
            if (pos is null) return;

            var offset = QueryTextEditor.Document.GetOffset(pos.Value.Location);
            var code = QueryTextEditor.Text;

            var info = await _intellisenseService.GetHoverInfoAsync(code, offset, cts.Token);
            if (info is null || cts.Token.IsCancellationRequested) return;

            CloseHoverPopup();
            BuildAndShowHoverPopup(info);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void BuildAndShowHoverPopup(HoverInfo info)
    {
        var panel = new StackPanel
        {
            Spacing = 4,
            MaxWidth = 500,
        };

        panel.Children.Add(new TextBlock
        {
            Text = info.Signature,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New,monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrEmpty(info.Summary))
        {
            panel.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 2),
                Background = Brushes.Gray,
                Opacity = 0.3,
            });
            panel.Children.Add(new TextBlock
            {
                Text = info.Summary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (info.Parameters.Count > 0)
        {
            var paramsPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var (name, desc) in info.Parameters)
            {
                var paramRow = new WrapPanel();
                paramRow.Children.Add(new TextBlock
                {
                    Text = name,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New,monospace"),
                    FontSize = 11,
                    Foreground = Brushes.CornflowerBlue,
                    Margin = new Thickness(0, 0, 6, 0),
                });
                paramRow.Children.Add(new TextBlock
                {
                    Text = desc,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                });
                paramsPanel.Children.Add(paramRow);
            }
            panel.Children.Add(paramsPanel);
        }

        if (!string.IsNullOrEmpty(info.Returns))
        {
            var returnsRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            returnsRow.Children.Add(new TextBlock
            {
                Text = "Returns: ",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.Gray,
            });
            returnsRow.Children.Add(new TextBlock
            {
                Text = info.Returns,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(returnsRow);
        }

        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0, OffsetY = 2,
                Blur = 8,
                Color = Color.FromArgb(40, 0, 0, 0),
            }),
        };
        border.Bind(Border.BackgroundProperty,
            this.GetResourceObservable("SystemControlBackgroundAltHighBrush"));

        _hoverPopup = new Popup
        {
            Child = border,
            Placement = PlacementMode.Pointer,
            IsLightDismissEnabled = true,
            HorizontalOffset = 0,
            VerticalOffset = 16,
        };

        ((ISetLogicalParent)_hoverPopup).SetParent(this);
        _hoverPopup.Open();
    }

    private void CloseHoverPopup()
    {
        _hoverCts?.Cancel();
        if (_hoverPopup is not null)
        {
            _hoverPopup.Close();
            _hoverPopup = null;
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
