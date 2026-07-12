using System.Text;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// <see cref="GraphDatabase"/> と呼び出し側から渡される <see cref="IVectorStore"/> /
/// <see cref="IVectorCatalog"/> を、<c>Quiver.Embedding</c> が依存する <see cref="IGraphEngine"/>
/// 抽象に橋渡しするアダプタ。
/// ヘルパからエンジン内部実装を隠蔽しつつ、エンティティ走査と source-text プロパティ読み出しを公開する。
/// </summary>
/// <remarks>
/// ベクトルストアとカタログはアダプタ内で構築せず、外部から注入する。これにより同じ DB を
/// インメモリリファレンスストアと将来の ANN 裏付けストアのどちらにでも組み合わせられ、
/// 本クラスを変更する必要がない。
/// </remarks>
internal sealed class GraphEngineAdapter : IGraphEngine
{
    private readonly GraphDatabase _db;
    private readonly IVectorStore _vectors;
    private readonly IVectorCatalog _catalog;

    /// <summary>指定 DB と外部から注入されたベクトルストア / カタログでアダプタを生成する。</summary>
    public GraphEngineAdapter(GraphDatabase db, IVectorStore vectors, IVectorCatalog catalog)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _vectors = vectors ?? throw new ArgumentNullException(nameof(vectors));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc/>
    public IVectorStore Vectors => _vectors;

    /// <inheritdoc/>
    public IVectorCatalog Catalog => _catalog;

    /// <inheritdoc/>
    public IGraphEngineReadSession BeginRead()
        => new ReadSession(_db.BackendInternal.Transactions.Begin(IsolationLevel.SnapshotIsolation), _db.Schema);

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
                        yield return EntityRef.From(nid);
                    break;
                case EntityKind.Relationship:
                    foreach (var rid in _tx.Relationships.Scan())
                        yield return EntityRef.From(rid);
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

        // node / rel とも inline + overflow を結合列挙する。
            PropertyEnumerator enumerator;
            if (entity.Kind == EntityKind.Node)
            {
                enumerator = _tx.Nodes.EnumerateProperties(new NodeId(entity.Value), _tx.Properties);
            }
            else if (entity.Kind == EntityKind.Relationship)
            {
                enumerator = _tx.Relationships.EnumerateProperties(new RelationshipId(entity.Value), _tx.Properties);
            }
            else
            {
                text = string.Empty;
                return false;
            }
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
