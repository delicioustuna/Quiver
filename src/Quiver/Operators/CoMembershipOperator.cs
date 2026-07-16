using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 明示設定されたロール対では co-membership block を直接走査し、
/// ビューが無い場合は通常の incidence 展開へフォールバックする。
/// </summary>
internal sealed class CoMembershipOperator : IPhysicalOperator
{
    private const int BaseColumnCount = 2;

    private readonly IPhysicalOperator _source;
    private readonly IPhysicalOperator _fallback;
    private readonly int _sourceVertexColumn;
    private readonly NexusTypeId? _typeFilter;
    private readonly RoleId _originRole;
    private readonly RoleId _memberRole;
    private readonly int[]? _firstCarry;
    private readonly int[]? _finalCarry;
    private readonly TupleSlot[] _buffer;

    private ITransaction? _tx;
    private ICoMembershipBlockStore? _view;
    private CoMembershipEntry[] _entries = [];
    private int _entryCount;
    private int _entryIndex;
    private VertexId _originVertex;
    private OperatorStatistics _statistics;

    internal CoMembershipOperator(
        IPhysicalOperator source,
        IPhysicalOperator fallback,
        int sourceVertexColumn,
        NexusTypeId? typeFilter,
        RoleId originRole,
        RoleId memberRole,
        int[]? firstCarry,
        int[]? finalCarry)
    {
        _source = source;
        _fallback = fallback;
        _sourceVertexColumn = sourceVertexColumn;
        _typeFilter = typeFilter;
        _originRole = originRole;
        _memberRole = memberRole;
        _firstCarry = firstCarry;
        _finalCarry = finalCarry;
        _buffer = new TupleSlot[fallback.Schema.Columns.Count];
    }

    public TupleSchema Schema => _fallback.Schema;
    public OperatorStatistics Statistics
        => _view is null ? _fallback.Statistics : _statistics;
    public TupleRef Current => _view is null ? _fallback.Current : new TupleRef(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _view = tx.CoMembershipBlocks is { } view
            && view.Contains(_originRole, _memberRole)
            ? view
            : null;
        if (_view is null)
        {
            _fallback.Open(tx);
            return;
        }

        _source.Open(tx);
        _entries = [];
        _entryCount = 0;
        _entryIndex = 0;
        _originVertex = VertexId.Invalid;
    }

    public bool MoveNext()
    {
        if (_view is null)
            return _fallback.MoveNext();

        while (true)
        {
            while (_entryIndex < _entryCount)
            {
                CoMembershipEntry entry = _entries[_entryIndex++];
                using var header = _tx!.Nexuses.Read(entry.NexusId);
                if (!header.InUse || header.Id != entry.NexusId)
                    continue;
                if (_typeFilter.HasValue && header.Type != _typeFilter.Value)
                    continue;

                BuildOutput(entry);
                var statistics = _statistics;
                statistics.RowsProduced++;
                _statistics = statistics;
                return true;
            }

            if (!_source.MoveNext())
                return false;

            _originVertex = new VertexId(_source.Current[_sourceVertexColumn].LongValue);
            _entries = _view.GetEntries(
                _originVertex, _originRole, _memberRole, out _entryCount);
            _entryIndex = 0;
        }
    }

    private void BuildOutput(CoMembershipEntry entry)
    {
        _buffer[0] = new TupleSlot
        {
            Type = TupleSlotType.NexusId,
            LongValue = entry.NexusId.Value,
        };
        _buffer[1] = new TupleSlot
        {
            Type = TupleSlotType.VertexId,
            LongValue = entry.MemberVertexId.Value,
        };

        if (_finalCarry is null)
            return;
        for (int i = 0; i < _finalCarry.Length; i++)
            _buffer[BaseColumnCount + i] = ResolveIntermediateSlot(_finalCarry[i], entry);
    }

    private TupleSlot ResolveIntermediateSlot(int column, CoMembershipEntry entry)
    {
        if (column == 0)
        {
            return new TupleSlot
            {
                Type = TupleSlotType.VertexId,
                LongValue = _originVertex.Value,
            };
        }
        if (column == 1)
        {
            return new TupleSlot
            {
                Type = TupleSlotType.NexusId,
                LongValue = entry.NexusId.Value,
            };
        }

        int sourceColumn = _firstCarry![column - BaseColumnCount];
        return _source.Current[sourceColumn];
    }

    public ReadOnlySpan<byte> GetBytes(int column)
    {
        if (_view is null)
            return _fallback.GetBytes(column);
        if (_finalCarry is null || column < BaseColumnCount)
            return ReadOnlySpan<byte>.Empty;

        int intermediateColumn = _finalCarry[column - BaseColumnCount];
        if (intermediateColumn < BaseColumnCount)
            return ReadOnlySpan<byte>.Empty;
        return _source.GetBytes(_firstCarry![intermediateColumn - BaseColumnCount]);
    }

    public void Dispose()
    {
        _source.Dispose();
        _fallback.Dispose();
    }
}
