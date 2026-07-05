using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// opt-in 列の管理。<see cref="ColumnCatalog"/> の登録を読み、各列の
/// <see cref="ScalarColumnStore"/> (container テナント) を開く。<c>CreateColumn</c> はテナントを
/// 割り当て、現データから列を構築 (1 パス scan) し登録を永続化する。
///
/// <para>5b 時点では列は **登録 + 初期構築 + 永続** まで。以後の write での維持は 5c (write 経路統合)
/// で配線する。よって 5b 直後の登録済み列は構築時点の静的スナップショット (write は未反映)。</para>
/// </summary>
internal sealed class ColumnManager
{
    private readonly SingleFileContainer _container;
    private readonly byte _catalogTenantId;
    private readonly VersionedRelationshipStore _relStore;
    private readonly VersionedNodeStore _nodeStore;
    private readonly IHyperedgeStore _hyperedgeStore;
    private readonly PropertyStore _propStore;
    private readonly Dictionary<(EntityKind, int), ScalarColumnStore> _columns = new();
    // catalog は遅延生成 (ColumnCatalog ctor が空テナントにヘッダページを書くため)。
    // これにより列を使わない DB は catalog テナントを物理生成せず、既存 DB の on-disk
    // レイアウトを撹乱しない。CreateColumn (= 初回登録) で初めて生成される。
    private ColumnCatalog? _catalog;

    public ColumnManager(
        SingleFileContainer container,
        byte catalogTenantId,
        VersionedRelationshipStore relStore,
        VersionedNodeStore nodeStore,
        IHyperedgeStore hyperedgeStore,
        PropertyStore propStore)
    {
        _container = container;
        _catalogTenantId = catalogTenantId;
        _relStore = relStore;
        _nodeStore = nodeStore;
        _hyperedgeStore = hyperedgeStore;
        _propStore = propStore;

        // 既に catalog テナントが存在する DB のみ、登録済み列を eager に開く
        // (head ページから cache rebuild)。未生成なら何もしない (列無効状態)。
        if (_container.HasTenant(catalogTenantId))
        {
            _catalog = new ColumnCatalog(_container.OpenTenant(catalogTenantId, PageKind.Header));
            foreach (var e in _catalog.Entries)
                _columns[(e.Kind, e.KeyId)] = new ScalarColumnStore(
                    _container.OpenTenant(e.TenantId, PageKind.ItemPointerMap));
        }
    }

    // CreateColumn で初めて catalog テナントを生成する (遅延)。
    private ColumnCatalog Catalog => _catalog ??= new ColumnCatalog(
        _container.OpenTenant(_catalogTenantId, PageKind.Header));

    public bool TryGetColumn(EntityKind kind, int keyId, out ScalarColumnStore column)
        => _columns.TryGetValue((kind, keyId), out column!);

    public bool IsColumn(EntityKind kind, int keyId) => _columns.ContainsKey((kind, keyId));

    /// <summary>登録済み列が 1 つ以上あるか (write hook / abort hook の早期 bail に使う)。</summary>
    public bool HasAnyColumns => _columns.Count > 0;

    // ===== Phase 5c: write 経路統合 =====

    /// <summary>
    /// <see cref="GraphTransaction.SetProperty"/> から呼ばれ、(kind, keyId) が列なら同 tx で
    /// 列を維持する。scalar なら <see cref="ScalarColumnStore.Set"/>、scalar でなくなった
    /// (overflow へ逃げた) なら <see cref="ScalarColumnStore.Delete"/> で論理削除する
    /// (列は scalar 値のみ保持するため)。
    /// </summary>
    public void OnSetProperty(EntityKind kind, long seq, PropertyKeyId keyId, in PropertyValue value, long txId)
    {
        if (!_columns.TryGetValue((kind, keyId.Value), out var col)) return;
        if (TryScalarBits(in value, out long bits))
            col.Set(seq, bits, txId, value.Type);
        else
            col.Delete(seq, txId);
    }

    /// <summary>
    /// Phase 5d: (kind, keyId) が列なら可視値で count/sum/min/max/longSum を 1 パス集計する。
    /// 列が無い / mixed / 未設定なら false (呼び出し側が row path フォールバック)。
    /// <paramref name="valueType"/> は列の scalar 型 (集約側で SumLong 可否などの判定に使う)。
    /// </summary>
    public bool TryAggregate(EntityKind kind, int keyId,
        in SnapshotState snap, TransactionId self, CommittedTxRegistry committed,
        out long count, out double sum, out double min, out double max, out long longSum,
        out PropertyValueType valueType)
    {
        count = 0; sum = 0; min = 0; max = 0; longSum = 0; valueType = default;
        if (!_columns.TryGetValue((kind, keyId), out var col)) return false;
        // Phase 5d: optimizer コストモデルで列スキャン vs row path を判定する。delta 肥大時
        // (compaction 前) は row へフォールバックして列の point 劣化を避ける。head entries を
        // 行数推定の proxy に使う (full scan は全エンティティ ≒ 列エントリ数を訪れる)。
        if (!Quiver.QueryOptimizer.ShouldUseColumnAggregate(col.Hwm, col.DeltaVersionCount, col.Hwm))
            return false;
        if (!col.TryAggregate(in snap, self, committed, out count, out sum, out min, out max, out longSum))
            return false;
        valueType = col.ValueType;
        return true;
    }

    /// <summary>プロパティ除去: (kind, keyId) が列なら論理削除 (head に xmax)。</summary>
    public void OnRemoveProperty(EntityKind kind, long seq, PropertyKeyId keyId, long txId)
    {
        if (_columns.TryGetValue((kind, keyId.Value), out var col))
            col.Delete(seq, txId);
    }

    /// <summary>エンティティ削除: その kind の全列で seq を論理削除する。</summary>
    public void OnDeleteEntity(EntityKind kind, long seq, long txId)
    {
        foreach (var kv in _columns)
            if (kv.Key.Item1 == kind)
                kv.Value.Delete(seq, txId);
    }

    /// <summary>
    /// abort の before-image undo 後 (<c>ReloadStoreMeta</c> 経由) に呼ばれ、復元された head
    /// ページから各列の in-memory cache を再構築する。head 値の正当性を回復する。
    /// </summary>
    public void ReloadColumns()
    {
        foreach (var kv in _columns)
            kv.Value.ReloadFromPages();
    }

    /// <summary>abort 完了後 (OnRolledBack) に呼ばれ、中止 tx が積んだ delta 版を全列から捨てる。</summary>
    public void PruneAbortedTx(long txId)
    {
        foreach (var kv in _columns)
            kv.Value.PruneTx(txId);
    }

    /// <summary>
    /// Phase 5e: vacuum から呼ばれ、全列で visibility <paramref name="horizon"/> 未満かつ commit 済みの
    /// 超過 delta 版を merge する (どの snapshot からも不要になった旧版を回収)。回収版数の合計を返す。
    /// </summary>
    public int Compact(long horizon, CommittedTxRegistry committed)
    {
        int removed = 0;
        foreach (var kv in _columns)
            removed += kv.Value.Merge(horizon, committed);
        return removed;
    }

    /// <summary>列を登録し、現データから構築する。既存なら no-op で false。</summary>
    public bool CreateColumn(EntityKind kind, int keyId)
    {
        if (kind is not (EntityKind.Relationship or EntityKind.Node or EntityKind.Hyperedge))
            throw new NotSupportedException(
                $"columnar is only supported for Node / Relationship / Hyperedge, got {kind}.");
        if (_columns.ContainsKey((kind, keyId))) return false;

        byte tenantId = Catalog.Register(kind, keyId);
        var store = new ScalarColumnStore(_container.OpenTenant(tenantId, PageKind.ItemPointerMap));
        BuildFromData(store, kind, keyId);
        _columns[(kind, keyId)] = store;
        return true;
    }

    /// <summary>列登録を解除する (テナントの物理回収は後続)。</summary>
    public bool DropColumn(EntityKind kind, int keyId)
    {
        if (!_columns.Remove((kind, keyId))) return false;
        Catalog.Unregister(kind, keyId);
        return true;
    }

    private void BuildFromData(ScalarColumnStore store, EntityKind kind, int keyId)
    {
        var key = new PropertyKeyId(keyId);
        if (kind == EntityKind.Relationship)
        {
            foreach (var relId in _relStore.Scan())
                if (TryExtractScalar(_relStore.EnumerateProperties(relId, _propStore), key, out long bits, out var type))
                    store.Set(relId.Sequence, bits, TransactionId.Bootstrap.Value, type);
        }
        else if (kind == EntityKind.Node)
        {
            foreach (var nodeId in _nodeStore.Scan())
                if (TryExtractScalar(_nodeStore.EnumerateProperties(nodeId, _propStore), key, out long bits, out var type))
                    store.Set(nodeId.Sequence, bits, TransactionId.Bootstrap.Value, type);
        }
        else
        {
            foreach (var hyperedgeId in _hyperedgeStore.Scan())
                if (TryExtractScalar(
                        _hyperedgeStore.EnumerateProperties(hyperedgeId, _propStore),
                        key,
                        out long bits,
                        out var type))
                    store.Set(hyperedgeId.Sequence, bits, TransactionId.Bootstrap.Value, type);
        }
    }

    private static bool TryExtractScalar(PropertyEnumerator pe, PropertyKeyId key, out long bits, out PropertyValueType type)
    {
        bits = 0; type = default;
        while (pe.MoveNext())
        {
            var cur = pe.Current;
            if (cur.KeyId != key) continue;
            type = cur.Value.Type;
            return TryScalarBits(cur.Value, out bits);
        }
        return false;
    }

    /// <summary>scalar (Bool/Int32/Int64/Double) を 8B にパックする。非 scalar は false。</summary>
    private static bool TryScalarBits(in PropertyValue value, out long bits)
    {
        if (!InlinePropertyCodec.IsScalar(value.Type)) { bits = 0; return false; }
        bits = value.Type switch
        {
            PropertyValueType.Bool => value.BoolValue ? 1L : 0L,
            PropertyValueType.Int32 => value.Int32Value,
            PropertyValueType.Int64 => value.Int64Value,
            PropertyValueType.Double => BitConverter.DoubleToInt64Bits(value.DoubleValue),
            _ => 0L,
        };
        return true;
    }
}
