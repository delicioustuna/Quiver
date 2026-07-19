using Quiver.Core;
using Quiver.Index;
using Quiver.Index.FullText;
using Quiver.Text;

namespace Quiver.Transactions;

internal sealed class TxIndexManager : IIndexManager
{
    private readonly IIndexManager _inner;
    private readonly bool _isReadOnly;
    private readonly ScalarIndexMetadata[]? _definitionSnapshot;

    internal TxIndexManager(IIndexManager inner, bool isReadOnly)
    {
        _inner = inner;
        _isReadOnly = isReadOnly;
        _definitionSnapshot = isReadOnly
            ? inner.ListIndexDefinitions().ToArray()
            : null;
    }

    internal IIndexManager Inner => _inner;

    public IBTreeIndex<int> CreateInt32Index(string name)
    { EnsureCanOpen(name); return _inner.CreateInt32Index(name); }
    public IBTreeIndex<long> CreateInt64Index(string name)
    { EnsureCanOpen(name); return _inner.CreateInt64Index(name); }
    public IBTreeIndex<double> CreateDoubleIndex(string name)
    { EnsureCanOpen(name); return _inner.CreateDoubleIndex(name); }
    public IBTreeIndex<string> CreateStringIndex(string name)
    { EnsureCanOpen(name); return _inner.CreateStringIndex(name); }
    public IBTreeIndex<byte[]> CreateBytesIndex(string name)
    { EnsureCanOpen(name); return _inner.CreateBytesIndex(name); }
    public bool DropIndex(string name)
    { EnsureWritable(); return _inner.DropIndex(name); }
    public bool RenameIndex(string oldName, string newName)
    { EnsureWritable(); return _inner.RenameIndex(oldName, newName); }
    public void RenamePropertyTarget(string oldName, string newName)
    { EnsureWritable(); _inner.RenamePropertyTarget(oldName, newName); }
    public void RenameTargetScope(
        PropertyOwnerKind ownerKind,
        string oldName,
        string newName)
    { EnsureWritable(); _inner.RenameTargetScope(ownerKind, oldName, newName); }
    public IEnumerable<string> ListIndexes() => _inner.ListIndexes();

    public void RegisterIndexDefinition(
        ScalarIndexDefinition definition,
        IndexLifecycleState state = IndexLifecycleState.Ready)
    { EnsureWritable(); _inner.RegisterIndexDefinition(definition, state); }

    public bool TryGetIndexName(string label, string propertyKey, out string indexName)
    {
        if (_definitionSnapshot is null)
            return _inner.TryGetIndexName(label, propertyKey, out indexName);

        ScalarIndexMetadata match = _definitionSnapshot.FirstOrDefault(x =>
            x.Definition.Target.OwnerKind == PropertyOwnerKind.Vertex
            && x.Definition.Target.Scope == label
            && x.Definition.Target.PropertyKey == propertyKey);
        indexName = match.Definition?.Name ?? string.Empty;
        return match.Definition is not null;
    }

    public IEnumerable<ScalarIndexMetadata> ListIndexDefinitions()
    {
        if (_definitionSnapshot is null)
            return _inner.ListIndexDefinitions();

        HashSet<string> available =
            [.. _inner.ListIndexes()];
        return _definitionSnapshot.Select(x =>
            available.Contains(x.Definition.Name)
                ? x
                : x with { State = IndexLifecycleState.RebuildRequired });
    }

    public void SetIndexState(string name, IndexLifecycleState state)
    { EnsureWritable(); _inner.SetIndexState(name, state); }

    public void ResetIndexArtifact(string name)
    { EnsureWritable(); _inner.ResetIndexArtifact(name); }

    public FullTextCatalogEntry CreateFullTextDefinition(
        string name,
        string target,
        string propertyKey,
        string tokenizerId)
    {
        EnsureWritable();
        return _inner.CreateFullTextDefinition(name, target, propertyKey, tokenizerId);
    }

    public bool TryGetFullTextDefinition(
        string name,
        out FullTextCatalogEntry definition)
        => _inner.TryGetFullTextDefinition(name, out definition);

    public IEnumerable<(string Name, string Target, string PropertyKey, string TokenizerId)> ListFullTextDefinitions()
        => _inner.ListFullTextDefinitions();

    public IEnumerable<FullTextCatalogEntry> ListFullTextCatalogEntries()
        => _inner.ListFullTextCatalogEntries();

    public bool DropFullTextDefinition(string name)
    { EnsureWritable(); return _inner.DropFullTextDefinition(name); }

    public void UpdateFullTextManifest(
        string name,
        string manifest,
        IndexLifecycleState state)
    { EnsureWritable(); _inner.UpdateFullTextManifest(name, manifest, state); }

    public ITokenizer ResolveTokenizer(string tokenizerId)
        => _inner.ResolveTokenizer(tokenizerId);

    public void RegisterTokenizer(ITokenizer tokenizer)
    { EnsureWritable(); _inner.RegisterTokenizer(tokenizer); }

    private void EnsureWritable()
    {
        if (_isReadOnly)
            throw new TransactionException("Cannot mutate indexes in a read-only transaction.");
    }

    private void EnsureCanOpen(string name)
    {
        if (!_isReadOnly) return;
        if (_inner.ListIndexes().Contains(name, StringComparer.Ordinal)) return;
        throw new TransactionException(
            $"Cannot create index '{name}' in a read-only transaction.");
    }
}
