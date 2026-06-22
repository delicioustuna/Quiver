using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quiver.Studio.Services;
using R3;

namespace Quiver.Studio.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseService _db;
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

    public MainWindowViewModel(DatabaseService databaseService, QueryExecutionService queryService)
    {
        _db = databaseService;
        SchemaBrowser = new SchemaBrowserViewModel(_db);
        QueryEditor = new QueryEditorViewModel(queryService);

        _subscriptions = Disposable.Combine(
            _db.IsOpen.Subscribe(v => Dispatcher.UIThread.Post(() => IsConnected = v)),
            _db.FilePath.Subscribe(v => Dispatcher.UIThread.Post(() => FilePath = v)),
            _db.Statistics.Subscribe(v => Dispatcher.UIThread.Post(() => Statistics = v)));
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
