using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 上流 node が参加する可視 hyperedge を放出する。
/// 出力は (sourceNode, hyperedge) と任意の carry 列。
/// </summary>
internal sealed class ExpandToHyperedgeOperator : IPhysicalOperator
{
    private const int BaseColumnCount = 2;

    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly HyperedgeTypeId? _typeFilter;
    private readonly RoleId? _roleFilter;
    private readonly int[]? _carryColumns;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;

    private ITransaction? _tx;
    private NodeId _currentSourceNode;
    private IncidenceId _nextIncidence;

    public ExpandToHyperedgeOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        HyperedgeTypeId? typeFilter,
        RoleId? roleFilter,
        int[]? carryColumns = null)
    {
        _source = source;
        _sourceNodeColumn = sourceNodeColumn;
        _typeFilter = typeFilter;
        _roleFilter = roleFilter;
        _carryColumns = carryColumns is { Length: > 0 } ? carryColumns : null;
        _buffer = new TupleSlot[BaseColumnCount + (_carryColumns?.Length ?? 0)];

        var columns = new List<ColumnDefinition>(_buffer.Length)
        {
            new("source", TupleSlotType.NodeId),
            new("hyperedge", TupleSlotType.HyperedgeId),
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
        _currentSourceNode = NodeId.Invalid;
        _nextIncidence = IncidenceId.Invalid;
    }

    public TupleSchema Schema => _schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _currentSourceNode = NodeId.Invalid;
        _nextIncidence = IncidenceId.Invalid;
    }

    public bool MoveNext()
    {
        while (true)
        {
            while (_nextIncidence.IsValid)
            {
                using var incidence = _tx!.Incidences.Read(_nextIncidence);
                _nextIncidence = incidence.NextInNode;
                if (!incidence.InUse)
                    continue;
                if (_roleFilter.HasValue && incidence.RoleId != _roleFilter.Value)
                    continue;

                using var header = _tx.Hyperedges.Read(incidence.HyperedgeId);
                if (!header.InUse)
                    continue;
                if (_typeFilter.HasValue && header.Type != _typeFilter.Value)
                    continue;

                BuildOutput(header.Id);
                var statistics = Statistics;
                statistics.RowsProduced++;
                Statistics = statistics;
                return true;
            }

            if (!_source.MoveNext())
                return false;

            _currentSourceNode = new NodeId(_source.Current[_sourceNodeColumn].LongValue);
            _nextIncidence = _tx!.NodeIncidenceHeads.Get(_currentSourceNode);
        }
    }

    private void BuildOutput(HyperedgeId hyperedgeId)
    {
        _buffer[0] = new TupleSlot
        {
            Type = TupleSlotType.NodeId,
            LongValue = _currentSourceNode.Value,
        };
        _buffer[1] = new TupleSlot
        {
            Type = TupleSlotType.HyperedgeId,
            LongValue = hyperedgeId.Value,
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
