using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 上流 hyperedge の member node を放出する。
/// 出力は (hyperedge, member) と任意の carry 列。
/// </summary>
internal sealed class ExpandMembersOperator : IPhysicalOperator
{
    private const int BaseColumnCount = 2;

    private readonly IPhysicalOperator _source;
    private readonly int _hyperedgeColumn;
    private readonly RoleId? _roleFilter;
    private readonly int? _excludeNodeColumn;
    private readonly int[]? _carryColumns;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;

    private ITransaction? _tx;
    private HyperedgeId _currentHyperedge;
    private NodeId _excludedNode;
    private IncidenceId _nextIncidence;

    public ExpandMembersOperator(
        IPhysicalOperator source,
        int hyperedgeColumn,
        RoleId? roleFilter,
        int? excludeNodeColumn,
        int[]? carryColumns = null)
    {
        _source = source;
        _hyperedgeColumn = hyperedgeColumn;
        _roleFilter = roleFilter;
        _excludeNodeColumn = excludeNodeColumn;
        _carryColumns = carryColumns is { Length: > 0 } ? carryColumns : null;
        _buffer = new TupleSlot[BaseColumnCount + (_carryColumns?.Length ?? 0)];

        var columns = new List<ColumnDefinition>(_buffer.Length)
        {
            new("hyperedge", TupleSlotType.HyperedgeId),
            new("member", TupleSlotType.NodeId),
        };
        if (_carryColumns != null)
        {
            foreach (int column in _carryColumns)
            {
                ColumnDefinition definition = source.Schema.Columns[column];
                columns.Add(new ColumnDefinition(definition.Name, definition.Type));
            }
        }
        _schema = new TupleSchema(columns);
        _currentHyperedge = HyperedgeId.Invalid;
        _excludedNode = NodeId.Invalid;
        _nextIncidence = IncidenceId.Invalid;
    }

    public TupleSchema Schema => _schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _currentHyperedge = HyperedgeId.Invalid;
        _excludedNode = NodeId.Invalid;
        _nextIncidence = IncidenceId.Invalid;
    }

    public bool MoveNext()
    {
        while (true)
        {
            while (_nextIncidence.IsValid)
            {
                using var incidence = _tx!.Incidences.Read(_nextIncidence);
                _nextIncidence = incidence.NextInHyperedge;
                if (!incidence.InUse)
                    continue;
                if (_roleFilter.HasValue && incidence.RoleId != _roleFilter.Value)
                    continue;
                if (_excludedNode.IsValid && incidence.NodeId == _excludedNode)
                    continue;

                BuildOutput(incidence.NodeId);
                var statistics = Statistics;
                statistics.RowsProduced++;
                Statistics = statistics;
                return true;
            }

            if (!_source.MoveNext())
                return false;

            var requested = new HyperedgeId(_source.Current[_hyperedgeColumn].LongValue);
            using var header = _tx!.Hyperedges.Read(requested);
            if (!header.InUse)
                continue;

            _currentHyperedge = header.Id;
            _excludedNode = _excludeNodeColumn.HasValue
                ? new NodeId(_source.Current[_excludeNodeColumn.Value].LongValue)
                : NodeId.Invalid;
            _nextIncidence = header.FirstIncidenceId;
        }
    }

    private void BuildOutput(NodeId member)
    {
        _buffer[0] = new TupleSlot
        {
            Type = TupleSlotType.HyperedgeId,
            LongValue = _currentHyperedge.Value,
        };
        _buffer[1] = new TupleSlot
        {
            Type = TupleSlotType.NodeId,
            LongValue = member.Value,
        };
        CopyCarry();
    }

    private void CopyCarry()
    {
        if (_carryColumns == null) return;
        TupleRef input = _source.Current;
        for (int i = 0; i < _carryColumns.Length; i++)
            _buffer[BaseColumnCount + i] = input[_carryColumns[i]];
    }

    public ReadOnlySpan<byte> GetBytes(int column)
    {
        if (_carryColumns != null && column >= BaseColumnCount)
            return _source.GetBytes(_carryColumns[column - BaseColumnCount]);
        return ReadOnlySpan<byte>.Empty;
    }

    public void Dispose() => _source.Dispose();
}
