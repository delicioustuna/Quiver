using Quiver.Client.Internal;
using Quiver.Core;
using Quiver.Operators;
using Quiver.Stores;

namespace Quiver.Client;

/// <summary>
/// .Where() / .Not() に渡すサブトラバーサルのビルダー。
/// 外側の現在エンティティを起点として内側プランを構築する。
/// </summary>
public sealed class SubTraversal
{
    private readonly CorrelatedInputOperator _probe;
    private readonly IOperatorBuilder _builder;
    private readonly ISchemaApi _schema;
    private readonly int _entityColumn;

    internal SubTraversal(CorrelatedInputOperator probe, IOperatorBuilder builder, ISchemaApi schema, int entityColumn = 0)
    {
        _probe = probe;
        _builder = builder;
        _schema = schema;
        _entityColumn = entityColumn;
    }

    public SubTraversal Out(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Outgoing, type, ExpandOutputMode.NeighborOnly);
        return new SubTraversal(_probe, expand, _schema, expand.CurrentEntityColumn);
    }

    public SubTraversal Out<TRel>() where TRel : IGraphRelationship<TRel>
        => Out(TRel.GraphType);

    public SubTraversal In(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Incoming, type, ExpandOutputMode.NeighborOnly);
        return new SubTraversal(_probe, expand, _schema, expand.CurrentEntityColumn);
    }

    public SubTraversal In<TRel>() where TRel : IGraphRelationship<TRel>
        => In(TRel.GraphType);

    public SubTraversal Both(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Both, type, ExpandOutputMode.NeighborOnly);
        return new SubTraversal(_probe, expand, _schema, expand.CurrentEntityColumn);
    }

    public SubTraversal Both<TRel>() where TRel : IGraphRelationship<TRel>
        => Both(TRel.GraphType);

    public SubTraversal HasLabel(string label)
    {
        var labelId = _schema.GetOrCreateLabel(label);
        var col = _entityColumn;
        return new SubTraversal(_probe,
            new FilterBuilder(_builder, _ => new LabelPredicate(labelId, col)),
            _schema, _entityColumn);
    }

    public SubTraversal Has(string key, string value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col = _entityColumn;
        return new SubTraversal(_probe,
            new FilterBuilder(_builder, _ => new PropertyEqStringPredicate(col, keyId, value)),
            _schema, _entityColumn);
    }

    public SubTraversal Has(string key, long value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col = _entityColumn;
        var pred = P.Eq(value);
        return new SubTraversal(_probe,
            new FilterBuilder(_builder, _ => new PropertyInt64Predicate(col, keyId, pred)),
            _schema, _entityColumn);
    }

    public SubTraversal Has(string key, PropertyPredicate pred)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col = _entityColumn;
        return new SubTraversal(_probe,
            new FilterBuilder(_builder, _ => PredicateDispatch.Build(col, keyId, pred)),
            _schema, _entityColumn);
    }

    internal IPredicate BuildExistsPredicate(int outerEntityColumn)
        => new SubquerySemiJoinPredicate(outerEntityColumn, _probe, _builder.Build(_schema), exists: true);

    internal IPredicate BuildNotExistsPredicate(int outerEntityColumn)
        => new SubquerySemiJoinPredicate(outerEntityColumn, _probe, _builder.Build(_schema), exists: false);
}
