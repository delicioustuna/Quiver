using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Api;

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

    /// <summary>外向 (Outgoing) リレーションシップを辿る。</summary>
    public SubTraversal Out(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Outgoing, type, ExpandOutputMode.NeighborOnly);
        return new SubTraversal(_probe, expand, _schema, expand.CurrentEntityColumn);
    }

    /// <summary>型付きリレーションシップで外向に辿る。</summary>
    public SubTraversal Out<TRel>() where TRel : IGraphRelationship<TRel>
        => Out(TRel.GraphType);

    /// <summary>内向 (Incoming) リレーションシップを辿る。</summary>
    public SubTraversal In(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Incoming, type, ExpandOutputMode.NeighborOnly);
        return new SubTraversal(_probe, expand, _schema, expand.CurrentEntityColumn);
    }

    /// <summary>型付きリレーションシップで内向に辿る。</summary>
    public SubTraversal In<TRel>() where TRel : IGraphRelationship<TRel>
        => In(TRel.GraphType);

    /// <summary>双方向のリレーションシップを辿る。</summary>
    public SubTraversal Both(string? type = null)
    {
        var expand = new ExpandBuilder(_builder, Direction.Both, type, ExpandOutputMode.NeighborOnly);
        return new SubTraversal(_probe, expand, _schema, expand.CurrentEntityColumn);
    }

    /// <summary>型付きリレーションシップで双方向に辿る。</summary>
    public SubTraversal Both<TRel>() where TRel : IGraphRelationship<TRel>
        => Both(TRel.GraphType);

    /// <summary>ラベルでフィルタする。</summary>
    public SubTraversal HasLabel(string label)
    {
        var labelId = _schema.GetOrCreateLabel(label);
        var col = _entityColumn;
        return new SubTraversal(_probe,
            new FilterBuilder(_builder, _ => new LabelPredicate(labelId, col)),
            _schema, _entityColumn);
    }

    /// <summary>プロパティ <paramref name="key"/> が文字列 <paramref name="value"/> と等しい要素のみを通す。</summary>
    public SubTraversal Has(string key, string value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col = _entityColumn;
        return new SubTraversal(_probe,
            new FilterBuilder(_builder, _ => new PropertyEqStringPredicate(col, keyId, value)),
            _schema, _entityColumn);
    }

    /// <summary>プロパティ <paramref name="key"/> が <see cref="long"/> <paramref name="value"/> と等しい要素のみを通す。</summary>
    public SubTraversal Has(string key, long value)
    {
        var keyId = _schema.GetOrCreatePropertyKey(key);
        var col = _entityColumn;
        var pred = P.Eq(value);
        return new SubTraversal(_probe,
            new FilterBuilder(_builder, _ => new PropertyInt64Predicate(col, keyId, pred)),
            _schema, _entityColumn);
    }

    /// <summary>任意の <see cref="PropertyPredicate"/> でプロパティ <paramref name="key"/> をフィルタする。</summary>
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

    /// <summary>
    /// GC-4: <c>.Union</c> / <c>.Coalesce</c> / <c>.Optional</c> の分岐として使うため、
    /// サブトラバーサルを単独の物理オペレータとして構築する。<see cref="CorrelatedInputOperator"/> の
    /// バインドは <see cref="SubTraversal"/> 構築時にキャプチャした参照を介して呼び出し側が行う。
    /// </summary>
    internal IPhysicalOperator BuildBranchOperator() => _builder.Build(_schema);

    /// <summary>GC-4: サブプラン出力中で現在のエンティティを保持する列番号。</summary>
    internal int BranchEntityColumn => _builder.CurrentEntityColumn;
}
