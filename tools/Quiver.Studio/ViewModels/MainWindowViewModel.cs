using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Quiver.Studio.Models;
using Quiver.Studio.Services;
using R3;

namespace Quiver.Studio.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseService _db;
    private readonly GraphEditingService _editingService;
    private readonly SettingsService _settings;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IDisposable _subscriptions;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private DatabaseStatistics? _statistics;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    private bool _isDarkTheme;

    [ObservableProperty]
    private IReadOnlyList<RecentFileEntry> _recentFiles = [];

    [ObservableProperty]
    private int _queryModeIndex;

    [ObservableProperty]
    private bool _isSettingsOpen;

    public string Title => "Quiver Studio";

    public string ThemeLabel => IsDarkTheme ? "Light" : "Dark";

    public SchemaBrowserViewModel SchemaBrowser { get; }

    public QueryEditorViewModel QueryEditor { get; }

    public ResultsViewModel Results { get; } = new();

    public GraphCanvasViewModel GraphCanvas { get; }

    public PropertyInspectorViewModel PropertyInspector { get; }

    public FullTextSearchViewModel FullTextSearch { get; }

    public QueryHistoryViewModel QueryHistory { get; }

    public GraphSettingsViewModel GraphSettings { get; }

    public bool IsTraversalMode => QueryModeIndex == 0;
    public bool IsFullTextMode => QueryModeIndex == 1;

    public MainWindowViewModel(
        DatabaseService databaseService,
        QueryExecutionService queryService,
        GraphLayoutService layoutService,
        SugiyamaLayoutService hierarchyLayoutService,
        GraphEditingService editingService,
        SettingsService settingsService,
        ILogger<MainWindowViewModel> logger)
    {
        _db = databaseService;
        _editingService = editingService;
        _settings = settingsService;
        _logger = logger;

        IsDarkTheme = string.Equals(settingsService.Settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
        RecentFiles = settingsService.Settings.RecentFiles;

        SchemaBrowser = new SchemaBrowserViewModel(_db);
        QueryEditor = new QueryEditorViewModel(queryService, settingsService, databaseService);
        GraphCanvas = new GraphCanvasViewModel(databaseService, layoutService, hierarchyLayoutService, editingService, logger);
        PropertyInspector = new PropertyInspectorViewModel(databaseService);
        FullTextSearch = new FullTextSearchViewModel(databaseService);
        QueryHistory = new QueryHistoryViewModel(settingsService);
        GraphSettings = new GraphSettingsViewModel(settingsService, GraphCanvas);

        GraphCanvas.Renderer.VisualSettings = settingsService.Settings.GraphVisual;

        QueryEditor.ResultReady += OnResultReady;
        FullTextSearch.ResultReady += OnResultReady;
        GraphCanvas.PropertyChanged += OnGraphCanvasPropertyChanged;
        GraphCanvas.LinkCompleted += OnLinkCompleted;
        GraphCanvas.AddNodeRequested += OnAddNodeRequested;
        Results.PropertyChanged += OnResultsSelectionChanged;

        _subscriptions = Disposable.Combine(
            _db.IsOpen.Subscribe(v => Dispatcher.UIThread.Post(() =>
            {
                IsConnected = v;
                if (v) FullTextSearch.RefreshIndexes();
            })),
            _db.FilePath.Subscribe(v => Dispatcher.UIThread.Post(() => FilePath = v)),
            _db.Statistics.Subscribe(v => Dispatcher.UIThread.Post(() => Statistics = v)));
    }

    partial void OnQueryModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsTraversalMode));
        OnPropertyChanged(nameof(IsFullTextMode));
    }

    private void OnResultReady(QueryResult result)
    {
        QueryHistory.Refresh();

        if (result.IsError)
        {
            _logger.LogWarning("クエリエラー: {Error}", result.Error);
            Results.Clear();
            GraphCanvas.BuildFromResult(result);
            return;
        }

        try
        {
            if (result.IsTabular)
                Results.LoadResult(result);
            else
                Results.Clear();

            GraphCanvas.BuildFromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "クエリ結果処理で例外");
        }
    }

    private void OnGraphCanvasPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphCanvasViewModel.SelectedNode))
        {
            if (GraphCanvas.SelectedNode is { } node)
                PropertyInspector.InspectNode(node);
            else if (GraphCanvas.SelectedEdge is null)
                PropertyInspector.Clear();
        }
        else if (e.PropertyName == nameof(GraphCanvasViewModel.SelectedEdge))
        {
            if (GraphCanvas.SelectedEdge is { } edge)
                PropertyInspector.InspectEdge(edge);
            else if (GraphCanvas.SelectedNode is null)
                PropertyInspector.Clear();
        }
    }

    private void OnResultsSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ResultsViewModel.SelectedItem)) return;

        var nid = Results.GetSelectedNodeId();
        if (nid is { } id && _db.CurrentDatabase is not null)
        {
            using var tx = _db.CurrentDatabase.BeginReadOnlyTransaction();
            if (tx.NodeExists(id))
            {
                var label = tx.GetNodeLabel(id) ?? $"({id.Sequence})";
                var vn = new Models.VisualNode(id, label);
                PropertyInspector.InspectNode(vn);
                return;
            }
        }
        PropertyInspector.Clear();
    }

    public void OpenDatabase(string filePath)
    {
        _db.Open(filePath);
        _settings.AddRecentFile(filePath);
        RecentFiles = _settings.Settings.RecentFiles;
    }

    [RelayCommand]
    private void CloseDatabase()
    {
        _db.Close();
        Results.Clear();
        GraphCanvas.BuildFromResult(QueryResult.Empty(TimeSpan.Zero));
        QueryEditor.Reset();
        PropertyInspector.Clear();
        FullTextSearch.Clear();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        _settings.Settings.Theme = IsDarkTheme ? "Dark" : "Light";
        _settings.MarkDirty();
    }

    private async Task OnLinkCompleted(VisualNode source, VisualNode target)
    {
        try
        {
            var existingTypes = _db.CurrentDatabase?.Schema.ListRelationshipTypes() ?? [];
            var dialog = new Views.AddRelationshipDialog(existingTypes);
            var owner = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            var result = owner is not null ? await dialog.ShowDialog<object?>(owner) : null;
            if (result is not true || dialog.ResultType is null) return;

            var rid = _editingService.CreateRelationship(source.Id, target.Id, dialog.ResultType);
            GraphCanvas.AddEdgeToGraph(rid, source, target, dialog.ResultType);
            SchemaBrowser.Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "リレーションシップ作成失敗");
        }
    }

    private async Task OnAddNodeRequested()
    {
        await AddNodeAtPositionAsync(GraphCanvas.LastContextWorldX, GraphCanvas.LastContextWorldY);
    }

    public async Task AddNodeAtPositionAsync(double worldX, double worldY)
    {
        try
        {
            var existingLabels = _db.CurrentDatabase?.Schema.ListLabels() ?? [];
            var dialog = new Views.AddNodeDialog(existingLabels);
            var owner = Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
            var result = owner is not null ? await dialog.ShowDialog<object?>(owner) : null;
            if (result is not true || dialog.ResultLabel is null) return;

            var nid = _editingService.CreateNode(dialog.ResultLabel);
            GraphCanvas.AddNodeToGraph(nid, dialog.ResultLabel, worldX, worldY);
            SchemaBrowser.Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ノード作成失敗");
        }
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        SchemaBrowser.Dispose();
    }
}
