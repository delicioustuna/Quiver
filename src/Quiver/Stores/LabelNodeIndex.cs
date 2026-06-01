using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// VEC-11: ラベル → 生存中 <see cref="NodeId"/> 集合の in-memory inverted index。
/// バイナリ backend で <c>NodeByLabelScanOperator</c> の O(N) 走査を
/// O(|L|) lookup に置き換えるための sidecar。
/// 永続化はしない: backend open 時に <see cref="Rebuild"/> で一度だけ
/// <see cref="INodeStore.Scan"/> から再構築する。WAL 復旧後に呼ばれる前提のため、
/// crash recovery 時もそのまま整合する。
/// </summary>
internal sealed class LabelNodeIndex
{
    private readonly Dictionary<int, SortedSet<long>> _byLabel = new();
    private bool _built;

    /// <summary>index が既に <see cref="Rebuild"/> 済みなら true。</summary>
    public bool IsBuilt => _built;

    /// <summary>
    /// 未構築なら full scan で再構築する。構築済みなら何もしない。
    /// 呼び出し側 (lookup 経路) からの暗黙的な lazy build フックとして用いる。
    /// </summary>
    public void EnsureBuilt(INodeStore nodes)
    {
        if (_built) return;
        Rebuild(nodes);
    }

    /// <summary>
    /// 既存内容を破棄して <see cref="INodeStore.Scan"/> から再構築する。
    /// O(N) のため、起動時 / bulk load 後の冪等な再構築に限定する想定。
    /// </summary>
    public void Rebuild(INodeStore nodes)
    {
        _byLabel.Clear();
        foreach (var nodeId in nodes.Scan())
        {
            var h = nodes.Read(nodeId);
            if (!h.InUse) continue;
            AddCore(h.Label.Value, nodeId.Value);
        }
        _built = true;
    }

    /// <summary>
    /// index 内容を捨てて未構築状態に戻す。bulk load 完了時に呼ぶことで、
    /// 次回 <see cref="EnsureBuilt"/> 経由で再構築される。
    /// </summary>
    public void Invalidate()
    {
        _byLabel.Clear();
        _built = false;
    }

    /// <summary>
    /// <see cref="NodeStore.Allocate"/> から通知され、生存中ノード集合へ追加する。
    /// 未構築なら何もしない (次の lookup で full rebuild されるため)。
    /// </summary>
    public void OnAllocate(NodeId id, LabelId label)
    {
        if (!_built) return;
        AddCore(label.Value, id.Value);
    }

    /// <summary>
    /// <see cref="NodeStore.Free"/> から通知され、生存中集合から取り除く。
    /// <paramref name="previousLabel"/> は free 前にレコードから読み出した値である必要がある。
    /// </summary>
    public void OnFree(NodeId id, LabelId previousLabel)
    {
        if (!_built) return;
        if (_byLabel.TryGetValue(previousLabel.Value, out var set))
            set.Remove(id.Value);
    }

    /// <summary>
    /// <paramref name="label"/> を持つ生存中の NodeId を昇順で返す。
    /// 列挙中の追加に対して安全なように内部でスナップショットを取る
    /// (NodeStore.Scan と同じ列挙安定性を維持する)。
    /// </summary>
    public IEnumerable<NodeId> Lookup(INodeStore nodes, LabelId label)
    {
        EnsureBuilt(nodes);
        if (!_byLabel.TryGetValue(label.Value, out var set) || set.Count == 0)
            return [];

        var snapshot = new long[set.Count];
        set.CopyTo(snapshot);
        return WrapNodeIds(snapshot);
    }

    /// <summary>
    /// 観測されたラベルの個数。テスト / 診断用。
    /// </summary>
    public int DistinctLabelCount => _byLabel.Count;

    /// <summary>指定ラベルに紐付く生存中ノード数。テスト / 診断用。</summary>
    public int CountFor(LabelId label)
        => _byLabel.TryGetValue(label.Value, out var s) ? s.Count : 0;

    /// <summary>
    /// FT-22: 現在 index に載っている (label, nodeId) ペアを列挙する。
    /// 未構築時は空。orphan 検出側で <see cref="INodeStore.Read"/> を引いて
    /// <c>InUse</c> を確認する用途を想定。
    /// </summary>
    public IEnumerable<(LabelId Label, NodeId Node)> EnumerateEntries()
    {
        if (!_built) yield break;
        foreach (var (labelValue, set) in _byLabel)
        {
            foreach (var nodeIdValue in set)
                yield return (new LabelId(labelValue), new NodeId(nodeIdValue));
        }
    }

    private void AddCore(int labelValue, long nodeIdValue)
    {
        if (!_byLabel.TryGetValue(labelValue, out var set))
            _byLabel[labelValue] = set = new SortedSet<long>();
        set.Add(nodeIdValue);
    }

    private static IEnumerable<NodeId> WrapNodeIds(long[] arr)
    {
        foreach (var v in arr) yield return new NodeId(v);
    }
}
