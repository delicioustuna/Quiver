using Quiver.Studio.Services;
using R3;

namespace Quiver.Studio.ViewModels;

public sealed class MainWindowViewModel : IDisposable
{
    private readonly DatabaseService _db;

    public string Title => "Quiver Studio";

    public ReadOnlyReactiveProperty<bool> IsConnected { get; }
    public ReadOnlyReactiveProperty<string> FilePath { get; }
    public ReadOnlyReactiveProperty<DatabaseStatistics?> Statistics { get; }
    public SchemaBrowserViewModel SchemaBrowser { get; }

    public MainWindowViewModel(DatabaseService databaseService)
    {
        _db = databaseService;
        IsConnected = _db.IsOpen;
        FilePath = _db.FilePath;
        Statistics = _db.Statistics;
        SchemaBrowser = new SchemaBrowserViewModel(_db);
    }

    public void OpenDatabase(string filePath) => _db.Open(filePath);
    public void CloseDatabase() => _db.Close();

    public void Dispose()
    {
        SchemaBrowser.Dispose();
    }
}
