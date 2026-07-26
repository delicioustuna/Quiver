using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// ラベル → 生存中 <see cref="VertexId"/> 集合の in-memory inverted index。
/// バイナリ backend で <c>VertexByLabelScanOperator</c> の O(N) 走査を
/// O(|L|) lookup に置き換えるための sidecar。
/// 永続化はしない: backend open 時に <see cref="Rebuild"/> で一度だけ
/// <see cref="IVertexStore.Scan"/> から再構築する。WAL 復旧後に呼ばれる前提のため、
/// crash recovery 時もそのまま整合する。
/// </summary>
internal sealed class LabelVertexIndex
{
    private readonly Dictionary<int, SortedSet<long>> _byLabel = new();
    private bool _built;

    /// <summary>index が既に <see cref="Rebuild"/> 済みなら true。</summary>
    public bool IsBuilt => _built;

    /// <summary>
    /// 未構築なら full scan で再構築する。構築済みなら何もしない。
    /// 呼び出し側 (lookup 経路) からの暗黙的な lazy build フックとして用いる。
    /// </summary>
    public void EnsureBuilt(IVertexStore vertices)
    {
        if (_built) return;
        Rebuild(vertices);
    }

    /// <summary>
    /// 既存内容を破棄して <see cref="IVertexStore.Scan"/> から再構築する。
    /// O(N) のため、起動時 / bulk load 後の冪等な再構築に限定する想定。
    /// </summary>
    public void Rebuild(IVertexStore vertices)
    {
        _byLabel.Clear();
        foreach (var vertexId in vertices.Scan())
        {
            var h = vertices.Read(vertexId);
            if (!h.InUse) continue;
            AddCore(h.Label.Value, vertexId.Sequence); // index は Sequence を格納
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
    /// <see cref="VertexStore.Allocate"/> から通知され、生存中Vertex集合へ追加する。
    /// 未構築なら何もしない (次の lookup で full rebuild されるため)。
    /// </summary>
    public void OnAllocate(VertexId id, LabelId label)
    {
        if (!_built) return;
        AddCore(label.Value, id.Sequence); // index は Sequence を格納 (id は gen 付きで届きうる)
    }

    /// <summary>
    /// <see cref="VertexStore.Free"/> から通知され、生存中集合から取り除く。
    /// <paramref name="previousLabel"/> は free 前にレコードから読み出した値である必要がある。
    /// </summary>
    public void OnFree(VertexId id, LabelId previousLabel)
    {
        if (!_built) return;
        if (_byLabel.TryGetValue(previousLabel.Value, out var set))
            set.Remove(id.Sequence); // index は Sequence を格納 (id は gen 付きで届きうる)
    }

    /// <summary>
    /// <paramref name="label"/> を持つ生存中の VertexId を昇順で返す。
    /// 列挙中の追加に対して安全なように内部でスナップショットを取る
    /// (VertexStore.Scan と同じ列挙安定性を維持する)。
    /// </summary>
    public IEnumerable<VertexId> Lookup(IVertexStore vertices, LabelId label)
    {
        EnsureBuilt(vertices);
        if (!_byLabel.TryGetValue(label.Value, out var set) || set.Count == 0)
            return [];

        var snapshot = new long[set.Count];
        set.CopyTo(snapshot);
        return ResolveLiveVertexIds(vertices, snapshot);
    }

    /// <summary>
    /// 観測されたラベルの個数。テスト / 診断用。
    /// </summary>
    public int DistinctLabelCount => _byLabel.Count;

    /// <summary>指定ラベルに紐付く生存中Vertex数。テスト / 診断用。</summary>
    public int CountFor(LabelId label)
        => _byLabel.TryGetValue(label.Value, out var s) ? s.Count : 0;

    /// <summary>
    /// 現在 index に載っている (label, vertexId) ペアを列挙する。
    /// 未構築時は空。orphan 検出側で <see cref="IVertexStore.Read"/> を引いて
    /// <c>InUse</c> を確認する用途を想定。
    /// </summary>
    public IEnumerable<(LabelId Label, VertexId Vertex)> EnumerateEntries()
    {
        if (!_built) yield break;
        foreach (var (labelValue, set) in _byLabel)
        {
            foreach (var vertexIdValue in set)
                yield return (new LabelId(labelValue), new VertexId(vertexIdValue));
        }
    }

    private void AddCore(int labelValue, long vertexIdValue)
    {
        if (!_byLabel.TryGetValue(labelValue, out var set))
            _byLabel[labelValue] = set = new SortedSet<long>();
        set.Add(vertexIdValue);
    }

    private static IReadOnlyList<VertexId> ResolveLiveVertexIds(IVertexStore vertices, long[] sequences)
    {
        var resolved = new List<VertexId>(sequences.Length);
        foreach (long sequence in sequences)
        {
            int generation = vertices.CurrentGeneration(sequence);
            if (generation < 0)
                continue;

            var candidate = VertexId.Create(sequence, generation);
            using var vertex = vertices.Read(candidate);
            if (vertex.InUse)
                resolved.Add(vertex.Id);
        }
        return resolved;
    }
}
