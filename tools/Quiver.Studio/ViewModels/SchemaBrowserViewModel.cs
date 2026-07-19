using System.Collections.ObjectModel;
using Avalonia.Threading;
using Quiver.Studio.Services;
using R3;

namespace Quiver.Studio.ViewModels;

public sealed class SchemaBrowserViewModel : IDisposable
{
    private readonly DatabaseService _db;
    private readonly SchemaInspectionService _inspection;
    private readonly IDisposable _subscription;

    public ObservableCollection<SchemaTreeNode> RootNodes { get; } = [];

    public SchemaBrowserViewModel(DatabaseService db, SchemaInspectionService inspection)
    {
        _db = db;
        _inspection = inspection;
        _subscription = _db.IsOpen.Subscribe(isOpen =>
            Dispatcher.UIThread.Post(() =>
            {
                if (isOpen)
                    Refresh();
                else
                    RootNodes.Clear();
            }));
    }

    public void Refresh()
    {
        RootNodes.Clear();
        var database = _db.CurrentDatabase;
        if (database is null) return;

        var schema = database.Schema;
        var inspection = _inspection.Inspect();

        var labels = schema.ListLabels();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Labels ({labels.Count})", "•",
            labels.Select(l =>
            {
                var props = inspection?.LabelProperties.GetValueOrDefault(l);
                if (props is { Count: > 0 })
                {
                    return SchemaTreeNode.Folder(l, "○",
                        props.Select(p => SchemaTreeNode.Leaf(p.Name, "∙", p.InferredType)));
                }
                return SchemaTreeNode.Leaf(l, "○");
            })));

        var edgeTypes = schema.ListEdgeTypes();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Edge Types ({edgeTypes.Count})", "→",
            edgeTypes.Select(r =>
            {
                var props = inspection?.EdgeTypeProperties.GetValueOrDefault(r);
                if (props is { Count: > 0 })
                {
                    return SchemaTreeNode.Folder(r, "→",
                        props.Select(p => SchemaTreeNode.Leaf(p.Name, "∙", p.InferredType)));
                }
                return SchemaTreeNode.Leaf(r, "→");
            })));

        var propKeys = schema.ListPropertyKeys();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Property Keys ({propKeys.Count})", "≡",
            propKeys.Select(p => SchemaTreeNode.Leaf(p, "∙"))));

        var indexes = schema.ListIndexes();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Indexes ({indexes.Count})", "⚡",
            indexes.Select(i => SchemaTreeNode.Leaf(
                i.Name, "⚡",
                $"{i.Target.Scope ?? "*"}.{i.Target.PropertyKey} ({i.Kind})"))));

        var ftIndexes = indexes
            .Where(static index => index.Definition is FullTextIndexDefinition)
            .ToArray();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Full-Text Indexes ({ftIndexes.Length})", "🔍",
            ftIndexes.Select(f => SchemaTreeNode.Leaf(
                f.Name, "🔍",
                $"{f.Target.Scope}.{f.Target.PropertyKey} ({((FullTextIndexDefinition)f.Definition).TokenizerId})"))));
    }

    public void Dispose() => _subscription.Dispose();
}
