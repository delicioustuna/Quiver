using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;

namespace Quiver.Client.Internal;

internal sealed class ScanBuilder : IOperatorBuilder
{
    private readonly string? _label;
    public int CurrentEntityColumn => 0;
    public int PredictedOutputColumnCount => 1;

    internal ScanBuilder(string? label = null) { _label = label; }

    public IPhysicalOperator Build(ISchemaApi schema)
    {
        if (_label != null)
            return new NodeByLabelScanOperator(schema.GetOrCreateLabel(_label));
        return new AllNodesScanOperator();
    }
}

internal sealed class FilterBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _source;
    private readonly Func<ISchemaApi, IPredicate> _predicateFactory;
    public int CurrentEntityColumn => _source.CurrentEntityColumn;
    public int PredictedOutputColumnCount => _source.PredictedOutputColumnCount;

    internal FilterBuilder(IOperatorBuilder source, Func<ISchemaApi, IPredicate> factory)
    {
        _source = source; _predicateFactory = factory;
    }

    public IPhysicalOperator Build(ISchemaApi schema)
        => new FilterOperator(_source.Build(schema), _predicateFactory(schema));
}

internal sealed class ExpandBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _source;
    private readonly Direction _direction;
    private readonly string? _typeFilter;
    private readonly ExpandOutputMode _mode;

    public int CurrentEntityColumn => _mode switch
    {
        ExpandOutputMode.NeighborOnly    => 0,
        ExpandOutputMode.NeighborAndRel => 1,
        _ /* Full */                     => 2,
    };

    public int PredictedOutputColumnCount => _mode switch
    {
        ExpandOutputMode.NeighborOnly    => 1,
        ExpandOutputMode.NeighborAndRel => 2,
        _                                => 3,
    };

    internal ExpandBuilder(IOperatorBuilder source, Direction direction, string? typeFilter, ExpandOutputMode mode)
    {
        _source = source; _direction = direction; _typeFilter = typeFilter; _mode = mode;
    }

    public IPhysicalOperator Build(ISchemaApi schema)
    {
        RelationshipTypeId? typeId = _typeFilter != null
            ? schema.GetOrCreateRelationshipType(_typeFilter)
            : null;
        return new ExpandOperator(_source.Build(schema), _source.CurrentEntityColumn, _direction, typeId, _mode);
    }
}

internal sealed class PropertyLookupBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _source;
    private readonly string _key;
    public int CurrentEntityColumn => _source.CurrentEntityColumn;
    public int PredictedOutputColumnCount => _source.PredictedOutputColumnCount + 1;

    internal PropertyLookupBuilder(IOperatorBuilder source, string key)
    {
        _source = source; _key = key;
    }

    public IPhysicalOperator Build(ISchemaApi schema)
    {
        var keyId = schema.GetOrCreatePropertyKey(_key);
        return new PropertyLookupOperator(_source.Build(schema), _source.CurrentEntityColumn, keyId, _key);
    }
}
