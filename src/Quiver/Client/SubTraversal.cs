using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Api;

/// <summary>
/// .Where() / .Not() に渡すサブトラバーサルのビルダー。
/// 外側の現在エンティティを起点として内側プランを構築する。
/// </summary>
internal sealed class SubTraversal
{
    private readonly CorrelatedInputOperator _probe;
    private readonly LogicalOp _plan;
    private readonly ISchemaCatalog _schema;
    private readonly int _entityColumn;

    internal SubTraversal(CorrelatedInputOperator probe, LogicalOp plan, ISchemaCatalog schema, int entityColumn = 0)
    {
        _probe = probe;
        _plan = plan;
        _schema = schema;
        _entityColumn = entityColumn;
    }

    /// <summary>外向 (Outgoing) Edgeを辿る。</summary>
    public SubTraversal Out(string? type = null)
    {
        var expand = new ExpandOp(_plan, _plan.CurrentEntityColumn, Direction.Outgoing, type, ExpandOutputMode.NeighborOnly, null);
        return new SubTraversal(_probe, expand, _schema, expand.CurrentEntityColumn);
    }

    /// <summary>型付きEdgeで外向に辿る。</summary>
    public SubTraversal Out<TEdge>() where TEdge : IGraphEdge<TEdge>
        => Out(TEdge.GraphType);

    /// <summary>内向 (Incoming) Edgeを辿る。</summary>
    public SubTraversal In(string? type = null)
    {
        var expand = new ExpandOp(_plan, _plan.CurrentEntityColumn, Direction.Incoming, type, ExpandOutputMode.NeighborOnly, null);
        return new SubTraversal(_probe, expand, _schema, expand.CurrentEntityColumn);
    }

    /// <summary>型付きEdgeで内向に辿る。</summary>
    public SubTraversal In<TEdge>() where TEdge : IGraphEdge<TEdge>
        => In(TEdge.GraphType);

    /// <summary>双方向のEdgeを辿る。</summary>
    public SubTraversal Both(string? type = null)
    {
        var expand = new ExpandOp(_plan, _plan.CurrentEntityColumn, Direction.Both, type, ExpandOutputMode.NeighborOnly, null);
        return new SubTraversal(_probe, expand, _schema, expand.CurrentEntityColumn);
    }

    /// <summary>型付きEdgeで双方向に辿る。</summary>
    public SubTraversal Both<TEdge>() where TEdge : IGraphEdge<TEdge>
        => Both(TEdge.GraphType);

    /// <summary>ラベルでフィルタする。</summary>
    public SubTraversal HasLabel(string label)
    {
        var labelId = _schema.ResolveLabel(label);
        var col = _entityColumn;
        return new SubTraversal(_probe,
            new FilterOp(_plan, _ => new LabelPredicate(labelId, col)),
            _schema, _entityColumn);
    }

    /// <summary>プロパティ <paramref name="key"/> が文字列 <paramref name="value"/> と等しい要素のみを通す。</summary>
    public SubTraversal Has(string key, string value)
    {
        var keyId = _schema.ResolvePropertyKey(key);
        var col = _entityColumn;
        return new SubTraversal(_probe,
            new FilterOp(_plan, _ => new PropertyEqStringPredicate(col, keyId, value)),
            _schema, _entityColumn);
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="long"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public SubTraversal Has(string key, long value)
    {
        var keyId = _schema.ResolvePropertyKey(key);
        var col = _entityColumn;
        var pred = P.Eq(value);
        return new SubTraversal(_probe,
            new FilterOp(_plan, _ => new PropertyInt64Predicate(col, keyId, pred)),
            _schema, _entityColumn);
    }

    /// <summary>任意の <see cref="PropertyPredicate"/> でプロパティ <paramref name="key"/> をフィルタする。</summary>
    public SubTraversal Has(string key, PropertyPredicate pred)
    {
        var keyId = _schema.ResolvePropertyKey(key);
        var col = _entityColumn;
        return new SubTraversal(_probe,
            new FilterOp(_plan, _ => PredicateDispatch.Build(col, keyId, pred)),
            _schema, _entityColumn);
    }

    internal IPredicate BuildExistsPredicate(int outerEntityColumn)
        => new SubquerySemiJoinPredicate(outerEntityColumn, _probe, PhysicalPlanner.Plan(_plan, _schema), exists: true);

    internal IPredicate BuildNotExistsPredicate(int outerEntityColumn)
        => new SubquerySemiJoinPredicate(outerEntityColumn, _probe, PhysicalPlanner.Plan(_plan, _schema), exists: false);

    /// <summary>
    /// <c>.Union</c> / <c>.Coalesce</c> / <c>.Optional</c> の分岐として使うため、
    /// サブトラバーサルを単独の物理オペレータとして構築する。<see cref="CorrelatedInputOperator"/> の
    /// バインドは <see cref="SubTraversal"/> 構築時にキャプチャした参照を介して呼び出し側が行う。
    /// </summary>
    internal IPhysicalOperator BuildBranchOperator() => PhysicalPlanner.Plan(_plan, _schema);

    /// <summary>サブプラン出力中で現在のエンティティを保持する列番号。</summary>
    internal int BranchEntityColumn => _plan.CurrentEntityColumn;
}
