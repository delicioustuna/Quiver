using System.Collections;
using System.Text;
using System.Text.Json;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quiver.Core;
using Quiver.Studio.Models;

namespace Quiver.Studio.ViewModels;

public sealed partial class ResultsViewModel : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<string> _columns = [];

    [ObservableProperty]
    private IList _rows = Array.Empty<object>();

    [ObservableProperty]
    private object? _selectedItem;

    [ObservableProperty]
    private bool _hasRows;

    private QueryResult? _currentResult;

    public void LoadResult(QueryResult result)
    {
        _currentResult = result;

        if (!result.IsTabular || result.Columns.Count == 0)
        {
            Columns = [];
            Rows = Array.Empty<object>();
            HasRows = false;
            SelectedItem = null;
            return;
        }

        var rows = new List<string[]>(result.Rows.Count);
        foreach (var row in result.Rows)
        {
            var cells = new string[result.Columns.Count];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = i < row.Count ? row[i]?.ToString() ?? "" : "";
            rows.Add(cells);
        }
        Rows = rows;
        Columns = result.Columns;
        HasRows = rows.Count > 0;
        SelectedItem = null;
    }

    public void Clear()
    {
        _currentResult = null;
        Columns = [];
        Rows = Array.Empty<object>();
        HasRows = false;
        SelectedItem = null;
    }

    [RelayCommand]
    private void CopyCsv()
    {
        if (_currentResult is not { IsTabular: true }) return;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', _currentResult.Columns));
        foreach (var row in _currentResult.Rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(row[i]?.ToString() ?? "");
            }
            sb.AppendLine();
        }
        TopLevel?.Clipboard?.SetTextAsync(sb.ToString());
    }

    [RelayCommand]
    private void CopyJson()
    {
        if (_currentResult is not { IsTabular: true }) return;
        var list = new List<Dictionary<string, object?>>();
        foreach (var row in _currentResult.Rows)
        {
            var dict = new Dictionary<string, object?>();
            for (var i = 0; i < _currentResult.Columns.Count; i++)
                dict[_currentResult.Columns[i]] = i < row.Count ? row[i] : null;
            list.Add(dict);
        }
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
        TopLevel?.Clipboard?.SetTextAsync(json);
    }

    public NodeId? GetSelectedNodeId()
    {
        if (_currentResult is null || SelectedItem is not string[] cells) return null;
        var rows = _currentResult.Rows;
        var stringRows = (List<string[]>)Rows;
        var idx = stringRows.IndexOf(cells);
        if (idx < 0 || idx >= rows.Count) return null;
        var row = rows[idx];
        foreach (var val in row)
        {
            if (val is NodeId nid) return nid;
        }
        return null;
    }

    internal Avalonia.Controls.TopLevel? TopLevel { get; set; }
}
