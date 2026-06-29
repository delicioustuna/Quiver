using System.Buffers;
using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// 1 つのベクトルインデックスの HNSW (Hierarchical Navigable Small World) ANN 索引。
/// グラフ構造を container テナントのページに永続化し (再起動跨ぎで再現)、in-memory 隣接キャッシュ
/// (open 時にページから rebuild) を read 経路に使う。書き込みはページ write-through で、container の
/// 単一物理 PagedFile を経由するため、アクティブ tx の WalPageContext 下なら WAL/ARIES に乗り
/// abort/crash でグラフ構造ごと巻き戻る。
///
/// <para><b>ページレイアウト</b>:</para>
/// <list type="bullet">
///   <item>page 1 = ヘッダ: entryPoint(i64) / maxLevel(i32) / count(i64) / maxSeq(i64) / format(byte@31)</item>
///   <item>page 2+ = seq 直接 index の node レコード (固定長, striping):
///     <c>[present:1 | level:1 | pad:2 | counts:MaxLayers | neighbors: (Mmax0 + (MaxLayers-1)*M) × i64]</c></item>
/// </list>
/// 距離は <c>-VectorMetrics.Score</c> (低いほど近い) で統一する。最終 top-k は <see cref="VectorKnnHeap"/>
/// と同じ「score 降順 / 同点 seq 昇順」で返すので、recall 100% のとき flat scan と完全一致する。
/// </summary>
internal sealed class HnswIndex
{
    // パラメタ (MVP 既定)。M = 層あたり近傍数、Mmax0 = 層0 の上限、efConstruction/efSearch = ビーム幅。
    private const int M = 16;
    private const int Mmax0 = 2 * M;        // 32
    private const int EfConstruction = 200;
    private const int MaxLayers = 8;         // level は 0..7 にクランプ
    private static readonly double ML = 1.0 / Math.Log(M);

    private const int NeighborSlots = Mmax0 + (MaxLayers - 1) * M; // 32 + 112 = 144
    private const int HeaderBytes = 4;       // present1 + level1 + pad2
    private const int CountsBytes = MaxLayers;
    private const int RecordSize = HeaderBytes + CountsBytes + NeighborSlots * 8; // 1164
    private static int Body => RecordPageMapping.PageBodySize;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaEntry = 0;          // i64 (-1 = empty)
    private const int MetaMaxLevel = 8;       // i32
    private const int MetaCount = 12;         // i64
    private const int MetaMaxSeq = 20;        // i64 (scan 上限)
    private const int MetaFormatVersion = 31; // byte

    private readonly IPagedFile _file;
    private readonly VectorPayloadStore _payload;
    private readonly DistanceMetric _metric;
    private readonly int _dim;

    // in-memory 隣接 (ページの写し)。Layers[level+1] 個の long[] (層ごとの近傍 seq)。
    private sealed class Node { public int Level; public required long[][] Layers; }
    private readonly Dictionary<long, Node> _nodes = new();

    private long _entry = -1;
    private int _maxLevel = -1;
    private long _count;
    private long _maxSeq;
    // 削除で生じた tombstone 数 (in-memory ヒューリスティック。reload で 0 にリセット)。
    // 削除が生存ノードを上回ると Rebuild を自動起動してグラフ劣化を回収する。
    private long _tombstones;

    // 決定的構築のための per-index 乱数 (seq を seed に混ぜて再現性を持たせる)。
    private readonly Random _rng = new(0x6D6E7377);

    public HnswIndex(IPagedFile file, VectorPayloadStore payload, DistanceMetric metric)
    {
        _file = file;
        _payload = payload;
        _metric = metric;
        _dim = payload.Dimensions;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            SaveMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
            RebuildFromPages();
        }
    }

    public long Count => _count;

    /// <summary>
    /// seq (= payload に既に書かれたベクトル) を HNSW へ挿入する。既存 seq は no-op
    /// (overwrite は payload のみ更新しグラフ位置は保つ — MVP の制限)。
    /// </summary>
    public void Insert(long seq)
    {
        if (_nodes.ContainsKey(seq)) return;
        var buf = ArrayPool<float>.Shared.Rent(_dim);
        try
        {
            var q = buf.AsSpan(0, _dim);
            if (!_payload.TryGet(seq, q, out _)) return; // payload 無し → 何もしない

            int level = RandomLevel();
            var node = new Node { Level = level, Layers = NewLayers(level) };
            _nodes[seq] = node;
            if (seq >= _maxSeq) _maxSeq = seq + 1;
            _count++;

            if (_entry < 0)
            {
                _entry = seq; _maxLevel = level;
                Persist(seq); SaveMeta();
                return;
            }

            long ep = _entry;
            int curMax = _maxLevel;
            // 上位層を ef=1 で貪欲降下し、挿入層直上まで entry を絞る。
            for (int lc = curMax; lc > level; lc--)
                ep = GreedyDescent(q, ep, lc);

            var touched = new HashSet<long> { seq };
            // 挿入層以下で beam search → M 近傍を選び双方向リンク。
            for (int lc = Math.Min(level, curMax); lc >= 0; lc--)
            {
                var w = SearchLayer(q, ep, EfConstruction, lc);
                int mm = lc == 0 ? Mmax0 : M;
                var selected = SelectNeighbors(w, Math.Min(M, mm));
                node.Layers[lc] = selected.ToArray();
                foreach (var nb in selected)
                {
                    AddNeighbor(nb, lc, seq);
                    touched.Add(nb);
                }
                if (w.Count > 0) ep = w[0].Seq; // 次の層の entry
            }

            if (level > _maxLevel) { _entry = seq; _maxLevel = level; }
            foreach (var t in touched) Persist(t);
            SaveMeta();
        }
        finally { ArrayPool<float>.Shared.Return(buf); }
    }

    /// <summary>
    /// overwrite 時の再リンク。既存 seq はグラフから外して新ベクトルで挿入し直す。
    /// payload は呼び出し側が事前に更新済みの前提。新規 seq は単純 <see cref="Insert"/>。
    /// </summary>
    public void Upsert(long seq)
    {
        if (_nodes.ContainsKey(seq)) Delete(seq);
        Insert(seq);
    }

    /// <summary>
    /// seq をグラフから物理削除する。全近傍の隣接リストから seq を除去し、entry なら
    /// 付け替える。removed レコードは present=0 で永続化。削除が蓄積したら <see cref="Rebuild"/> で回収。
    /// グラフに無い seq は no-op。
    /// </summary>
    public void Delete(long seq)
    {
        if (!_nodes.Remove(seq, out var removed)) return;
        _count = Math.Max(0, _count - 1);

        // 全ノードの隣接から seq への back-ref を除去する (近傍は非対称になりうるため全走査)。
        var touched = new HashSet<long>();
        foreach (var (otherSeq, other) in _nodes)
        {
            bool changed = false;
            for (int lc = 0; lc < other.Layers.Length; lc++)
            {
                var arr = other.Layers[lc];
                int idx = Array.IndexOf(arr, seq);
                if (idx < 0) continue;
                var shrunk = new long[arr.Length - 1];
                Array.Copy(arr, 0, shrunk, 0, idx);
                Array.Copy(arr, idx + 1, shrunk, idx, arr.Length - idx - 1);
                other.Layers[lc] = shrunk;
                changed = true;
            }
            if (changed) touched.Add(otherSeq);
        }

        // グラフ修復。削除ノードの近傍同士を層ごとに張り直し、ナビゲーション経路の穴を塞ぐ。
        // 再取込で削除が常態化してもグラフが断片化せず、Rebuild を待たずに recall を保てる。
        HealNeighborhood(removed, touched);

        // entry の付け替え (最高 level の残存ノード)。
        if (_entry == seq)
        {
            _entry = -1; _maxLevel = -1;
            foreach (var (s, n) in _nodes)
                if (n.Level > _maxLevel) { _maxLevel = n.Level; _entry = s; }
        }

        WriteAbsent(seq);
        foreach (var t in touched) Persist(t);
        _tombstones++;
        SaveMeta();

        // 削除が生存ノードを上回り、かつ一定数たまったらグラフを再構築して劣化 (とレコード領域) を回収する。
        if (_tombstones >= 16 && _tombstones > _count) Rebuild();
    }

    /// <summary>
    /// 削除ノード <paramref name="removed"/> の各層の近傍同士を相互リンクして局所的な穴を塞ぐ。
    /// 既存リンクは重複追加せず、上限超過は <see cref="AddNeighbor"/> の prune で最近接を保持する。
    /// 触れたノードは <paramref name="touched"/> に積み、呼び出し側で永続化する。
    /// </summary>
    private void HealNeighborhood(Node removed, HashSet<long> touched)
    {
        for (int lc = 0; lc < removed.Layers.Length; lc++)
        {
            var nbrs = removed.Layers[lc];
            if (nbrs.Length < 2) continue; // 近傍 1 個では結ぶ相手がいない
            for (int i = 0; i < nbrs.Length; i++)
            {
                long a = nbrs[i];
                if (!_nodes.TryGetValue(a, out var an) || lc > an.Level) continue;
                bool linked = false;
                for (int j = 0; j < nbrs.Length; j++)
                {
                    if (i == j) continue;
                    long b = nbrs[j];
                    if (a == b || !_nodes.ContainsKey(b)) continue;
                    if (Array.IndexOf(an.Layers[lc], b) >= 0) continue; // 既に隣接
                    AddNeighbor(a, lc, b);
                    linked = true;
                }
                if (linked) touched.Add(a);
            }
        }
    }

    /// <summary>
    /// payload の present な seq だけから HNSW を全再構築する (削除蓄積でグラフが
    /// 劣化したとき)。旧レコード領域を一掃してから昇順に挿入し直す。
    /// </summary>
    public void Rebuild()
    {
        var present = new List<long>();
        var buf = ArrayPool<float>.Shared.Rent(_dim);
        try
        {
            var dest = buf.AsSpan(0, _dim);
            long hwm = _payload.Hwm;
            for (long seq = 0; seq < hwm; seq++)
                if (_payload.TryGet(seq, dest, out _)) present.Add(seq);
        }
        finally { ArrayPool<float>.Shared.Return(buf); }

        for (long seq = 0; seq < _maxSeq; seq++) WriteAbsent(seq);
        _nodes.Clear();
        _entry = -1; _maxLevel = -1; _count = 0; _maxSeq = 0; _tombstones = 0;
        foreach (var seq in present) Insert(seq);
        SaveMeta();
    }

    /// <summary>
    /// query に最も近い top-k を (seq, score) で返す。score は <see cref="VectorMetrics.Score"/>
    /// (高いほど近い)。グラフが空なら空配列。removed/absent な payload は除外する。
    /// <paramref name="isLive"/> は世代照合— false の候補は除外する。
    /// </summary>
    public VectorSearchResult[] Search(
        ReadOnlySpan<float> query, int k, EntityKind kind, Func<long, ushort, bool> isLive,
        Func<long, bool>? inFilter = null)
    {
        if (_entry < 0 || k <= 0) return Array.Empty<VectorSearchResult>();
        long ep = _entry;
        for (int lc = _maxLevel; lc > 0; lc--)
            ep = GreedyDescent(query, ep, lc);

        // ③: フィルタ付き検索は post-filter で k 件に満たなくなりうるため ef をオーバーサンプルする。
        int ef = inFilter is null ? Math.Max(EfConstruction, k) : Math.Max(EfConstruction, k * 8);
        var w = SearchLayer(query, ep, ef, 0);

        // 層0の候補を payload で再スコアし、present + 世代照合 (+ フィルタ) を通したものだけ top-k へ。
        var heap = new VectorKnnHeap(k);
        var buf = ArrayPool<float>.Shared.Rent(_dim);
        try
        {
            var dest = buf.AsSpan(0, _dim);
            foreach (var c in w)
            {
                if (inFilter is not null && !inFilter(c.Seq)) continue;
                if (!_payload.TryGet(c.Seq, dest, out var gen)) continue;
                if (!isLive(c.Seq, gen)) continue;
                heap.Offer(new VectorSearchResult(kind, c.Seq, VectorMetrics.Score(_metric, query, dest)));
            }
        }
        finally { ArrayPool<float>.Shared.Return(buf); }
        return heap.ToSortedArray();
    }

    /// <summary>abort の before-image undo 後 / clean reopen で呼ばれ、ページから in-memory を再構築する。</summary>
    public void ReloadFromPages()
    {
        _nodes.Clear();
        LoadMeta();
        RebuildFromPages();
    }

    // ===== HNSW コア =====

    private int RandomLevel()
    {
        int lvl = (int)(-Math.Log(1.0 - _rng.NextDouble()) * ML);
        return Math.Min(lvl, MaxLayers - 1);
    }

    private static long[][] NewLayers(int level)
    {
        var layers = new long[level + 1][];
        for (int i = 0; i <= level; i++) layers[i] = Array.Empty<long>();
        return layers;
    }

    private long GreedyDescent(ReadOnlySpan<float> q, long ep, int layer)
    {
        var bufA = ArrayPool<float>.Shared.Rent(_dim);
        var bufB = ArrayPool<float>.Shared.Rent(_dim);
        try
        {
            double bestDist = Dist(q, ep, bufA);
            bool improved = true;
            while (improved)
            {
                improved = false;
                if (!_nodes.TryGetValue(ep, out var node) || layer > node.Level) break;
                foreach (var nb in node.Layers[layer])
                {
                    double d = Dist(q, nb, bufB);
                    if (d < bestDist)
                    {
                        bestDist = d; ep = nb; improved = true;
                    }
                }
            }
            return ep;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(bufA);
            ArrayPool<float>.Shared.Return(bufB);
        }
    }

    private readonly record struct Cand(long Seq, double Dist);

    private List<Cand> SearchLayer(ReadOnlySpan<float> q, long entry, int ef, int layer)
    {
        var buf = ArrayPool<float>.Shared.Rent(_dim);
        try
        {
            var visited = new HashSet<long> { entry };
            double ed = Dist(q, entry, buf);
            // candidates: 近い順に展開する min-heap (priority = dist)。
            var candidates = new PriorityQueue<long, double>();
            // results: 最遠を peek できる max-heap (priority = -dist)、ef で bound。
            var results = new PriorityQueue<long, double>();
            candidates.Enqueue(entry, ed);
            results.Enqueue(entry, -ed);

            while (candidates.Count > 0)
            {
                candidates.TryDequeue(out long c, out double cd);
                // results の最遠 (= -priority が最小 → priority 最大... PriorityQueue は min-priority を peek)。
                results.TryPeek(out _, out double negFar);
                double farthest = -negFar;
                if (cd > farthest && results.Count >= ef) break;

                if (!_nodes.TryGetValue(c, out var node) || layer > node.Level) continue;
                foreach (var nb in node.Layers[layer])
                {
                    if (!visited.Add(nb)) continue;
                    double d = Dist(q, nb, buf);
                    results.TryPeek(out _, out double nf);
                    double far = -nf;
                    if (d < far || results.Count < ef)
                    {
                        candidates.Enqueue(nb, d);
                        results.Enqueue(nb, -d);
                        if (results.Count > ef) results.Dequeue(); // 最遠を捨てる
                    }
                }
            }

            // results を近い順に並べて返す。
            var list = new List<Cand>(results.Count);
            while (results.Count > 0)
            {
                results.TryDequeue(out long s, out double negd);
                list.Add(new Cand(s, -negd));
            }
            list.Sort(static (a, b) =>
            {
                int c = a.Dist.CompareTo(b.Dist);
                return c != 0 ? c : a.Seq.CompareTo(b.Seq);
            });
            return list;
        }
        finally { ArrayPool<float>.Shared.Return(buf); }
    }

    // 単純ヒューリスティック: 近い順 m 件を採用。
    private static List<long> SelectNeighbors(List<Cand> candidates, int m)
    {
        var res = new List<long>(Math.Min(m, candidates.Count));
        for (int i = 0; i < candidates.Count && res.Count < m; i++)
            res.Add(candidates[i].Seq);
        return res;
    }

    private void AddNeighbor(long node, int layer, long newNeighbor)
    {
        if (!_nodes.TryGetValue(node, out var n) || layer > n.Level) return;
        var cur = n.Layers[layer];
        int mmax = layer == 0 ? Mmax0 : M;
        if (cur.Length < mmax)
        {
            var grown = new long[cur.Length + 1];
            Array.Copy(cur, grown, cur.Length);
            grown[cur.Length] = newNeighbor;
            n.Layers[layer] = grown;
            return;
        }
        // 上限到達 → newNeighbor を含めた候補から最も近い mmax 件を選び直す (prune)。
        var buf = ArrayPool<float>.Shared.Rent(_dim);
        var nodeVecBuf = ArrayPool<float>.Shared.Rent(_dim);
        try
        {
            var nodeVec = nodeVecBuf.AsSpan(0, _dim);
            if (!_payload.TryGet(node, nodeVec, out _)) return;
            var pool = new List<long>(cur.Length + 1);
            pool.AddRange(cur);
            pool.Add(newNeighbor);
            var scored = new List<Cand>(pool.Count);
            foreach (var p in pool)
                scored.Add(new Cand(p, Dist(nodeVec, p, buf)));
            scored.Sort(static (a, b) =>
            {
                int c = a.Dist.CompareTo(b.Dist);
                return c != 0 ? c : a.Seq.CompareTo(b.Seq);
            });
            var kept = new long[mmax];
            for (int i = 0; i < mmax; i++) kept[i] = scored[i].Seq;
            n.Layers[layer] = kept;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buf);
            ArrayPool<float>.Shared.Return(nodeVecBuf);
        }
    }

    // dist(query, vec(seq)) = -Score (低いほど近い)。payload 無しは +∞。
    private double Dist(ReadOnlySpan<float> q, long seq, float[] scratch)
    {
        var v = scratch.AsSpan(0, _dim);
        if (!_payload.TryGet(seq, v, out _)) return double.PositiveInfinity;
        return -VectorMetrics.Score(_metric, q, v);
    }

    // ===== 永続化 (seq 直接 index の striped レコード) =====

    private void Persist(long seq)
    {
        if (!_nodes.TryGetValue(seq, out var n)) return;
        Span<byte> rec = stackalloc byte[RecordSize];
        rec.Clear();
        rec[0] = 1;                  // present
        rec[1] = (byte)n.Level;
        for (int lc = 0; lc <= n.Level && lc < MaxLayers; lc++)
        {
            var arr = n.Layers[lc];
            rec[HeaderBytes + lc] = (byte)arr.Length; // counts
            int baseSlot = LayerSlotBase(lc);
            int off = HeaderBytes + CountsBytes + baseSlot * 8;
            for (int i = 0; i < arr.Length; i++)
                BinaryPrimitives.WriteInt64LittleEndian(rec[(off + i * 8)..], arr[i]);
        }
        WriteBytes(seq * (long)RecordSize, rec);
    }

    private void WriteAbsent(long seq)
    {
        Span<byte> rec = stackalloc byte[RecordSize];
        rec.Clear(); // present=0
        WriteBytes(seq * (long)RecordSize, rec);
    }

    private void RebuildFromPages()
    {
        if (_maxSeq <= 0) return;
        Span<byte> rec = stackalloc byte[RecordSize];
        for (long seq = 0; seq < _maxSeq; seq++)
        {
            ReadBytes(seq * (long)RecordSize, rec);
            if (rec[0] != 1) continue;
            int level = rec[1];
            var layers = NewLayers(level);
            for (int lc = 0; lc <= level && lc < MaxLayers; lc++)
            {
                int cnt = rec[HeaderBytes + lc];
                int baseSlot = LayerSlotBase(lc);
                int off = HeaderBytes + CountsBytes + baseSlot * 8;
                var arr = new long[cnt];
                for (int i = 0; i < cnt; i++)
                    arr[i] = BinaryPrimitives.ReadInt64LittleEndian(rec[(off + i * 8)..]);
                layers[lc] = arr;
            }
            _nodes[seq] = new Node { Level = level, Layers = layers };
        }
    }

    private static int LayerSlotBase(int layer) => layer == 0 ? 0 : Mmax0 + (layer - 1) * M;

    private void ReadBytes(long logicalStart, Span<byte> dest)
    {
        int copied = 0;
        while (copied < dest.Length)
        {
            long pos = logicalStart + copied;
            var pid = new PageId(pos / Body + 2);
            int intra = (int)(pos % Body);
            int n = Math.Min(Body - intra, dest.Length - copied);
            if (_file.PageCount <= pid.Value) dest.Slice(copied, n).Clear();
            else { using var h = _file.PinForRead(pid); h.Data.Slice(intra, n).CopyTo(dest.Slice(copied, n)); }
            copied += n;
        }
    }

    private void WriteBytes(long logicalStart, ReadOnlySpan<byte> src)
    {
        int copied = 0;
        while (copied < src.Length)
        {
            long pos = logicalStart + copied;
            var pid = new PageId(pos / Body + 2);
            int intra = (int)(pos % Body);
            while (_file.PageCount <= pid.Value) _file.AllocatePage(PageKind.ItemPointerMap);
            int n = Math.Min(Body - intra, src.Length - copied);
            var ph = _file.PinForWrite(pid);
            src.Slice(copied, n).CopyTo(ph.Data.Slice(intra, n));
            _file.UnpinDirty(pid, 0);
            copied += n;
        }
    }

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _entry = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaEntry..]);
        _maxLevel = BinaryPrimitives.ReadInt32LittleEndian(h.Data[MetaMaxLevel..]);
        _count = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaCount..]);
        _maxSeq = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaMaxSeq..]);
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != FormatVersion.Current)
            throw new FormatVersionMismatchException("hnsw", v, FormatVersion.Current);
    }

    private void SaveMeta(bool initialise = false)
    {
        using var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaEntry..], _entry);
        BinaryPrimitives.WriteInt32LittleEndian(ph.Data[MetaMaxLevel..], _maxLevel);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaCount..], _count);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaMaxSeq..], _maxSeq);
        if (initialise) ph.Data[MetaFormatVersion] = FormatVersion.Current;
    }
}
