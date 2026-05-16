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

internal sealed class KnnNodeSourceBuilder : IOperatorBuilder
{
    private readonly string _indexName;
    private readonly float[] _query;
    private readonly int _k;

    public int CurrentEntityColumn => 0;
    public int PredictedOutputColumnCount => 1;

    internal KnnNodeSourceBuilder(string indexName, ReadOnlySpan<float> query, int k)
    {
        _indexName = indexName;
        _query = query.ToArray();
        _k = k;
    }

    public IPhysicalOperator Build(ISchemaApi schema)
        => new KnnNodeSourceOperator(_indexName, _query, _k);
}

internal sealed class FilteredKnnNodeSourceBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _source;
    private readonly string _indexName;
    private readonly float[] _query;
    private readonly int _k;

    // Filter still emits the NodeId column at position 0 (single-column tuple).
    public int CurrentEntityColumn => 0;
    public int PredictedOutputColumnCount => 1;

    internal FilteredKnnNodeSourceBuilder(IOperatorBuilder source, string indexName, ReadOnlySpan<float> query, int k)
    {
        _source = source;
        _indexName = indexName;
        _query = query.ToArray();
        _k = k;
    }

    public IPhysicalOperator Build(ISchemaApi schema)
        => new FilteredKnnNodeSourceOperator(
            _source.Build(schema), _source.CurrentEntityColumn, _indexName, _query, _k);
}

internal sealed class PropertyLookupBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _source;
    private readonly string _key;
    public int CurrentEntityColumn => _source.CurrentEntityColumn;
    public int PredictedOutputColumnCount => _source.PredictedOutputColumnCount + 1;

    /// <summary>GC-1: exposed so <c>GraphTraversal.Is(value)</c> can rewrite
    /// <c>Values(key).Is(v)</c> into <c>Has(key, v).Values(key)</c>.</summary>
    internal IOperatorBuilder Source => _source;
    internal string Key => _key;

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

/// <summary>GC-1: wraps any source with <see cref="LimitOperator"/>'s skip/limit window.</summary>
internal sealed class LimitBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _source;
    private readonly long _limit;
    private readonly long _skip;
    public int CurrentEntityColumn => _source.CurrentEntityColumn;
    public int PredictedOutputColumnCount => _source.PredictedOutputColumnCount;

    internal LimitBuilder(IOperatorBuilder source, long limit, long skip)
    {
        _source = source; _limit = limit; _skip = skip;
    }

    public IPhysicalOperator Build(ISchemaApi schema)
        => new LimitOperator(_source.Build(schema), _limit, _skip);
}

/// <summary>GC-1: <c>.outV()</c> / <c>.inV()</c> / <c>.otherV()</c> — resolves
/// a relationship column into a node column via <see cref="RelationshipEndpointOperator"/>.</summary>
internal sealed class RelationshipEndpointBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _source;
    private readonly int _relColumn;
    private readonly RelationshipEndpoint _endpoint;
    public int CurrentEntityColumn => 0;
    public int PredictedOutputColumnCount => 1;

    internal RelationshipEndpointBuilder(IOperatorBuilder source, int relColumn, RelationshipEndpoint endpoint)
    {
        _source = source; _relColumn = relColumn; _endpoint = endpoint;
    }

    public IPhysicalOperator Build(ISchemaApi schema)
        => new RelationshipEndpointOperator(_source.Build(schema), _relColumn, _endpoint);
}

/// <summary>
/// GC-3: <c>.OrderBy(key)</c> — chains a <see cref="PropertyLookupOperator"/>
/// (so the sort key is materialised into a column) and then wraps it in
/// <see cref="SortOperator"/>. The entity column the caller cares about is
/// untouched, so downstream projections still read the original NodeId /
/// RelationshipId.
/// </summary>
internal sealed class SortBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _source;
    private readonly string? _propertyKey;
    private readonly int _sortColumn;
    private readonly bool _descending;

    public int CurrentEntityColumn => _source.CurrentEntityColumn;
    public int PredictedOutputColumnCount => _propertyKey != null
        ? _source.PredictedOutputColumnCount + 1
        : _source.PredictedOutputColumnCount;

    /// <summary>Sort by a property value — the property gets materialised into an extra column first.</summary>
    internal SortBuilder(IOperatorBuilder source, string propertyKey, bool descending)
    {
        _source = source;
        _propertyKey = propertyKey;
        _sortColumn = source.PredictedOutputColumnCount; // the column added by PropertyLookup
        _descending = descending;
    }

    /// <summary>Sort by an existing column index (e.g. the entity column itself).</summary>
    internal SortBuilder(IOperatorBuilder source, int sortColumn, bool descending)
    {
        _source = source;
        _propertyKey = null;
        _sortColumn = sortColumn;
        _descending = descending;
    }

    public IPhysicalOperator Build(ISchemaApi schema)
    {
        if (_propertyKey != null)
        {
            var keyId = schema.GetOrCreatePropertyKey(_propertyKey);
            var withProp = new PropertyLookupOperator(_source.Build(schema), _source.CurrentEntityColumn, keyId, _propertyKey);
            return new SortOperator(withProp, _sortColumn, _descending);
        }
        return new SortOperator(_source.Build(schema), _sortColumn, _descending);
    }
}

/// <summary>GC-1: <c>.label()</c> — adds a string column carrying the label name for the entity column.</summary>
internal sealed class LabelNameLookupBuilder : IOperatorBuilder
{
    private readonly IOperatorBuilder _source;
    private readonly int _nodeColumn;
    private readonly ISchemaApi _schemaRef;
    public int CurrentEntityColumn => _source.CurrentEntityColumn;
    public int PredictedOutputColumnCount => _source.PredictedOutputColumnCount + 1;

    internal LabelNameLookupBuilder(IOperatorBuilder source, int nodeColumn, ISchemaApi schemaRef)
    {
        _source = source; _nodeColumn = nodeColumn; _schemaRef = schemaRef;
    }

    public IPhysicalOperator Build(ISchemaApi schema)
        => new LabelNameLookupOperator(_source.Build(schema), _nodeColumn, _schemaRef.GetLabelName);
}
