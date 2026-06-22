using Microsoft.Extensions.Logging;
using R3;

namespace Quiver.Studio.Services;

public sealed class DatabaseService : IDisposable
{
    private readonly ILogger<DatabaseService> _logger;
    private GraphDatabase? _database;
    private readonly ReactiveProperty<bool> _isOpen = new(false);

    public DatabaseService(ILogger<DatabaseService> logger)
    {
        _logger = logger;
    }

    public GraphDatabase? CurrentDatabase => _database;
    public ReadOnlyReactiveProperty<bool> IsOpen => _isOpen;

    public void Open(string filePath)
    {
        Close();
        _logger.LogInformation("データベースを開いています: {Path}", filePath);
        _database = GraphDatabase.Open(filePath);
        _isOpen.Value = true;
        _logger.LogInformation("データベースを開きました: {Path}", filePath);
    }

    public void Close()
    {
        if (_database is null) return;
        _logger.LogInformation("データベースを閉じています: {Path}", _database.Path);
        _database.Dispose();
        _database = null;
        _isOpen.Value = false;
    }

    public void Dispose()
    {
        Close();
        _isOpen.Dispose();
    }
}
