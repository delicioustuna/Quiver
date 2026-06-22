using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Extensions.Logging;
using Quiver.Api;
using Quiver.Core;
using Quiver.Studio.Models;

namespace Quiver.Studio.Services;

public sealed class QueryExecutionService
{
    private readonly DatabaseService _databaseService;
    private readonly ILogger<QueryExecutionService> _logger;
    private ScriptOptions? _scriptOptions;

    public QueryExecutionService(DatabaseService databaseService, ILogger<QueryExecutionService> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }

    public bool CanExecute => _databaseService.CurrentDatabase is not null;

    public async Task<QueryResult> ExecuteAsync(string code, CancellationToken ct = default)
    {
        var db = _databaseService.CurrentDatabase;
        if (db is null)
            return QueryResult.FromError("No database is open.", TimeSpan.Zero);

        _scriptOptions ??= CreateScriptOptions();

        var sw = Stopwatch.StartNew();
        using var tx = db.BeginReadOnlyTransaction();

        var globals = new ScriptGlobals
        {
            db = db,
            tx = tx,
            g = tx.G(db.Schema),
            schema = db.Schema,
        };

        try
        {
            var state = await CSharpScript.RunAsync(code, _scriptOptions, globals, typeof(ScriptGlobals), ct);
            sw.Stop();
            _databaseService.RefreshStatistics();
            return MaterializeResult(state.ReturnValue, sw.Elapsed);
        }
        catch (CompilationErrorException ex)
        {
            sw.Stop();
            var errors = string.Join(Environment.NewLine, ex.Diagnostics.Select(d => d.ToString()));
            return QueryResult.FromError(errors, sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return QueryResult.FromError(ex.ToString(), sw.Elapsed);
        }
    }

    private ScriptOptions CreateScriptOptions()
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => a.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "Quiver",
                "Quiver.Core",
                "Quiver.Api",
                "Quiver.Transactions",
                "Quiver.Storage.Records");
    }

    private static QueryResult MaterializeResult(object? result, TimeSpan elapsed)
    {
        if (result is null)
            return QueryResult.Empty(elapsed);

        if (result is string s)
            return QueryResult.Scalar(s, elapsed);

        if (result is NodeId or RelationshipId)
            return QueryResult.Scalar(result.ToString()!, elapsed);

        if (IsPrimitive(result))
            return QueryResult.Scalar(result.ToString() ?? "", elapsed);

        if (result is IEnumerable enumerable)
            return MaterializeEnumerable(enumerable, elapsed);

        return MaterializeObject(result, elapsed);
    }

    private static QueryResult MaterializeEnumerable(IEnumerable enumerable, TimeSpan elapsed)
    {
        var items = new List<object?>();
        foreach (var item in enumerable)
            items.Add(item);

        if (items.Count == 0)
            return QueryResult.Empty(elapsed);

        var firstNonNull = items.FirstOrDefault(x => x is not null);
        if (firstNonNull is null)
            return QueryResult.Tabular(["Value"],
                items.Select(x => (IReadOnlyList<object?>)[x]).ToList(), elapsed);

        var type = firstNonNull.GetType();

        if (IsPrimitive(firstNonNull) || firstNonNull is string || firstNonNull is NodeId || firstNonNull is RelationshipId)
        {
            return QueryResult.Tabular(["Value"],
                items.Select(x => (IReadOnlyList<object?>)[x?.ToString()]).ToList(), elapsed);
        }

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        if (props.Length == 0)
        {
            return QueryResult.Tabular(["Value"],
                items.Select(x => (IReadOnlyList<object?>)[x?.ToString()]).ToList(), elapsed);
        }

        var columns = props.Select(p => p.Name).ToList();
        var rows = items.Select(item =>
        {
            if (item is null)
                return (IReadOnlyList<object?>)new object?[columns.Count];
            return (IReadOnlyList<object?>)props.Select(p =>
            {
                try { return p.GetValue(item); }
                catch { return null; }
            }).ToArray();
        }).ToList();

        return QueryResult.Tabular(columns, rows, elapsed);
    }

    private static QueryResult MaterializeObject(object result, TimeSpan elapsed)
    {
        var type = result.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        if (props.Length == 0)
            return QueryResult.Scalar(result.ToString() ?? "", elapsed);

        var columns = props.Select(p => p.Name).ToList();
        var values = props.Select(p =>
        {
            try { return p.GetValue(result); }
            catch { return null; }
        }).ToArray();

        return QueryResult.Tabular(columns, [(IReadOnlyList<object?>)values], elapsed);
    }

    private static bool IsPrimitive(object value)
        => value is int or long or double or float or decimal or bool or char or byte
            or short or ushort or uint or ulong or sbyte;
}
