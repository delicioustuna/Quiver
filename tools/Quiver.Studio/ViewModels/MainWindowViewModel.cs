using System.ComponentModel;
using System.Runtime.CompilerServices;
using Quiver.Studio.Services;
using R3;

namespace Quiver.Studio.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DatabaseService _db;
    private readonly IDisposable _subscriptions;

    private bool _isConnected;
    private string _filePath = string.Empty;
    private DatabaseStatistics? _statistics;

    public string Title => "Quiver Studio";

    public bool IsConnected
    {
        get => _isConnected;
        private set { if (_isConnected != value) { _isConnected = value; OnPropertyChanged(); } }
    }

    public string FilePath
    {
        get => _filePath;
        private set { if (_filePath != value) { _filePath = value; OnPropertyChanged(); } }
    }

    public DatabaseStatistics? Statistics
    {
        get => _statistics;
        private set { if (_statistics != value) { _statistics = value; OnPropertyChanged(); } }
    }

    public SchemaBrowserViewModel SchemaBrowser { get; }

    public MainWindowViewModel(DatabaseService databaseService)
    {
        _db = databaseService;
        SchemaBrowser = new SchemaBrowserViewModel(_db);

        _subscriptions = Disposable.Combine(
            _db.IsOpen.Subscribe(v => IsConnected = v),
            _db.FilePath.Subscribe(v => FilePath = v),
            _db.Statistics.Subscribe(v => Statistics = v));
    }

    public void OpenDatabase(string filePath) => _db.Open(filePath);
    public void CloseDatabase() => _db.Close();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _subscriptions.Dispose();
        SchemaBrowser.Dispose();
    }
}
