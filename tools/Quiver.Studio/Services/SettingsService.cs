using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quiver.Studio.Models;

namespace Quiver.Studio.Services;

public sealed class SettingsService : IDisposable
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuiverStudio");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly System.Threading.Lock _lock = new();
    private readonly Timer _debounceTimer;
    private StudioSettings _settings;
    private bool _dirty;

    public StudioSettings Settings
    {
        get { lock (_lock) return _settings; }
    }

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        _settings = Load();
        _debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void MarkDirty()
    {
        lock (_lock) _dirty = true;
        _debounceTimer.Change(500, Timeout.Infinite);
    }

    public void SaveImmediate()
    {
        _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Flush();
    }

    public void AddRecentFile(string path)
    {
        lock (_lock)
        {
            _settings.RecentFiles.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            _settings.RecentFiles.Insert(0, new RecentFileEntry { Path = path, LastOpened = DateTime.UtcNow });
            if (_settings.RecentFiles.Count > 20)
                _settings.RecentFiles.RemoveRange(20, _settings.RecentFiles.Count - 20);
            _settings.LastOpenedPath = path;
        }
        MarkDirty();
    }

    public void AddQueryHistory(string code, double durationMs, bool wasError, string? dbPath)
    {
        lock (_lock)
        {
            _settings.QueryHistory.Insert(0, new QueryHistoryEntry
            {
                Code = code,
                Timestamp = DateTime.UtcNow,
                DurationMs = durationMs,
                WasError = wasError,
                DbPath = dbPath,
            });
            var max = _settings.MaxHistoryEntries;
            if (_settings.QueryHistory.Count > max)
                _settings.QueryHistory.RemoveRange(max, _settings.QueryHistory.Count - max);
        }
        MarkDirty();
    }

    private StudioSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new StudioSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<StudioSettings>(json, JsonOptions) ?? new StudioSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "設定ファイルの読み込みに失敗。デフォルトを使用");
            return new StudioSettings();
        }
    }

    private void Flush()
    {
        StudioSettings snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
            snapshot = _settings;
        }

        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定ファイルの保存に失敗");
        }
    }

    public void Dispose()
    {
        _debounceTimer.Dispose();
        Flush();
    }
}
