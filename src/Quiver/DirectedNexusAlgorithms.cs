using Quiver.Core;

namespace Quiver;

/// <summary>有向Nexusの探索が終了した理由。</summary>
internal enum DirectedNexusTerminationReason
{
    /// <summary>探索空間を完走した。</summary>
    Completed,
    /// <summary>返却するVertex数の上限に達した。</summary>
    MaxResultsReached,
    /// <summary>走査するNexus数の上限に達した。</summary>
    MaxNexusesReached,
    /// <summary>復元する導出木のノード数上限に達した。</summary>
    MaxTreeNodesReached,
}

/// <summary>最短導出でtailコストを集約する方式。</summary>
internal enum DerivationCostMode
{
    /// <summary>Nexusコストと全tailの導出コストを加算する。</summary>
    Additive,
    /// <summary>Nexusコストと全tailの導出コストの最大値を取る。</summary>
    Bottleneck,
}

/// <summary>Nexusの非負有限コストを返すデリゲート。</summary>
/// <param name="transaction">探索と同じsnapshotに束縛された読み取りトランザクション。</param>
/// <param name="nexusId">コストを取得するNexus。</param>
/// <returns>0以上の有限値。</returns>
internal delegate double NexusCostSelector(IReadTransaction transaction, NexusId nexusId);

/// <summary>有向Nexus到達探索の実行上限。</summary>
internal sealed class DirectedNexusReachabilityOptions
{
    /// <summary>返却するVertexの最大数。seedもこの数に含む。</summary>
    public int MaxResults { get; init; } = int.MaxValue;

    /// <summary>初期化して走査するNexusの最大数。</summary>
    public int MaxNexuses { get; init; } = int.MaxValue;

    /// <summary>探索のキャンセル。キャンセル時は <see cref="OperationCanceledException"/> を送出する。</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>最短導出探索と導出木復元の実行上限。</summary>
internal sealed class ShortestDerivationOptions
{
    /// <summary>初期化して走査するNexusの最大数。</summary>
    public int MaxNexuses { get; init; } = int.MaxValue;

    /// <summary>復元する導出木の最大ノード数。共有された導出も出現ごとに数える。</summary>
    public int MaxTreeNodes { get; init; } = 1_000_000;

    /// <summary>探索のキャンセル。キャンセル時は <see cref="OperationCanceledException"/> を送出する。</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>有向Nexus到達探索の結果。</summary>
internal sealed class DirectedNexusReachabilityResult
{
    internal DirectedNexusReachabilityResult(
        IReadOnlyList<VertexId> vertices,
        DirectedNexusTerminationReason terminationReason,
        int visitedNexusCount)
    {
        Vertices = vertices;
        TerminationReason = terminationReason;
        VisitedNexusCount = visitedNexusCount;
    }

    /// <summary>seedを含む到達Vertex。探索で確定した順に並ぶ。</summary>
    public IReadOnlyList<VertexId> Vertices { get; }

    /// <summary>上限で打ち切らず探索空間を完走したか。</summary>
    public bool IsComplete => TerminationReason == DirectedNexusTerminationReason.Completed;

    /// <summary>探索が終了した理由。</summary>
    public DirectedNexusTerminationReason TerminationReason { get; }

    /// <summary>初期化して走査したNexus数。</summary>
    public int VisitedNexusCount { get; }
}

/// <summary>導出木における1回のVertex出現。</summary>
/// <param name="VertexId">この出現が表すVertex。</param>
/// <param name="NexusId">このVertexを導出したNexus。seedでは <see cref="Core.NexusId.Invalid"/>。</param>
/// <param name="ParentIndex">親ノードのindex。rootでは -1。</param>
internal readonly record struct DerivationTreeNode(VertexId VertexId, NexusId NexusId, int ParentIndex);

/// <summary>最短導出探索の結果。</summary>
internal sealed class ShortestDerivationResult
{
    internal ShortestDerivationResult(
        bool isReachable,
        double? cost,
        IReadOnlyList<DerivationTreeNode> tree,
        DirectedNexusTerminationReason terminationReason,
        int visitedNexusCount)
    {
        IsReachable = isReachable;
        Cost = cost;
        Tree = tree;
        TerminationReason = terminationReason;
        VisitedNexusCount = visitedNexusCount;
    }

    /// <summary>対象Vertexへの導出が見つかったか。</summary>
    public bool IsReachable { get; }

    /// <summary>最小コスト。到達不能または探索打ち切りで未確定なら <c>null</c>。</summary>
    public double? Cost { get; }

    /// <summary>
    /// rootをindex 0とするpreorderの導出木。各非seedノードの子は、そのノードの
    /// <see cref="DerivationTreeNode.NexusId"/> に属する全tailであり、共有は出現ごとに含む。
    /// 復元上限に達した場合は空になる。
    /// </summary>
    public IReadOnlyList<DerivationTreeNode> Tree { get; }

    /// <summary>上限で打ち切らず探索と必要な復元を完了したか。</summary>
    public bool IsComplete => TerminationReason == DirectedNexusTerminationReason.Completed;

    /// <summary>探索が終了した理由。</summary>
    public DirectedNexusTerminationReason TerminationReason { get; }

    /// <summary>初期化して走査したNexus数。</summary>
    public int VisitedNexusCount { get; }
}

/// <summary>tail/headロールで方向付けたNexusを探索するアルゴリズム。</summary>
internal static class DirectedNexusAlgorithms
{
    /// <summary>
    /// seedから、全tailが到達したNexusの全headを発火するAND到達を列挙する。
    /// 同じsnapshotの可視Nexusとmemberだけを読み、永続状態は変更しない。
    /// 時間計算量は触れたNexusのmember総数に対して線形、作業領域は到達Vertex数と触れたNexus数に比例する。
    /// </summary>
    public static DirectedNexusReachabilityResult FindReachableVertices(
        this IReadTransaction transaction,
        IReadOnlyList<VertexId> seeds,
        string nexusType,
        string tailRole,
        string headRole,
        DirectedNexusReachabilityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(seeds);
        ValidateNames(nexusType, tailRole, headRole);
        options ??= new DirectedNexusReachabilityOptions();
        if (options.MaxResults <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxResultsは正でなければなりません。");
        if (options.MaxNexuses <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxNexusesは正でなければなりません。");

        var reached = new HashSet<VertexId>();
        var vertices = new List<VertexId>(Math.Min(options.MaxResults, 256));
        var queue = new Queue<VertexId>();
        foreach (VertexId seed in seeds)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (!transaction.VertexExists(seed)) throw new ArgumentException($"seed Vertex {seed.Value} はsnapshotに存在しません。", nameof(seeds));
            if (!reached.Add(seed)) continue;
            if (vertices.Count == options.MaxResults)
                return new(vertices.ToArray(), DirectedNexusTerminationReason.MaxResultsReached, 0);
            vertices.Add(seed);
            queue.Enqueue(seed);
        }

        var states = new Dictionary<NexusId, ReachState>();
        while (queue.TryDequeue(out VertexId vertex))
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var nexuses = transaction.GetNexuses(vertex, nexusType, tailRole);
            try
            {
                while (nexuses.MoveNext())
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    NexusId nexus = nexuses.Current;
                    if (!states.TryGetValue(nexus, out ReachState? state))
                    {
                        if (states.Count == options.MaxNexuses)
                            return new(vertices.ToArray(), DirectedNexusTerminationReason.MaxNexusesReached, states.Count);
                        state = ReadReachState(transaction, nexus, tailRole, headRole);
                        states.Add(nexus, state);
                    }
                    if (--state.Remaining != 0) continue;
                    foreach (VertexId head in state.Heads)
                    {
                        if (!reached.Add(head)) continue;
                        if (vertices.Count == options.MaxResults)
                            return new(vertices.ToArray(), DirectedNexusTerminationReason.MaxResultsReached, states.Count);
                        vertices.Add(head);
                        queue.Enqueue(head);
                    }
                }
            }
            finally
            {
                nexuses.Dispose();
            }
        }
        return new(vertices.ToArray(), DirectedNexusTerminationReason.Completed, states.Count);
    }

    /// <summary>
    /// 非負有限Nexusコストについて、対象Vertexへの最小B-導出木を求める。
    /// 加法では木に現れる共有導出を出現ごとに加算し、ボトルネックでは木全体の最大コストを返す。
    /// 一般の最短hyperpathではなく、全tailを必要とするB-導出と単調な2集約だけを扱う。
    /// 時間計算量は触れたmember総数と優先度queue操作、復元は返す木の大きさに比例する。
    /// 同コスト候補はVertex確定前なら小さいNexus IDを優先し、確定済みVertexの導出は変更しない。
    /// 対象Vertexを正しい最小コストで確定した時点で復元し、その先のNexusは走査しない。
    /// </summary>
    public static ShortestDerivationResult FindShortestDerivation(
        this IReadTransaction transaction,
        IReadOnlyList<VertexId> seeds,
        VertexId target,
        string nexusType,
        string tailRole,
        string headRole,
        NexusCostSelector costSelector,
        DerivationCostMode costMode,
        ShortestDerivationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(costSelector);
        ValidateNames(nexusType, tailRole, headRole);
        if (!Enum.IsDefined(costMode)) throw new ArgumentOutOfRangeException(nameof(costMode));
        options ??= new ShortestDerivationOptions();
        if (options.MaxNexuses <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxNexusesは正でなければなりません。");
        if (options.MaxTreeNodes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxTreeNodesは正でなければなりません。");
        if (!transaction.VertexExists(target)) throw new ArgumentException($"target Vertex {target.Value} はsnapshotに存在しません。", nameof(target));

        var seedSet = new HashSet<VertexId>();
        var distances = new Dictionary<VertexId, double>();
        var predecessors = new Dictionary<VertexId, NexusId>();
        var finalized = new HashSet<VertexId>();
        var queue = new PriorityQueue<VertexId, (double Cost, long Vertex)>();
        foreach (VertexId seed in seeds)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (!transaction.VertexExists(seed)) throw new ArgumentException($"seed Vertex {seed.Value} はsnapshotに存在しません。", nameof(seeds));
            if (!seedSet.Add(seed)) continue;
            distances[seed] = 0;
            queue.Enqueue(seed, (0, seed.Value));
        }

        var states = new Dictionary<NexusId, CostState>();
        while (queue.TryDequeue(out VertexId vertex, out (double Cost, long Vertex) priority))
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (finalized.Contains(vertex) || !distances.TryGetValue(vertex, out double known) || priority.Cost != known) continue;
            finalized.Add(vertex);
            if (vertex == target)
            {
                if (!TryBuildTree(target, seedSet, predecessors, states, options, out DerivationTreeNode[] targetTree))
                    return new(true, known, [], DirectedNexusTerminationReason.MaxTreeNodesReached, states.Count);
                return new(true, known, targetTree, DirectedNexusTerminationReason.Completed, states.Count);
            }
            var nexuses = transaction.GetNexuses(vertex, nexusType, tailRole);
            try
            {
                while (nexuses.MoveNext())
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    NexusId nexus = nexuses.Current;
                    if (!states.TryGetValue(nexus, out CostState? state))
                    {
                        if (states.Count == options.MaxNexuses)
                            return new(false, null, [], DirectedNexusTerminationReason.MaxNexusesReached, states.Count);
                        double cost = costSelector(transaction, nexus);
                        if (!double.IsFinite(cost) || cost < 0)
                            throw new ArgumentOutOfRangeException(nameof(costSelector), $"Nexus {nexus.Value} のコストは0以上の有限値でなければなりません。");
                        state = ReadCostState(transaction, nexus, tailRole, headRole, cost);
                        states.Add(nexus, state);
                    }
                    state.Aggregate = costMode == DerivationCostMode.Additive
                        ? state.Aggregate + priority.Cost
                        : Math.Max(state.Aggregate, priority.Cost);
                    if (--state.Remaining != 0) continue;
                    double candidate = costMode == DerivationCostMode.Additive
                        ? state.Aggregate + state.Cost
                        : Math.Max(state.Aggregate, state.Cost);
                    if (!double.IsFinite(candidate))
                        throw new OverflowException($"Nexus {nexus.Value} の導出コストが有限範囲を超えました。");
                    foreach (VertexId head in state.Heads)
                    {
                        if (finalized.Contains(head)) continue;
                        if (seedSet.Contains(head)) continue;
                        bool improve = !distances.TryGetValue(head, out double old) || candidate < old;
                        bool tie = !improve && candidate == old
                            && (!predecessors.TryGetValue(head, out NexusId previous) || nexus.Value < previous.Value);
                        if (!improve && !tie) continue;
                        distances[head] = candidate;
                        predecessors[head] = nexus;
                        queue.Enqueue(head, (candidate, head.Value));
                    }
                }
            }
            finally
            {
                nexuses.Dispose();
            }
        }

        return new(false, null, [], DirectedNexusTerminationReason.Completed, states.Count);
    }

    private static bool TryBuildTree(
        VertexId target,
        HashSet<VertexId> seeds,
        Dictionary<VertexId, NexusId> predecessors,
        Dictionary<NexusId, CostState> states,
        ShortestDerivationOptions options,
        out DerivationTreeNode[] tree)
    {
        var nodes = new List<DerivationTreeNode>();
        var pending = new Stack<(VertexId Vertex, int Parent)>();
        pending.Push((target, -1));
        while (pending.TryPop(out var item))
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (nodes.Count == options.MaxTreeNodes)
            {
                tree = [];
                return false;
            }
            NexusId nexus = seeds.Contains(item.Vertex) ? NexusId.Invalid : predecessors[item.Vertex];
            int index = nodes.Count;
            nodes.Add(new(item.Vertex, nexus, item.Parent));
            if (nexus == NexusId.Invalid) continue;
            VertexId[] tails = states[nexus].Tails;
            for (int i = tails.Length - 1; i >= 0; i--) pending.Push((tails[i], index));
        }
        tree = nodes.ToArray();
        return true;
    }

    private static ReachState ReadReachState(IReadTransaction transaction, NexusId nexus, string tailRole, string headRole)
    {
        var tails = new List<VertexId>();
        var heads = new List<VertexId>();
        var members = transaction.GetMembers(nexus);
        try
        {
            while (members.MoveNext())
            {
                NexusMember member = members.Current;
                if (member.Role == tailRole) tails.Add(member.VertexId);
                if (member.Role == headRole) heads.Add(member.VertexId);
            }
        }
        finally
        {
            members.Dispose();
        }
        return new ReachState(tails.Count, heads.ToArray());
    }

    private static CostState ReadCostState(IReadTransaction transaction, NexusId nexus, string tailRole, string headRole, double cost)
    {
        var tails = new List<VertexId>();
        var heads = new List<VertexId>();
        var members = transaction.GetMembers(nexus);
        try
        {
            while (members.MoveNext())
            {
                NexusMember member = members.Current;
                if (member.Role == tailRole) tails.Add(member.VertexId);
                if (member.Role == headRole) heads.Add(member.VertexId);
            }
        }
        finally
        {
            members.Dispose();
        }
        return new CostState(tails.ToArray(), heads.ToArray(), cost);
    }

    private static void ValidateNames(string nexusType, string tailRole, string headRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nexusType);
        ArgumentException.ThrowIfNullOrWhiteSpace(tailRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(headRole);
        if (tailRole == headRole) throw new ArgumentException("tailRoleとheadRoleは異なる必要があります。", nameof(headRole));
    }

    private sealed class ReachState(int remaining, VertexId[] heads)
    {
        internal int Remaining = remaining;
        internal VertexId[] Heads { get; } = heads;
    }

    private sealed class CostState(VertexId[] tails, VertexId[] heads, double cost)
    {
        internal VertexId[] Tails { get; } = tails;
        internal VertexId[] Heads { get; } = heads;
        internal double Cost { get; } = cost;
        internal int Remaining = tails.Length;
        internal double Aggregate;
    }
}
