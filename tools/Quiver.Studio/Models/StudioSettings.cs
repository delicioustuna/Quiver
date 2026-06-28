namespace Quiver.Studio.Models;

public sealed class StudioSettings
{
    public int Version { get; set; } = 1;
    public string Theme { get; set; } = "Light";
    public string? LastOpenedPath { get; set; }
    public WindowStateData? WindowState { get; set; }
    public List<RecentFileEntry> RecentFiles { get; set; } = [];
    public List<QueryHistoryEntry> QueryHistory { get; set; } = [];
    public int MaxHistoryEntries { get; set; } = 200;
    public GraphVisualSettings GraphVisual { get; set; } = new();
    public string Language { get; set; } = "auto";
}

public sealed class RecentFileEntry
{
    public required string Path { get; set; }
    public DateTime LastOpened { get; set; }
}

public sealed class QueryHistoryEntry
{
    public required string Code { get; set; }
    public DateTime Timestamp { get; set; }
    public double DurationMs { get; set; }
    public bool WasError { get; set; }
    public string? DbPath { get; set; }
}

public sealed class WindowStateData
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public bool IsMaximized { get; set; }
}
