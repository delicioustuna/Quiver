using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Quiver.Studio.Models;
using Quiver.Studio.Services;

namespace Quiver.Studio.ViewModels;

public sealed partial class QueryEditorViewModel : ObservableObject
{
    private readonly QueryExecutionService _queryService;

    [ObservableProperty]
    private string _output = "";

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusText = "Ready";

    public QueryResult? LastResult { get; private set; }

    public event Action<QueryResult>? ResultReady;

    public QueryEditorViewModel(QueryExecutionService queryService)
    {
        _queryService = queryService;
    }

    public void Reset()
    {
        Output = "";
        HasError = false;
        StatusText = "Ready";
        LastResult = null;
    }

    public async Task ExecuteAsync(string code)
    {
        if (IsExecuting || !_queryService.CanExecute) return;
        IsExecuting = true;
        HasError = false;
        StatusText = "Executing...";
        Output = "";

        try
        {
            var result = await Task.Run(() => _queryService.ExecuteAsync(code));
            Dispatcher.UIThread.VerifyAccess();

            LastResult = result;
            ResultReady?.Invoke(result);

            if (result.IsError)
            {
                HasError = true;
                Output = result.Error!;
                StatusText = $"Error ({result.Elapsed.TotalMilliseconds:F0}ms)";
            }
            else if (result.IsScalar)
            {
                Output = result.ScalarText!;
                StatusText = $"OK ({result.Elapsed.TotalMilliseconds:F0}ms)";
            }
            else if (result.IsTabular)
            {
                Output = FormatTable(result);
                StatusText = $"{result.Rows.Count} row(s) ({result.Elapsed.TotalMilliseconds:F0}ms)";
            }
            else
            {
                Output = "(no result)";
                StatusText = $"OK ({result.Elapsed.TotalMilliseconds:F0}ms)";
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            Output = ex.ToString();
            StatusText = "Error";
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private static string FormatTable(QueryResult result)
    {
        var sb = new StringBuilder();
        var widths = new int[result.Columns.Count];

        for (var i = 0; i < result.Columns.Count; i++)
            widths[i] = result.Columns[i].Length;

        foreach (var row in result.Rows)
            for (var i = 0; i < row.Count && i < widths.Length; i++)
                widths[i] = Math.Max(widths[i], (row[i]?.ToString() ?? "null").Length);

        for (var i = 0; i < widths.Length; i++)
            widths[i] = Math.Min(widths[i], 40);

        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (i > 0) sb.Append(" | ");
            sb.Append(result.Columns[i].PadRight(widths[i]));
        }
        sb.AppendLine();

        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (i > 0) sb.Append("-+-");
            sb.Append(new string('-', widths[i]));
        }
        sb.AppendLine();

        foreach (var row in result.Rows)
        {
            for (var i = 0; i < row.Count && i < widths.Length; i++)
            {
                if (i > 0) sb.Append(" | ");
                var text = (row[i]?.ToString() ?? "null");
                if (text.Length > 40) text = text[..37] + "...";
                sb.Append(text.PadRight(widths[i]));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
