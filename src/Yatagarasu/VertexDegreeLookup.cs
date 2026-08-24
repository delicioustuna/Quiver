using Yatagarasu.Core;

namespace Yatagarasu;

/// <summary>
/// <see cref="GraphStats"/> を裏で支える 2 層の
/// Vertex毎 degree lookup。観測された <c>VertexId</c> 空間が密
/// (<c>maxVertexId / vertexCount &lt;= DenseThreshold</c>) な場合は
/// <c>VertexId.Value</c> をインデックスとする direct 配列 + Vertex毎 1 ビットの
/// パワーVertexフラグを使い、ボクシング無し・辞書チェーンウォーク無しで <c>O(1)</c> 参照を行う。
/// ID 空間が疎な場合は、パワーVertexエントリのみをマテリアライズするVertex毎辞書に
/// フォールバックする (従来のメモリフットプリントを維持)。
/// </summary>
internal sealed class VertexDegreeLookup
{
    /// <summary>
    /// 密経路に進むための <c>(maxVertexId + 1) / vertexCount</c> の上限比率。既定 <c>4.0</c> なら
    /// 少なくとも ID 空間の 25% を使い切ったグラフが密と判定される。それ未満の疎なグラフは
    /// 辞書にフォールバックし、数千Vertexしか持たないが ID 範囲が巨大なグラフに数百 MB を
    /// 確保するのを避ける。
    /// </summary>
    public const double DefaultDenseThreshold = 4.0;

    /// <summary>Vertexが 1 つも記録されていない空 lookup の sentinel。</summary>
    public static readonly VertexDegreeLookup Empty = new(
        dense: false,
        outDegrees: null,
        inDegrees: null,
        vertexIds: null,
        powerVertexBits: null,
        denseLength: 0,
        sparseDegrees: null,
        sparsePowerVertices: new Dictionary<VertexId, VertexDegreeSummary>(),
        powerVertexThreshold: GraphStats.PowerVertexDegreeThreshold,
        maxVertexIdObserved: -1,
        denseThreshold: DefaultDenseThreshold);

    private readonly long[]? _outDegrees;
    private readonly long[]? _inDegrees;
    private readonly VertexId[]? _vertexIds;
    private readonly ulong[]? _powerVertexBits;
    private readonly int _denseLength;
    private readonly IReadOnlyDictionary<VertexId, VertexDegreeSummary>? _sparseDegrees;
    private readonly IReadOnlyDictionary<VertexId, VertexDegreeSummary> _sparsePowerVertices;

    /// <summary>密モード (direct 配列) なら true。</summary>
    public bool IsDense { get; }

    /// <summary>密モードで内部配列が確保するスロット数。</summary>
    public int DenseLength => _denseLength;

    /// <summary>パワーVertex判定の degree しきい値。</summary>
    public long PowerVertexThreshold { get; }

    /// <summary>観測された最大 VertexId。記録されていない場合は -1。</summary>
    public long MaxVertexIdObserved { get; }

    /// <summary>密 / 疎切り替えに用いたしきい値。</summary>
    public double DenseThreshold { get; }

    /// <summary>記録されたパワーVertexの件数。</summary>
    public int PowerVertexCount { get; }

    private VertexDegreeLookup(
        bool dense,
        long[]? outDegrees,
        long[]? inDegrees,
        VertexId[]? vertexIds,
        ulong[]? powerVertexBits,
        int denseLength,
        IReadOnlyDictionary<VertexId, VertexDegreeSummary>? sparseDegrees,
        IReadOnlyDictionary<VertexId, VertexDegreeSummary> sparsePowerVertices,
        long powerVertexThreshold,
        long maxVertexIdObserved,
        double denseThreshold)
    {
        IsDense = dense;
        _outDegrees = outDegrees;
        _inDegrees = inDegrees;
        _vertexIds = vertexIds;
        _powerVertexBits = powerVertexBits;
        _denseLength = denseLength;
        _sparseDegrees = sparseDegrees;
        _sparsePowerVertices = sparsePowerVertices;
        PowerVertexThreshold = powerVertexThreshold;
        MaxVertexIdObserved = maxVertexIdObserved;
        DenseThreshold = denseThreshold;
        PowerVertexCount = sparsePowerVertices.Count;
    }

    /// <summary>
    /// <paramref name="vertexId"/> の out/in degree を返す。
    /// 密モードかつ範囲内の場合、または疎モードでパワーVertexとして追跡されている場合 (疎モードでは
    /// パワーVertexのみ追跡される) に <c>true</c> を返す。それ以外は <c>false</c>。
    /// <c>false</c> を「degree が 0」と解釈してはいけない。
    /// </summary>
    public bool TryGetDegree(VertexId vertexId, out long outDegree, out long inDegree)
    {
        long v = vertexId.Sequence; // dense 配列 index は Sequence
        if (IsDense)
        {
            if ((ulong)v < (ulong)_denseLength)
            {
                VertexId logical = _vertexIds![v];
                if (vertexId.Generation != 0 && vertexId != logical)
                {
                    outDegree = 0;
                    inDegree = 0;
                    return false;
                }
                outDegree = _outDegrees![v];
                inDegree = _inDegrees![v];
                return true;
            }
            outDegree = 0;
            inDegree = 0;
            return false;
        }

        if (_sparseDegrees is not null && _sparseDegrees.TryGetValue(vertexId, out var s))
        {
            outDegree = s.OutDegree;
            inDegree = s.InDegree;
            return true;
        }
        outDegree = 0;
        inDegree = 0;
        return false;
    }

    /// <summary>
    /// O(1) でパワーVertexかを判定する。密モードでは 1 ビット読み出し、疎モードでは
    /// 辞書の <c>ContainsKey</c> にフォールバックする。
    /// </summary>
    public bool IsLikelyPowerVertex(VertexId vertexId)
    {
        long v = vertexId.Sequence; // dense bit index は Sequence
        if (IsDense)
        {
            if ((ulong)v >= (ulong)_denseLength) return false;
            int word = (int)(v >> 6);
            int bit = (int)(v & 63);
            return (_powerVertexBits![word] & (1UL << bit)) != 0;
        }
        return _sparsePowerVertices.ContainsKey(vertexId);
    }

    /// <summary>
    /// 全パワーVertexを列挙する。密モードはビットセットを走査、疎モードは内部辞書を列挙する。
    /// 密モードでは順序は安定 (<c>VertexId.Value</c> 昇順)、疎モードでは順序は未定義。
    /// </summary>
    public IEnumerable<VertexDegreeSummary> EnumeratePowerVertices()
    {
        if (IsDense)
        {
            int len = _denseLength;
            for (int word = 0; word < _powerVertexBits!.Length; word++)
            {
                ulong bits = _powerVertexBits[word];
                while (bits != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    long v = ((long)word << 6) | (uint)bit;
                    if (v >= len) yield break;
                    yield return new VertexDegreeSummary(_vertexIds![v], _outDegrees![v], _inDegrees![v]);
                }
            }
        }
        else
        {
            foreach (var kv in _sparsePowerVertices)
                yield return kv.Value;
        }
    }

    /// <summary>
    /// パワーVertex集合を辞書としてスナップショット化する。
    /// <see cref="GraphStats.PowerVertices"/> の既存呼び出し側を、密モード収集時に
    /// 辞書を前もってマテリアライズせずに済む形で維持するために存在する。
    /// </summary>
    public IReadOnlyDictionary<VertexId, VertexDegreeSummary> SnapshotPowerVertices()
    {
        if (!IsDense) return _sparsePowerVertices;
        var dict = new Dictionary<VertexId, VertexDegreeSummary>(capacity: PowerVertexCount);
        foreach (var s in EnumeratePowerVertices())
            dict[s.VertexId] = s;
        return dict;
    }

    // ──────────────────────────── ビルダー ───────────────────────────

    internal sealed class Builder
    {
        private readonly List<VertexDegreeRecord> _records = [];
        private readonly Dictionary<VertexId, VertexDegreeSummary> _powerVertices = [];
        private readonly long _powerVertexThreshold;
        private readonly double _denseThreshold;
        private long _maxVertexId = -1;

        internal Builder(long powerVertexThreshold, double denseThreshold)
        {
            _powerVertexThreshold = powerVertexThreshold;
            _denseThreshold = denseThreshold;
        }

        internal void Record(VertexId vertexId, long outDegree, long inDegree)
        {
            _records.Add(new VertexDegreeRecord(vertexId, outDegree, inDegree));
            if (vertexId.Sequence > _maxVertexId) _maxVertexId = vertexId.Sequence; // dense サイズは Sequence
            long total = outDegree + inDegree;
            if (total >= _powerVertexThreshold)
                _powerVertices[vertexId] = new VertexDegreeSummary(vertexId, outDegree, inDegree);
        }

        internal VertexDegreeLookup Build()
        {
            int n = _records.Count;
            if (n == 0)
            {
                return new VertexDegreeLookup(
                    dense: false,
                    outDegrees: null, inDegrees: null, vertexIds: null, powerVertexBits: null, denseLength: 0,
                    sparseDegrees: null, sparsePowerVertices: _powerVertices,
                    powerVertexThreshold: _powerVertexThreshold,
                    maxVertexIdObserved: _maxVertexId,
                    denseThreshold: _denseThreshold);
            }

            long denseCapacity = _maxVertexId + 1;
            double ratio = (double)denseCapacity / n;
            bool dense = ratio <= _denseThreshold;

            // 密確保を上限で制限する: 約 3200 万Vertexを超えると direct 配列 (Vertex毎 16 B + 1 bit) が
            // 巨大になり、呼び出し側に明示同意を求めるべき規模になる。ここでは無断で疎フォールバックする
            // — 呼び出し側は Collect の threshold パラメータで挙動をオーバーライドできる。
            if (denseCapacity > int.MaxValue / 16) dense = false;

            if (dense)
            {
                int len = (int)denseCapacity;
                var outs = new long[len];
                var ins = new long[len];
                var ids = new VertexId[len];
                int bitWords = (len + 63) >> 6;
                var bits = new ulong[bitWords == 0 ? 1 : bitWords];

                foreach (var r in _records)
                {
                    int v = (int)r.VertexId.Sequence; // dense 配列 index は Sequence
                    outs[v] = r.OutDegree;
                    ins[v] = r.InDegree;
                    ids[v] = r.VertexId;
                    if (r.OutDegree + r.InDegree >= _powerVertexThreshold)
                    {
                        int word = v >> 6;
                        int bit = v & 63;
                        bits[word] |= 1UL << bit;
                    }
                }

                return new VertexDegreeLookup(
                    dense: true,
                    outDegrees: outs, inDegrees: ins, vertexIds: ids, powerVertexBits: bits, denseLength: len,
                    sparseDegrees: null, sparsePowerVertices: _powerVertices,
                    powerVertexThreshold: _powerVertexThreshold,
                    maxVertexIdObserved: _maxVertexId,
                    denseThreshold: _denseThreshold);
            }

            // 疎モード: Vertex毎辞書はパワーVertexのみマテリアライズする。
            // 以前のメモリ挙動に揃える。
            return new VertexDegreeLookup(
                dense: false,
                outDegrees: null, inDegrees: null, vertexIds: null, powerVertexBits: null, denseLength: 0,
                sparseDegrees: _powerVertices,
                sparsePowerVertices: _powerVertices,
                powerVertexThreshold: _powerVertexThreshold,
                maxVertexIdObserved: _maxVertexId,
                denseThreshold: _denseThreshold);
        }

        private readonly record struct VertexDegreeRecord(VertexId VertexId, long OutDegree, long InDegree);
    }
}
