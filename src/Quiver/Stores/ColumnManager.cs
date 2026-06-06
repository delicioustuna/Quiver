using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// ARCH-5c Phase 5 (5b): opt-in 列の管理。<see cref="ColumnCatalog"/> の登録を読み、各列の
/// <see cref="ScalarColumnStore"/> (container テナント) を開く。<c>CreateColumn</c> はテナントを
/// 割り当て、現データから列を構築 (1 パス scan) し登録を永続化する。
///
/// <para>5b 時点では列は **登録 + 初期構築 + 永続** まで。以後の write での維持は 5c (write 経路統合)
/// で配線する。よって 5b 直後の登録済み列は構築時点の静的スナップショット (write は未反映)。</para>
/// </summary>
internal sealed class ColumnManager
{
    private readonly ColumnCatalog _catalog;
    private readonly SingleFileContainer _container;
    private readonly VersionedRelationshipStore _relStore;
    private readonly VersionedNodeStore _nodeStore;
    private readonly PropertyStore _propStore;
    private readonly Dictionary<(EntityKind, int), ScalarColumnStore> _columns = new();

    public ColumnManager(
        IPagedFile catalogFile,
        SingleFileContainer container,
        VersionedRelationshipStore relStore,
        VersionedNodeStore nodeStore,
        PropertyStore propStore)
    {
        _catalog = new ColumnCatalog(catalogFile);
        _container = container;
        _relStore = relStore;
        _nodeStore = nodeStore;
        _propStore = propStore;

        // 登録済み列を開く (head ページから cache rebuild)。
        foreach (var e in _catalog.Entries)
            _columns[(e.Kind, e.KeyId)] = new ScalarColumnStore(
                _container.OpenTenant(e.TenantId, PageKind.ItemPointerMap));
    }

    public bool TryGetColumn(EntityKind kind, int keyId, out ScalarColumnStore column)
        => _columns.TryGetValue((kind, keyId), out column!);

    public bool IsColumn(EntityKind kind, int keyId) => _columns.ContainsKey((kind, keyId));

    /// <summary>列を登録し、現データから構築する。既存なら no-op で false。</summary>
    public bool CreateColumn(EntityKind kind, int keyId)
    {
        if (kind is not (EntityKind.Relationship or EntityKind.Node))
            throw new NotSupportedException($"columnar is only supported for Node / Relationship, got {kind}.");
        if (_columns.ContainsKey((kind, keyId))) return false;

        byte tenantId = _catalog.Register(kind, keyId);
        var store = new ScalarColumnStore(_container.OpenTenant(tenantId, PageKind.ItemPointerMap));
        BuildFromData(store, kind, keyId);
        _columns[(kind, keyId)] = store;
        return true;
    }

    /// <summary>列登録を解除する (テナントの物理回収は後続)。</summary>
    public bool DropColumn(EntityKind kind, int keyId)
    {
        if (!_columns.Remove((kind, keyId))) return false;
        _catalog.Unregister(kind, keyId);
        return true;
    }

    private void BuildFromData(ScalarColumnStore store, EntityKind kind, int keyId)
    {
        var key = new PropertyKeyId(keyId);
        if (kind == EntityKind.Relationship)
        {
            foreach (var relId in _relStore.Scan())
                if (TryExtractScalar(_relStore.EnumerateProperties(relId, _propStore), key, out long bits))
                    store.Set(relId.Sequence, bits, TransactionId.Bootstrap.Value);
        }
        else // Node
        {
            foreach (var nodeId in _nodeStore.Scan())
                if (TryExtractScalar(_nodeStore.EnumerateProperties(nodeId, _propStore), key, out long bits))
                    store.Set(nodeId.Sequence, bits, TransactionId.Bootstrap.Value);
        }
    }

    private static bool TryExtractScalar(PropertyEnumerator pe, PropertyKeyId key, out long bits)
    {
        bits = 0;
        while (pe.MoveNext())
        {
            var cur = pe.Current;
            if (cur.KeyId != key) continue;
            if (!InlinePropertyCodec.IsScalar(cur.Value.Type)) return false;
            bits = cur.Value.Type switch
            {
                PropertyValueType.Bool => cur.Value.BoolValue ? 1L : 0L,
                PropertyValueType.Int32 => cur.Value.Int32Value,
                PropertyValueType.Int64 => cur.Value.Int64Value,
                PropertyValueType.Double => BitConverter.DoubleToInt64Bits(cur.Value.DoubleValue),
                _ => 0L,
            };
            return true;
        }
        return false;
    }
}
