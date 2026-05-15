using System.Text;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// Adapts a <see cref="GraphDatabase"/> + caller-provided
/// <see cref="IVectorStore"/> / <see cref="IVectorCatalog"/> to the
/// <see cref="IGraphEngine"/> abstraction <c>Quiver.Embedding</c> talks to
/// (VEC-4 / codex_advice_3.md §6.6). Keeps the helper free of engine
/// internals while letting it walk entities and read source-text properties.
/// </summary>
/// <remarks>
/// Vector store and catalog are injected rather than constructed by the
/// adapter — that lets the same database be paired with either the
/// in-memory reference store or a future ANN-backed store without changing
/// this class.
/// </remarks>
public sealed class GraphEngineAdapter : IGraphEngine
{
    private readonly GraphDatabase _db;
    private readonly IVectorStore _vectors;
    private readonly IVectorCatalog _catalog;

    public GraphEngineAdapter(GraphDatabase db, IVectorStore vectors, IVectorCatalog catalog)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _vectors = vectors ?? throw new ArgumentNullException(nameof(vectors));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public IVectorStore Vectors => _vectors;
    public IVectorCatalog Catalog => _catalog;

    public IGraphEngineReadSession BeginRead()
        => new ReadSession(_db.Backend.Transactions.Begin(IsolationLevel.SnapshotIsolation), _db.Schema);

    private sealed class ReadSession : IGraphEngineReadSession
    {
        private readonly ITransaction _tx;
        private readonly ISchemaApi _schema;
        private readonly Dictionary<string, PropertyKeyId> _keyCache = new(StringComparer.Ordinal);

        internal ReadSession(ITransaction tx, ISchemaApi schema)
        {
            _tx = tx;
            _schema = schema;
        }

        public IEnumerable<EntityRef> EnumerateEntities(EntityKind kind)
        {
            switch (kind)
            {
                case EntityKind.Node:
                    foreach (var nid in _tx.Nodes.Scan())
                        yield return new EntityRef(EntityKind.Node, nid.Value);
                    break;
                case EntityKind.Relationship:
                    foreach (var rid in _tx.Relationships.Scan())
                        yield return new EntityRef(EntityKind.Relationship, rid.Value);
                    break;
                default:
                    yield break;
            }
        }

        public bool TryReadStringProperty(EntityRef entity, string propertyKey, out string text)
        {
            ArgumentException.ThrowIfNullOrEmpty(propertyKey);

            var keyId = ResolveKey(propertyKey);
            if (!keyId.IsValid)
            {
                text = string.Empty;
                return false;
            }

            PropertyId firstProp = entity.Kind switch
            {
                EntityKind.Node => _tx.Nodes.Read(new NodeId(entity.Id)).FirstPropertyId,
                EntityKind.Relationship => _tx.Relationships.Read(new RelationshipId(entity.Id)).FirstPropertyId,
                _ => PropertyId.Invalid,
            };
            if (!firstProp.IsValid)
            {
                text = string.Empty;
                return false;
            }

            var enumerator = _tx.Properties.Enumerate(firstProp);
            while (enumerator.MoveNext())
            {
                var prop = enumerator.Current;
                if (prop.KeyId == keyId)
                {
                    if (prop.Value.Type != PropertyValueType.String)
                    {
                        text = string.Empty;
                        return false;
                    }
                    text = Encoding.UTF8.GetString(prop.Value.Utf8StringValue);
                    return true;
                }
            }

            text = string.Empty;
            return false;
        }

        private PropertyKeyId ResolveKey(string name)
        {
            if (_keyCache.TryGetValue(name, out var id)) return id;
            id = _schema.GetOrCreatePropertyKey(name);
            _keyCache[name] = id;
            return id;
        }

        public void Dispose() => _tx.Dispose();
    }
}
