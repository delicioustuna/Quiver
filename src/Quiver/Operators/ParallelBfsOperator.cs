using System.Collections.Concurrent;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 並列 BFS 実装。<see cref="BfsOperator"/> が maxParallelism != 1 のとき使用する。
/// 上流のソースノードをすべて収集後、<see cref="Parallel.ForEach"/> で各ソースから
/// 独立 BFS を同時実行する。各タスクは専用の状態 (frontier キュー・visited HashSet) を
/// 所有するため、走査中の同期は不要 — 結果の集約のみ <see cref="ConcurrentBag{T}"/> を使う。
/// </summary>
/// <remarks>
/// <para>
/// 下層ストアは並行読み取り安全: TxNodeStore.Read() / TxRelationshipStore.Read() は
/// 内部ストアへ直接委譲し、PagedFile.PinForRead() は _poolLock 上で短時間だけ直列化する。
/// </para>
/// <para>
/// スキーマ: <c>(startNode NodeId, endNode NodeId, depth Int64)</c>。
/// タスクごとの BFS は <see cref="OneHopExpansion"/> と専用の <see cref="ParallelKernel"/> を使い、
/// 各タスクが独立した <see cref="FrontierKernelState"/> を持つ。
/// </para>
/// </remarks>
internal sealed class ParallelBfsOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _srcCol;
    private readonly Direction _dir;
    private readonly RelationshipTypeId? _typeFilter;
    private readonly int _maxDepth;
    private readonly int _maxParallelism;

    private ITransaction? _tx;
    private List<(NodeId start, NodeId end, int depth)>? _results;
    private int _resultIdx;
    private readonly TupleSlot[] _buffer = new TupleSlot[3];

    private static readonly TupleSchema s_schema = new([
        new ColumnDefinition("startNode", TupleSlotType.NodeId),
        new ColumnDefinition("endNode",   TupleSlotType.NodeId),
        new ColumnDefinition("depth",     TupleSlotType.Int64)]);

    /// <param name="maxParallelism">
    /// Parallel.ForEach に渡す最大並列度。-1 (既定) ならランタイムが利用可能コア数に基づき決定する。
    /// </param>
    public ParallelBfsOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        Direction direction,
        RelationshipTypeId? typeFilter,
        int maxDepth,
        int maxParallelism = -1)
    {
        if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        _source = source;
        _srcCol = sourceNodeColumn;
        _dir = direction;
        _typeFilter = typeFilter;
        _maxDepth = maxDepth;
        _maxParallelism = maxParallelism;
    }

    public TupleSchema Schema => s_schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _results = null;
        _resultIdx = 0;
    }

    public bool MoveNext()
    {
        if (_results == null)
            RunParallel();

        if (_resultIdx >= _results!.Count) return false;

        var (start, end, depth) = _results[_resultIdx++];
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = start.Value };
        _buffer[1] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = end.Value };
        _buffer[2] = new TupleSlot { Type = TupleSlotType.Int64,  LongValue = depth };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    private void RunParallel()
    {
        // 上流を逐次的に排出してソースノードを収集する。
        var sources = new List<NodeId>();
        while (_source.MoveNext())
            sources.Add(new NodeId(_source.Current[_srcCol].LongValue));

        var bag = new ConcurrentBag<(NodeId start, NodeId end, int depth)>();
        var tx = _tx!;
        var dir = _dir;
        var typeFilter = _typeFilter;
        var maxDepth = _maxDepth;
        var kernel = new ParallelKernel(maxDepth);

        Parallel.ForEach(
            sources,
            new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism },
            // タスクごとの状態ファクトリ: 各ワーカスレッドが専用の frontier / visited バッファを
            // 所有し、パーティショナが割り当てたソースノード間で再利用する。
            () => new FrontierKernelState(),
            (source, _, state) =>
            {
                kernel.Initialize(source, ref state);

                while (state.Frontier!.Count > 0)
                {
                    var (node, depth) = state.Frontier.Dequeue();

                    if (depth > 0)
                        bag.Add((source, node, depth));

                    if (kernel.ShouldContinue(depth, in state))
                        OneHopExpansion.Expand(tx, node, dir, typeFilter, depth, kernel, ref state);
                }
                return state;
            },
            _ => { });

        _results = [.. bag];
    }

    public void Dispose() => _source.Dispose();

    /// <summary>
    /// 全並列ワーカが共有するステートレス BFS カーネル。frontier / visited は
    /// タスクファクトリが渡すワーカローカルの <see cref="FrontierKernelState"/> に保持されるため、
    /// カーネルのフィールドは並行で変更されない。
    /// </summary>
    private sealed class ParallelKernel(int maxDepth) : IGraphKernel<FrontierKernelState>
    {
        public void Initialize(NodeId source, ref FrontierKernelState s)
        {
            s.Frontier ??= new Queue<(NodeId, int)>();
            s.Frontier.Clear();
            s.Visited ??= new HashSet<long>();
            s.Visited.Clear();
            s.Visited.Add(source.Sequence); // 内部 dedup は slot 同一性 (Sequence) で判定する
            s.Frontier.Enqueue((source, 0));
        }

        public bool VisitNeighbor(
            NodeId source, NodeId target, RelationshipId rel,
            long weightRaw, int depth, ref FrontierKernelState s)
        {
            if (s.Visited!.Add(target.Sequence))
                s.Frontier!.Enqueue((target, depth + 1));
            return true;
        }

        public bool ShouldContinue(int depth, in FrontierKernelState s) => depth < maxDepth;
    }
}
