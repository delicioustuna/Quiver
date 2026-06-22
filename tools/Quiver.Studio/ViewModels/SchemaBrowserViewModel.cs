using System.Collections.ObjectModel;
using Avalonia.Threading;
using Quiver.Studio.Services;
using R3;

namespace Quiver.Studio.ViewModels;

public sealed class SchemaBrowserViewModel : IDisposable
{
    private readonly DatabaseService _db;
    private readonly IDisposable _subscription;

    public ObservableCollection<SchemaTreeNode> RootNodes { get; } = [];

    public SchemaBrowserViewModel(DatabaseService db)
    {
        _db = db;
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

        var labels = schema.ListLabels();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Labels ({labels.Count})", "•",
            labels.Select(l => SchemaTreeNode.Leaf(l, "○"))));

        var relTypes = schema.ListRelationshipTypes();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Relationship Types ({relTypes.Count})", "→",
            relTypes.Select(r => SchemaTreeNode.Leaf(r, "→"))));

        var propKeys = schema.ListPropertyKeys();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Property Keys ({propKeys.Count})", "≡",
            propKeys.Select(p => SchemaTreeNode.Leaf(p, "∙"))));

        var indexes = schema.ListIndexes();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Indexes ({indexes.Count})", "⚡",
            indexes.Select(i => SchemaTreeNode.Leaf(
                i.Name, "⚡",
                $"{i.Label}.{i.PropertyKey} ({i.Kind})"))));

        var ftIndexes = schema.ListFullTextIndexes();
        RootNodes.Add(SchemaTreeNode.Folder(
            $"Full-Text Indexes ({ftIndexes.Count})", "🔍",
            ftIndexes.Select(f => SchemaTreeNode.Leaf(
                f.Name, "🔍",
                $"{f.Label}.{f.PropertyKey} ({f.TokenizerId})"))));
    }

    public void Dispose() => _subscription.Dispose();
}
