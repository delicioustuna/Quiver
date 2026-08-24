using System.Text;
using System.Runtime.InteropServices;
using Yatagarasu.Core;
using Yatagarasu.Transactions;

namespace Yatagarasu.Storage.Records;

/// <summary>
/// Nexus の型と role 付き member 集合から primary candidate を引く process-local derived index。
/// candidate は snapshot-scoped primary header/incidence で再検証してから利用する。
/// </summary>
internal sealed class NexusMergeIndex
{
    private readonly Dictionary<NexusMergeKey, List<NexusId>> _candidates = [];
    private bool _initialized;

    internal bool TryFind(
        ITransaction transaction,
        NexusTypeId type,
        ReadOnlySpan<IncidenceMember> members,
        out NexusId nexus)
    {
        EnsureInitialized(transaction);
        NexusMergeKey key = CreateKey(type, members);
        if (_candidates.TryGetValue(key, out var candidates))
        {
            foreach (NexusId candidate in candidates)
            {
                using var header = transaction.Nexuses.Read(candidate);
                if (!header.InUse || header.Type != type)
                    continue;
                if (TryCreateKey(transaction, candidate, header.Type, out var stored)
                    && stored == key)
                {
                    nexus = header.Id;
                    return true;
                }
            }
        }

        nexus = default;
        return false;
    }

    internal void Add(
        NexusId nexus,
        NexusTypeId type,
        ReadOnlySpan<IncidenceMember> members)
    {
        NexusMergeKey key = CreateKey(type, members);
        if (!_candidates.TryGetValue(key, out var candidates))
            _candidates[key] = candidates = [];
        candidates.Add(nexus);
    }

    private void EnsureInitialized(ITransaction transaction)
    {
        if (_initialized)
            return;
        _initialized = true;
        foreach (NexusId nexus in transaction.Nexuses.Scan())
        {
            using var header = transaction.Nexuses.Read(nexus);
            if (!header.InUse
                || !TryCreateKey(transaction, header.Id, header.Type, out var key))
                continue;
            if (!_candidates.TryGetValue(key, out var candidates))
                _candidates[key] = candidates = [];
            candidates.Add(header.Id);
        }
    }

    private static bool TryCreateKey(
        ITransaction transaction,
        NexusId nexus,
        NexusTypeId type,
        out NexusMergeKey key)
    {
        var members = new List<IncidenceMember>();
        var incidences = transaction.Incidences.EnumerateByNexus(
            nexus,
            transaction.Nexuses);
        while (incidences.MoveNext())
        {
            IncidenceReadHandle incidence = incidences.Current;
            members.Add(new IncidenceMember(
                incidence.VertexId,
                incidence.RoleId));
        }
        if (members.Count < 2)
        {
            key = default;
            return false;
        }
        key = CreateKey(type, CollectionsMarshal.AsSpan(members));
        return true;
    }

    private static NexusMergeKey CreateKey(
        NexusTypeId type,
        ReadOnlySpan<IncidenceMember> members)
    {
        var canonical = new (int Role, long Vertex)[members.Length];
        for (int i = 0; i < members.Length; i++)
            canonical[i] = (members[i].RoleId.Value, members[i].VertexId.Sequence);
        Array.Sort(canonical, static (left, right) =>
        {
            int role = left.Role.CompareTo(right.Role);
            return role != 0 ? role : left.Vertex.CompareTo(right.Vertex);
        });

        var builder = new StringBuilder();
        builder.Append(type.Value).Append('|');
        foreach (var member in canonical)
            builder.Append(member.Role).Append(':').Append(member.Vertex).Append(';');
        return new NexusMergeKey(builder.ToString());
    }

    private readonly record struct NexusMergeKey(string Canonical);
}
