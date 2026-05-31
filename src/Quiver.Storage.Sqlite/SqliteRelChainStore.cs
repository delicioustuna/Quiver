using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Storage.Sqlite;

/// <summary>
/// In-memory <see cref="IRelationshipStore"/> shim used solely to construct
/// <see cref="RelationshipEnumerator"/> ref structs (which require an
/// <see cref="IRelationshipStore"/> for their internal <c>Read</c> callback).
///
/// <para>
/// At <c>EnumerateRelationships</c> time we fetch every incident edge for the
/// scan node, sort by id, and synthesise per-edge <c>SourceNext</c> /
/// <c>TargetNext</c> pointers so the enumerator walks the rows in order.
/// </para>
/// <para>
/// SQLite has no native linked-list layout — this shim is the bridge from a
/// SELECT result set to the ref-struct-shaped iteration contract.
/// </para>
/// </summary>
internal sealed class SqliteRelChainStore : IRelationshipStore
{
    private readonly Dictionary<long, RelEntry> _byId;
    private readonly NodeId _scanNode;

    internal SqliteRelChainStore(NodeId scanNode, List<RelRow> ordered)
    {
        _scanNode = scanNode;
        _byId = new Dictionary<long, RelEntry>(ordered.Count);

        for (int i = 0; i < ordered.Count; i++)
        {
            var cur = ordered[i];
            var nextId = i + 1 < ordered.Count
                ? new RelationshipId(ordered[i + 1].Id)
                : RelationshipId.Invalid;

            // Encode nextId on the side the enumerator will read for this scan node:
            //   if scan node is the source side, the enumerator follows SourceNext;
            //   otherwise it follows TargetNext.
            var srcNext = RelationshipId.Invalid;
            var tgtNext = RelationshipId.Invalid;
            if (cur.Source == scanNode.Value) srcNext = nextId;
            else                              tgtNext = nextId;

            _byId[cur.Id] = new RelEntry(cur, srcNext, tgtNext);
        }
    }

    internal RelationshipId FirstId => _byId.Count == 0
        ? RelationshipId.Invalid
        : new RelationshipId(_byId.Keys.Min());

    public RelationshipReadHandle Read(RelationshipId relId)
    {
        if (!_byId.TryGetValue(relId.Value, out var entry))
        {
            return new RelationshipReadHandle(
                relId, inUse: false,
                source: NodeId.Invalid, target: NodeId.Invalid,
                type: default,
                srcPrev: RelationshipId.Invalid, srcNext: RelationshipId.Invalid,
                tgtPrev: RelationshipId.Invalid, tgtNext: RelationshipId.Invalid,
                firstPropId: PropertyId.Invalid);
        }

        return new RelationshipReadHandle(
            id: relId,
            inUse: true,
            source: new NodeId(entry.Row.Source),
            target: new NodeId(entry.Row.Target),
            type: new RelationshipTypeId(entry.Row.TypeId),
            srcPrev: RelationshipId.Invalid,
            srcNext: entry.SourceNext,
            tgtPrev: RelationshipId.Invalid,
            tgtNext: entry.TargetNext,
            firstPropId: PropertyId.Invalid);
    }

    // ---- IRelationshipStore members we don't service (never called on the
    //      enumeration path; throw so any accidental wiring is loud) ----

    public RelationshipId Create(INodeStore _, NodeId __, NodeId ___, RelationshipTypeId ____)
        => throw new NotSupportedException("Use SqliteGraphTransaction.CreateRelationship.");

    public void Delete(INodeStore _, RelationshipId __)
        => throw new NotSupportedException("Use SqliteGraphTransaction.DeleteRelationship.");

    public RelationshipWriteHandle Write(RelationshipId _)
        => throw new NotSupportedException("Binary write handles are not available on the SQLite backend.");

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore _)
        => new(this, nodeId, FirstId);

    public RelationshipEnumerator EnumerateNeighbors(NodeId nodeId, INodeStore _, RelationshipTypeId type, Direction direction)
        => new(this, nodeId, FirstId, type, direction);

    public long InUseCount => _byId.Count;

    public IEnumerable<RelationshipId> Scan()
        => _byId.Keys.OrderBy(k => k).Select(k => new RelationshipId(k));

    internal readonly record struct RelRow(long Id, long Source, long Target, int TypeId);
    private readonly record struct RelEntry(RelRow Row, RelationshipId SourceNext, RelationshipId TargetNext);
}
