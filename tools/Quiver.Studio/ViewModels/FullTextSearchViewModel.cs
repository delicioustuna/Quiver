using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Studio.Models;
using Quiver.Studio.Services;

namespace Quiver.Studio.ViewModels;

public sealed partial class FullTextSearchViewModel : ObservableObject
{
    private readonly DatabaseService _db;

    [ObservableProperty]
    private IReadOnlyList<FullTextIndexInfo> _indexes = [];

    [ObservableProperty]
    private FullTextIndexInfo? _selectedIndex;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private int _maxResults = 20;

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private string _statusText = "Ready";

    public QueryResult? LastResult { get; private set; }

    public event Action<QueryResult>? ResultReady;

    public FullTextSearchViewModel(DatabaseService databaseService)
    {
        _db = databaseService;
    }

    public void RefreshIndexes()
    {
        var database = _db.CurrentDatabase;
        if (database is null)
        {
            Indexes = [];
            SelectedIndex = null;
            return;
        }

        Indexes = database.Schema.ListFullTextIndexes();
        if (SelectedIndex is not null &&
            !Indexes.Any(i => i.Name == SelectedIndex.Name))
            SelectedIndex = null;

        SelectedIndex ??= Indexes.Count > 0 ? Indexes[0] : null;
    }

    public void Reset()
    {
        StatusText = "Ready";
        IsExecuting = false;
        LastResult = null;
    }

    public async Task ExecuteAsync()
    {
        if (SelectedIndex is null || string.IsNullOrWhiteSpace(QueryText) || _db.CurrentDatabase is null)
            return;

        IsExecuting = true;
        StatusText = "Searching...";

        try
        {
            var indexName = SelectedIndex.Name;
            var propKeyName = SelectedIndex.PropertyKey;
            var query = QueryText;
            var k = MaxResults;
            var database = _db.CurrentDatabase;

            var result = await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                using var tx = database.BeginReadOnlyTransaction();
                var g = tx.G(database.Schema);

                List<VertexId> ids;
                try
                {
                    ids = g.Search(indexName, query, k).ToList();
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return QueryResult.FromError(ex.Message, sw.Elapsed);
                }

                PropertyKeyId? propKeyId = null;
                if (database.Schema.TryGetPropertyKeyId(propKeyName, out var kid))
                    propKeyId = kid;

                var columns = new List<string> { "VertexId", "Label", propKeyName };
                var rows = new List<IReadOnlyList<object?>>();

                foreach (var nid in ids)
                {
                    if (!tx.VertexExists(nid)) continue;
                    var label = tx.GetVertexLabel(nid) ?? $"({nid.Sequence})";

                    var propValue = "";
                    if (propKeyId is not null)
                    {
                        var props = tx.EnumerateProperties(nid);
                        while (props.MoveNext())
                        {
                            if (props.Current.KeyId == propKeyId.Value)
                            {
                                propValue = MaterializeValue(props.Current.Value);
                                break;
                            }
                        }
                    }

                    rows.Add(new object?[] { nid.Sequence, label, propValue });
                }

                sw.Stop();
                return QueryResult.Tabular(columns, rows, sw.Elapsed, ids);
            });

            LastResult = result;
            ResultReady?.Invoke(result);

            if (result.IsError)
            {
                StatusText = $"Error ({result.Elapsed.TotalMilliseconds:F0}ms)";
            }
            else
            {
                var count = result.Rows.Count;
                StatusText = $"{count} result(s) ({result.Elapsed.TotalMilliseconds:F0}ms)";
            }
        }
        catch (Exception ex)
        {
            var errorResult = QueryResult.FromError(ex.ToString(), TimeSpan.Zero);
            LastResult = errorResult;
            ResultReady?.Invoke(errorResult);
            StatusText = "Error";
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void Clear()
    {
        Indexes = [];
        SelectedIndex = null;
        QueryText = "";
        Reset();
    }

    private static string MaterializeValue(in PropertyValue value) => value.Type switch
    {
        PropertyValueType.String => Encoding.UTF8.GetString(value.Utf8StringValue),
        PropertyValueType.Bool => value.BoolValue.ToString(),
        PropertyValueType.Int32 => value.Int32Value.ToString(),
        PropertyValueType.Int64 => value.Int64Value.ToString(),
        PropertyValueType.Double => value.DoubleValue.ToString("G"),
        _ => "(binary)",
    };
}
