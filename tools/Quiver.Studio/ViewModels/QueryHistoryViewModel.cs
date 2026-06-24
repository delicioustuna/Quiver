using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quiver.Studio.Models;
using Quiver.Studio.Services;

namespace Quiver.Studio.ViewModels;

public sealed partial class QueryHistoryViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private ObservableCollection<QueryHistoryEntry> _filteredEntries = [];

    public event Action<string>? LoadRequested;

    public QueryHistoryViewModel(SettingsService settingsService)
    {
        _settings = settingsService;
        Refresh();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public void Refresh()
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var source = _settings.Settings.QueryHistory;
        var filtered = string.IsNullOrWhiteSpace(FilterText)
            ? source
            : source.Where(e => e.Code.Contains(FilterText, StringComparison.OrdinalIgnoreCase)).ToList();

        FilteredEntries = new ObservableCollection<QueryHistoryEntry>(filtered);
    }

    [RelayCommand]
    private void LoadEntry(QueryHistoryEntry? entry)
    {
        if (entry is not null)
            LoadRequested?.Invoke(entry.Code);
    }

    [RelayCommand]
    private void DeleteEntry(QueryHistoryEntry? entry)
    {
        if (entry is null) return;
        _settings.Settings.QueryHistory.Remove(entry);
        _settings.MarkDirty();
        Refresh();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _settings.Settings.QueryHistory.Clear();
        _settings.MarkDirty();
        Refresh();
    }
}
