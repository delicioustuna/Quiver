using Quiver.Core;
using System.Runtime.InteropServices;

namespace Quiver.Storage.Records;

/// <summary>
/// 明示されたロール対だけを物理化する co-membership block。
/// ノードとロール対ごとの配列に、ハイパーエッジと到達先ノードを連続配置する。
/// </summary>
// header と incidence が正本であり、このビューは起動時・vacuum 後に全再構築できる。
// transaction 中の追加は即時反映するが、abort された entry は header の可視性判定で
// 読み飛ばす。HyperedgeId は世代を含むため slot 再利用後の別 entity とも一致しない。
internal sealed class CoMembershipBlockStore : ICoMembershipBlockStore
{
    private readonly object _gate = new();
    private readonly HashSet<RolePair> _pairs;
    private Dictionary<BlockKey, Block> _blocks = [];
    private long _readCount;
    private bool _usable = true;

    internal CoMembershipBlockStore(IEnumerable<(RoleId OriginRole, RoleId MemberRole)> pairs)
    {
        _pairs = pairs
            .Select(static pair => new RolePair(pair.OriginRole, pair.MemberRole))
            .ToHashSet();
    }

    public bool Contains(RoleId originRole, RoleId memberRole)
        => Volatile.Read(ref _usable)
            && _pairs.Contains(new RolePair(originRole, memberRole));

    public CoMembershipEntry[] GetEntries(
        NodeId originNode,
        RoleId originRole,
        RoleId memberRole,
        out int count)
    {
        Interlocked.Increment(ref _readCount);
        lock (_gate)
        {
            if (_blocks.TryGetValue(
                    new BlockKey(originNode.Sequence, originRole.Value, memberRole.Value),
                    out var block))
            {
                count = block.Count;
                return block.Entries;
            }
        }

        count = 0;
        return [];
    }

    internal long ReadCount => Interlocked.Read(ref _readCount);

    public void Add(HyperedgeId hyperedgeId, ReadOnlySpan<IncidenceMember> members)
    {
        lock (_gate)
            AddCore(_blocks, hyperedgeId, members);
    }

    public void Rebuild(IHyperedgeStore hyperedges, IIncidenceStore incidences)
    {
        var replacement = new Dictionary<BlockKey, Block>();
        foreach (HyperedgeId hyperedgeId in hyperedges.Scan())
        {
            using var header = hyperedges.Read(hyperedgeId);
            if (!header.InUse)
                continue;

            var members = new List<IncidenceMember>();
            var enumerator = incidences.EnumerateByHyperedge(hyperedgeId, hyperedges);
            while (enumerator.MoveNext())
            {
                var incidence = enumerator.Current;
                members.Add(new IncidenceMember(incidence.NodeId, incidence.RoleId));
            }

            AddCore(replacement, hyperedgeId, CollectionsMarshal.AsSpan(members));
        }

        lock (_gate)
        {
            _blocks = replacement;
            Volatile.Write(ref _usable, true);
        }
    }

    public void Invalidate() => Volatile.Write(ref _usable, false);

    private void AddCore(
        Dictionary<BlockKey, Block> blocks,
        HyperedgeId hyperedgeId,
        ReadOnlySpan<IncidenceMember> members)
    {
        foreach (RolePair pair in _pairs)
        {
            for (int originIndex = 0; originIndex < members.Length; originIndex++)
            {
                IncidenceMember origin = members[originIndex];
                if (origin.RoleId != pair.OriginRole)
                    continue;

                for (int memberIndex = 0; memberIndex < members.Length; memberIndex++)
                {
                    IncidenceMember member = members[memberIndex];
                    if (member.RoleId != pair.MemberRole || member.NodeId == origin.NodeId)
                        continue;

                    var key = new BlockKey(
                        origin.NodeId.Sequence,
                        pair.OriginRole.Value,
                        pair.MemberRole.Value);
                    if (!blocks.TryGetValue(key, out Block? block))
                    {
                        block = new Block();
                        blocks.Add(key, block);
                    }
                    block.Add(new CoMembershipEntry(hyperedgeId, member.NodeId));
                }
            }
        }
    }

    private readonly record struct RolePair(RoleId OriginRole, RoleId MemberRole);
    private readonly record struct BlockKey(long NodeSequence, int OriginRole, int MemberRole);

    private sealed class Block
    {
        internal CoMembershipEntry[] Entries = new CoMembershipEntry[4];
        internal int Count;

        internal void Add(CoMembershipEntry entry)
        {
            if (Count == Entries.Length)
                Array.Resize(ref Entries, checked(Entries.Length * 2));
            Entries[Count++] = entry;
        }
    }
}
