using Quiver.Core;

namespace Quiver;

/// <summary>
/// PW-16 / codex_advice_3 7.5 節。<see cref="GraphStats"/> を裏で支える 2 層の
/// ノード毎 degree lookup。観測された <c>NodeId</c> 空間が密
/// (<c>maxNodeId / nodeCount &lt;= DenseThreshold</c>) な場合は
/// <c>NodeId.Value</c> をインデックスとする direct 配列 + ノード毎 1 ビットの
/// パワーノードフラグを使い、ボクシング無し・辞書チェーンウォーク無しで <c>O(1)</c> 参照を行う。
/// ID 空間が疎な場合は、パワーノードエントリのみをマテリアライズするノード毎辞書に
/// フォールバックする (PW-16 以前のメモリフットプリントを維持)。
/// </summary>
public sealed class NodeDegreeLookup
{
    /// <summary>
    /// 密経路に進むための <c>(maxNodeId + 1) / nodeCount</c> の上限比率。既定 <c>4.0</c> なら
    /// 少なくとも ID 空間の 25% を使い切ったグラフが密と判定される。それ未満の疎なグラフは
    /// 辞書にフォールバックし、数千ノードしか持たないが ID 範囲が巨大なグラフに数百 MB を
    /// 確保するのを避ける。
    /// </summary>
    public const double DefaultDenseThreshold = 4.0;

    /// <summary>ノードが 1 つも記録されていない空 lookup の sentinel。</summary>
    public static readonly NodeDegreeLookup Empty = new(
        dense: false,
        outDegrees: null,
        inDegrees: null,
        powerNodeBits: null,
        denseLength: 0,
        sparseDegrees: null,
        sparsePowerNodes: new Dictionary<NodeId, NodeDegreeSummary>(),
        powerNodeThreshold: GraphStats.PowerNodeDegreeThreshold,
        maxNodeIdObserved: -1,
        denseThreshold: DefaultDenseThreshold);

    private readonly long[]? _outDegrees;
    private readonly long[]? _inDegrees;
    private readonly ulong[]? _powerNodeBits;
    private readonly int _denseLength;
    private readonly IReadOnlyDictionary<NodeId, NodeDegreeSummary>? _sparseDegrees;
    private readonly IReadOnlyDictionary<NodeId, NodeDegreeSummary> _sparsePowerNodes;

    /// <summary>密モード (direct 配列) なら true。</summary>
    public bool IsDense { get; }

    /// <summary>密モードで内部配列が確保するスロット数。</summary>
    public int DenseLength => _denseLength;

    /// <summary>パワーノード判定の degree しきい値。</summary>
    public long PowerNodeThreshold { get; }

    /// <summary>観測された最大 NodeId。記録されていない場合は -1。</summary>
    public long MaxNodeIdObserved { get; }

    /// <summary>密 / 疎切り替えに用いたしきい値。</summary>
    public double DenseThreshold { get; }

    /// <summary>記録されたパワーノードの件数。</summary>
    public int PowerNodeCount { get; }

    private NodeDegreeLookup(
        bool dense,
        long[]? outDegrees,
        long[]? inDegrees,
        ulong[]? powerNodeBits,
        int denseLength,
        IReadOnlyDictionary<NodeId, NodeDegreeSummary>? sparseDegrees,
        IReadOnlyDictionary<NodeId, NodeDegreeSummary> sparsePowerNodes,
        long powerNodeThreshold,
        long maxNodeIdObserved,
        double denseThreshold)
    {
        IsDense = dense;
        _outDegrees = outDegrees;
        _inDegrees = inDegrees;
        _powerNodeBits = powerNodeBits;
        _denseLength = denseLength;
        _sparseDegrees = sparseDegrees;
        _sparsePowerNodes = sparsePowerNodes;
        PowerNodeThreshold = powerNodeThreshold;
        MaxNodeIdObserved = maxNodeIdObserved;
        DenseThreshold = denseThreshold;
        PowerNodeCount = sparsePowerNodes.Count;
    }

    /// <summary>
    /// <paramref name="nodeId"/> の out/in degree を返す。
    /// 密モードかつ範囲内の場合、または疎モードでパワーノードとして追跡されている場合 (疎モードでは
    /// パワーノードのみ追跡される) に <c>true</c> を返す。それ以外は <c>false</c>。
    /// <c>false</c> を「degree が 0」と解釈してはいけない。
    /// </summary>
    public bool TryGetDegree(NodeId nodeId, out long outDegree, out long inDegree)
    {
        long v = nodeId.Sequence; // ARCH-5b: dense 配列 index は Sequence
        if (IsDense)
        {
            if ((ulong)v < (ulong)_denseLength)
            {
                outDegree = _outDegrees![v];
                inDegree = _inDegrees![v];
                return true;
            }
            outDegree = 0;
            inDegree = 0;
            return false;
        }

        if (_sparseDegrees is not null && _sparseDegrees.TryGetValue(nodeId, out var s))
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
    /// O(1) でパワーノードかを判定する。密モードでは 1 ビット読み出し、疎モードでは
    /// 辞書の <c>ContainsKey</c> にフォールバックする。
    /// </summary>
    public bool IsLikelyPowerNode(NodeId nodeId)
    {
        long v = nodeId.Sequence; // ARCH-5b: dense bit index は Sequence
        if (IsDense)
        {
            if ((ulong)v >= (ulong)_denseLength) return false;
            int word = (int)(v >> 6);
            int bit = (int)(v & 63);
            return (_powerNodeBits![word] & (1UL << bit)) != 0;
        }
        return _sparsePowerNodes.ContainsKey(nodeId);
    }

    /// <summary>
    /// 全パワーノードを列挙する。密モードはビットセットを走査、疎モードは内部辞書を列挙する。
    /// 密モードでは順序は安定 (<c>NodeId.Value</c> 昇順)、疎モードでは順序は未定義。
    /// </summary>
    public IEnumerable<NodeDegreeSummary> EnumeratePowerNodes()
    {
        if (IsDense)
        {
            int len = _denseLength;
            for (int word = 0; word < _powerNodeBits!.Length; word++)
            {
                ulong bits = _powerNodeBits[word];
                while (bits != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    long v = ((long)word << 6) | (uint)bit;
                    if (v >= len) yield break;
                    yield return new NodeDegreeSummary(new NodeId(v), _outDegrees![v], _inDegrees![v]);
                }
            }
        }
        else
        {
            foreach (var kv in _sparsePowerNodes)
                yield return kv.Value;
        }
    }

    /// <summary>
    /// パワーノード集合を辞書としてスナップショット化する。
    /// <see cref="GraphStats.PowerNodes"/> の既存呼び出し側を、密モード収集時に
    /// 辞書を前もってマテリアライズせずに済む形で維持するために存在する。
    /// </summary>
    public IReadOnlyDictionary<NodeId, NodeDegreeSummary> SnapshotPowerNodes()
    {
        if (!IsDense) return _sparsePowerNodes;
        var dict = new Dictionary<NodeId, NodeDegreeSummary>(capacity: PowerNodeCount);
        foreach (var s in EnumeratePowerNodes())
            dict[s.NodeId] = s;
        return dict;
    }

    // ──────────────────────────── Builder ────────────────────────────

    internal sealed class Builder
    {
        private readonly List<NodeDegreeRecord> _records = [];
        private readonly Dictionary<NodeId, NodeDegreeSummary> _powerNodes = [];
        private readonly long _powerNodeThreshold;
        private readonly double _denseThreshold;
        private long _maxNodeId = -1;

        internal Builder(long powerNodeThreshold, double denseThreshold)
        {
            _powerNodeThreshold = powerNodeThreshold;
            _denseThreshold = denseThreshold;
        }

        internal void Record(NodeId nodeId, long outDegree, long inDegree)
        {
            _records.Add(new NodeDegreeRecord(nodeId, outDegree, inDegree));
            if (nodeId.Sequence > _maxNodeId) _maxNodeId = nodeId.Sequence; // ARCH-5b: dense サイズは Sequence
            long total = outDegree + inDegree;
            if (total >= _powerNodeThreshold)
                _powerNodes[nodeId] = new NodeDegreeSummary(nodeId, outDegree, inDegree);
        }

        internal NodeDegreeLookup Build()
        {
            int n = _records.Count;
            if (n == 0)
            {
                return new NodeDegreeLookup(
                    dense: false,
                    outDegrees: null, inDegrees: null, powerNodeBits: null, denseLength: 0,
                    sparseDegrees: null, sparsePowerNodes: _powerNodes,
                    powerNodeThreshold: _powerNodeThreshold,
                    maxNodeIdObserved: _maxNodeId,
                    denseThreshold: _denseThreshold);
            }

            long denseCapacity = _maxNodeId + 1;
            double ratio = (double)denseCapacity / n;
            bool dense = ratio <= _denseThreshold;

            // 密確保を上限で制限する: 約 3200 万ノードを超えると direct 配列 (ノード毎 16 B + 1 bit) が
            // 巨大になり、呼び出し側に明示同意を求めるべき規模になる。ここでは無断で疎フォールバックする
            // — 呼び出し側は Collect の threshold パラメータで挙動をオーバーライドできる。
            if (denseCapacity > int.MaxValue / 16) dense = false;

            if (dense)
            {
                int len = (int)denseCapacity;
                var outs = new long[len];
                var ins = new long[len];
                int bitWords = (len + 63) >> 6;
                var bits = new ulong[bitWords == 0 ? 1 : bitWords];

                foreach (var r in _records)
                {
                    int v = (int)r.NodeId.Sequence; // ARCH-5b: dense 配列 index は Sequence
                    outs[v] = r.OutDegree;
                    ins[v] = r.InDegree;
                    if (r.OutDegree + r.InDegree >= _powerNodeThreshold)
                    {
                        int word = v >> 6;
                        int bit = v & 63;
                        bits[word] |= 1UL << bit;
                    }
                }

                return new NodeDegreeLookup(
                    dense: true,
                    outDegrees: outs, inDegrees: ins, powerNodeBits: bits, denseLength: len,
                    sparseDegrees: null, sparsePowerNodes: _powerNodes,
                    powerNodeThreshold: _powerNodeThreshold,
                    maxNodeIdObserved: _maxNodeId,
                    denseThreshold: _denseThreshold);
            }

            // 疎モード: ノード毎辞書はパワーノードのみマテリアライズする。
            // PW-16 以前のメモリ挙動に揃える。
            return new NodeDegreeLookup(
                dense: false,
                outDegrees: null, inDegrees: null, powerNodeBits: null, denseLength: 0,
                sparseDegrees: _powerNodes,
                sparsePowerNodes: _powerNodes,
                powerNodeThreshold: _powerNodeThreshold,
                maxNodeIdObserved: _maxNodeId,
                denseThreshold: _denseThreshold);
        }

        private readonly record struct NodeDegreeRecord(NodeId NodeId, long OutDegree, long InDegree);
    }
}
