using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// 上流 nexus の member vertex を放出する。
/// 出力は (nexus, member) と任意の carry 列。
/// </summary>
internal sealed class ExpandMembersOperator : IPhysicalOperator
{
    private const int BaseColumnCount = 2;

    private readonly IPhysicalOperator _source;
    private readonly int _nexusColumn;
    private readonly RoleId? _roleFilter;
    private readonly int? _excludeVertexColumn;
    private readonly int[]? _carryColumns;
    private readonly TupleSlot[] _buffer;
    private readonly TupleSchema _schema;

    private ITransaction? _tx;
    private NexusId _currentNexus;
    private VertexId _excludedVertex;
    private IncidenceId _nextIncidence;

    public ExpandMembersOperator(
        IPhysicalOperator source,
        int nexusColumn,
        RoleId? roleFilter,
        int? excludeVertexColumn,
        int[]? carryColumns = null)
    {
        _source = source;
        _nexusColumn = nexusColumn;
        _roleFilter = roleFilter;
        _excludeVertexColumn = excludeVertexColumn;
        _carryColumns = carryColumns is { Length: > 0 } ? carryColumns : null;
        _buffer = new TupleSlot[BaseColumnCount + (_carryColumns?.Length ?? 0)];

        var columns = new List<ColumnDefinition>(_buffer.Length)
        {
            new("nexus", TupleSlotType.NexusId),
            new("member", TupleSlotType.VertexId),
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
        _currentNexus = NexusId.Invalid;
        _excludedVertex = VertexId.Invalid;
        _nextIncidence = IncidenceId.Invalid;
    }

    public TupleSchema Schema => _schema;
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        _tx = tx;
        _source.Open(tx);
        _currentNexus = NexusId.Invalid;
        _excludedVertex = VertexId.Invalid;
        _nextIncidence = IncidenceId.Invalid;
    }

    public bool MoveNext()
    {
        while (true)
        {
            while (_nextIncidence.IsValid)
            {
                using var incidence = _tx!.Incidences.Read(_nextIncidence);
                _nextIncidence = incidence.NextInNexus;
                if (!incidence.InUse)
                    continue;
                if (_roleFilter.HasValue && incidence.RoleId != _roleFilter.Value)
                    continue;
                int generation = _tx!.Vertices.CurrentGeneration(incidence.VertexId.Sequence);
                if (generation < 0)
                    continue;
                var member = VertexId.Create(incidence.VertexId.Sequence, generation);
                if (_excludedVertex.IsValid && member == _excludedVertex)
                    continue;

                BuildOutput(member);
                var statistics = Statistics;
                statistics.RowsProduced++;
                Statistics = statistics;
                return true;
            }

            if (!_source.MoveNext())
                return false;

            var requested = new NexusId(_source.Current[_nexusColumn].LongValue);
            using var header = _tx!.Nexuses.Read(requested);
            if (!header.InUse)
                continue;

            _currentNexus = header.Id;
            if (_excludeVertexColumn.HasValue)
            {
                using var excluded = _tx.Vertices.Read(
                    new VertexId(_source.Current[_excludeVertexColumn.Value].LongValue));
                if (!excluded.InUse)
                    continue;
                _excludedVertex = excluded.Id;
            }
            if (!_excludeVertexColumn.HasValue)
                _excludedVertex = VertexId.Invalid;
            _nextIncidence = header.FirstIncidenceId;
        }
    }

    private void BuildOutput(VertexId member)
    {
        _buffer[0] = new TupleSlot
        {
            Type = TupleSlotType.NexusId,
            LongValue = _currentNexus.Value,
        };
        _buffer[1] = new TupleSlot
        {
            Type = TupleSlotType.VertexId,
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
