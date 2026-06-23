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
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IDisposable _subscriptions;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private DatabaseStatistics? _statistics;

    public string Title => "Quiver Studio";

    public SchemaBrowserViewModel SchemaBrowser { get; }

    public QueryEditorViewModel QueryEditor { get; }

    public ResultsViewModel Results { get; } = new();

    public GraphCanvasViewModel GraphCanvas { get; }

    public MainWindowViewModel(
        DatabaseService databaseService,
        QueryExecutionService queryService,
        GraphLayoutService layoutService,
        ILogger<MainWindowViewModel> logger)
    {
        _db = databaseService;
        _logger = logger;
        SchemaBrowser = new SchemaBrowserViewModel(_db);
        QueryEditor = new QueryEditorViewModel(queryService);
        GraphCanvas = new GraphCanvasViewModel(databaseService, layoutService, logger);

        QueryEditor.ResultReady += OnResultReady;

        _subscriptions = Disposable.Combine(
            _db.IsOpen.Subscribe(v => Dispatcher.UIThread.Post(() => IsConnected = v)),
            _db.FilePath.Subscribe(v => Dispatcher.UIThread.Post(() => FilePath = v)),
            _db.Statistics.Subscribe(v => Dispatcher.UIThread.Post(() => Statistics = v)));
    }

    private void OnResultReady(QueryResult result)
    {
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

    public void OpenDatabase(string filePath) => _db.Open(filePath);

    [RelayCommand]
    private void CloseDatabase() => _db.Close();

    public void Dispose()
    {
        _subscriptions.Dispose();
        SchemaBrowser.Dispose();
    }
}
