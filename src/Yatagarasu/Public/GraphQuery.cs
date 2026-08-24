using System.Linq.Expressions;
using Yatagarasu.Api;
using Yatagarasu.Api.Internal;
using Yatagarasu.Api.Match;
using Yatagarasu.Core;

namespace Yatagarasu;

/// <summary>callback または session の snapshot に束縛された query 入口。</summary>
public sealed class GraphQuery
{
    private readonly GraphReadAccess _read;

    internal GraphQuery(GraphReadAccess read) => _read = read;

    /// <summary>すべての Vertex から traversal を開始する。</summary>
    public GraphVertexQuery Vertices()
    {
        _read.EnsureActive();
        return new GraphVertexQuery(_read.Transaction.Query.Vertices(), _read);
    }

    /// <summary>指定した Vertex から traversal を開始する。</summary>
    public GraphVertexQuery Vertices(params VertexKey[] vertices)
    {
        _read.EnsureActive();
        ArgumentNullException.ThrowIfNull(vertices);
        VertexId[] ids = new VertexId[vertices.Length];
        for (int i = 0; i < vertices.Length; i++) ids[i] = vertices[i].ToCore();
        return new GraphVertexQuery(_read.Transaction.Query.Vertices(ids), _read);
    }

    /// <summary>指定 model の label に限定した型付き traversal を開始する。</summary>
    public TypedGraphQuery<T> Vertices<T>() where T : IGraphEntity<T>
    {
        _read.EnsureActive();
        GraphTraversal<VertexId> traversal = _read.Transaction.Query.Vertices().HasLabel(T.GraphLabel);
        return new TypedGraphQuery<T>(traversal, _read);
    }

    /// <summary>全文 index を検索する。</summary>
    public GraphVertexQuery Search(string indexName, string queryText, int limit)
    {
        _read.EnsureActive();
        return new GraphVertexQuery(_read.Transaction.Query.Search(indexName, queryText, limit), _read);
    }

    /// <summary>vector index を近傍検索する。</summary>
    public GraphVertexQuery Knn(string indexName, ReadOnlySpan<float> vector, int limit)
    {
        _read.EnsureActive();
        return new GraphVertexQuery(_read.Transaction.Query.Knn(indexName, vector, limit), _read);
    }

    /// <summary>全文と vector の結果を融合検索する。</summary>
    public GraphVertexQuery HybridSearch(
        string textIndex,
        string queryText,
        string vectorIndex,
        ReadOnlySpan<float> vector,
        int limit)
    {
        _read.EnsureActive();
        return new GraphVertexQuery(
            _read.Transaction.Query.HybridSearch(textIndex, queryText, vectorIndex, vector, limit),
            _read);
    }

    /// <summary>Vertex/Edge pattern の Match query を開始する。</summary>
    public GraphMatchQuery Match(GraphPattern pattern)
    {
        _read.EnsureActive();
        ArgumentNullException.ThrowIfNull(pattern);
        return new GraphMatchQuery(_read.Transaction.Query.Match(pattern), _read);
    }

    /// <summary>Nexus pattern の Match query を開始する。</summary>
    public GraphMatchQuery Match(NexusPattern pattern)
    {
        _read.EnsureActive();
        ArgumentNullException.ThrowIfNull(pattern);
        return new GraphMatchQuery(_read.Transaction.Query.Match(pattern), _read);
    }
}

/// <summary>不透明な Vertex key を返す graph traversal。</summary>
public sealed class GraphVertexQuery
{
    private readonly GraphTraversal<VertexId> _inner;
    private readonly GraphReadAccess _read;

    internal GraphVertexQuery(GraphTraversal<VertexId> inner, GraphReadAccess read)
    {
        _inner = inner;
        _read = read;
    }

    /// <summary>label が一致する Vertex に限定する。</summary>
    public GraphVertexQuery HasLabel(string label) => Next(_inner.HasLabel(label));

    /// <summary>指定プロパティが存在する Vertex に限定する。</summary>
    public GraphVertexQuery Has(string property) => Next(_inner.Has(property));

    /// <summary>指定プロパティが述語を満たす Vertex に限定する。</summary>
    public GraphVertexQuery Has(string property, PropertyPredicate predicate) =>
        Next(_inner.Has(property, predicate));

    /// <summary>指定プロパティが値と一致する Vertex に限定する。</summary>
    public GraphVertexQuery Has(string property, GraphValue value) => Next(value.Kind switch
    {
        GraphValueKind.Boolean => _inner.Has(property, value.AsBoolean()),
        GraphValueKind.Int32 => _inner.Has(property, value.AsInt32()),
        GraphValueKind.Int64 => _inner.Has(property, value.AsInt64()),
        GraphValueKind.Double => _inner.Has(property, value.AsDouble()),
        GraphValueKind.String => _inner.Has(property, value.AsString()),
        _ => throw new NotSupportedException($"{value.Kind} は traversal の等値比較に使用できません。"),
    });

    /// <summary>外向 Edge を辿る。</summary>
    public GraphVertexQuery Out(string? edgeType = null) => Next(_inner.Out(edgeType));

    /// <summary>内向 Edge を辿る。</summary>
    public GraphVertexQuery In(string? edgeType = null) => Next(_inner.In(edgeType));

    /// <summary>両方向の Edge を辿る。</summary>
    public GraphVertexQuery Both(string? edgeType = null) => Next(_inner.Both(edgeType));

    /// <summary>先頭から指定件数に限定する。</summary>
    public GraphVertexQuery Limit(long count) => Next(_inner.Limit(count));

    /// <summary>先頭から指定件数を読み飛ばす。</summary>
    public GraphVertexQuery Skip(long count) => Next(_inner.Skip(count));

    /// <summary>重複する Vertex を除去する。</summary>
    public GraphVertexQuery Deduplicate() => Next(_inner.Dedup());

    /// <summary>結果件数を返す。</summary>
    public long Count()
    {
        _read.EnsureActive();
        return _inner.Count();
    }

    /// <summary>結果を callback の外へ保持できる key リストとして返す。</summary>
    public IReadOnlyList<VertexKey> ToList()
    {
        _read.EnsureActive();
        List<VertexId> values = _inner.ToList();
        var result = new VertexKey[values.Count];
        for (int i = 0; i < values.Count; i++) result[i] = new VertexKey(values[i]);
        return result;
    }

    private GraphVertexQuery Next(GraphTraversal<VertexId> traversal)
    {
        _read.EnsureActive();
        return new GraphVertexQuery(traversal, _read);
    }
}

/// <summary>生成 mapper で model を復元する型付き traversal。</summary>
/// <typeparam name="T">model 型。</typeparam>
public sealed class TypedGraphQuery<T> where T : IGraphEntity<T>
{
    private readonly GraphTraversal<VertexId> _inner;
    private readonly GraphReadAccess _read;

    internal TypedGraphQuery(GraphTraversal<VertexId> inner, GraphReadAccess read)
    {
        _inner = inner;
        _read = read;
    }

    /// <summary>式で指定したプロパティが値と一致する model に限定する。</summary>
    public TypedGraphQuery<T> Has<TProperty>(
        Expression<Func<T, TProperty>> selector,
        TProperty value)
    {
        string property = MemberName(selector);
        return new TypedGraphQuery<T>(ApplyValue(_inner, property, value), _read);
    }

    /// <summary>式で指定したプロパティが述語を満たす model に限定する。</summary>
    public TypedGraphQuery<T> Has<TProperty>(
        Expression<Func<T, TProperty>> selector,
        PropertyPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new TypedGraphQuery<T>(_inner.Has(MemberName(selector), predicate), _read);
    }

    /// <summary>C# 式の比較、論理積、文字列述語で model を限定する。</summary>
    public TypedGraphQuery<T> Where(Expression<Func<T, bool>> predicate) =>
        new(ExpressionPredicate.Apply(_inner, predicate), _read);

    /// <summary>外向 Edge を辿り、対象 model の label に限定する。</summary>
    public TypedGraphQuery<TTarget> Out<TTarget>(string? edgeType = null)
        where TTarget : IGraphEntity<TTarget> =>
        new(_inner.Out(edgeType).HasLabel(TTarget.GraphLabel), _read);

    /// <summary>先頭から指定件数に限定する。</summary>
    public TypedGraphQuery<T> Limit(long count) => new(_inner.Limit(count), _read);

    /// <summary>結果件数を返す。</summary>
    public long Count()
    {
        _read.EnsureActive();
        return _inner.Count();
    }

    /// <summary>結果を mapper で復元した所有権付きリストとして返す。</summary>
    public IReadOnlyList<T> ToList()
    {
        _read.EnsureActive();
        List<VertexId> ids = _inner.ToList();
        var result = new T[ids.Count];
        for (int i = 0; i < ids.Count; i++) result[i] = T.Read(_read, new VertexKey(ids[i]));
        return result;
    }

    private static GraphTraversal<VertexId> ApplyValue<TProperty>(
        GraphTraversal<VertexId> traversal,
        string property,
        TProperty value) => value switch
    {
        string current => traversal.Has(property, current),
        int current => traversal.Has(property, current),
        long current => traversal.Has(property, current),
        double current => traversal.Has(property, current),
        float current => traversal.Has(property, (double)current),
        Half current => traversal.Has(property, (double)current),
        bool current => traversal.Has(property, current),
        DateTime current => traversal.Has(property, Storage.Records.TemporalCodec.ToUtcTicks(current)),
        DateTimeOffset current => traversal.Has(property, Storage.Records.TemporalCodec.OffsetToUtcTicks(current)),
        DateOnly current => traversal.Has(property, Storage.Records.TemporalCodec.ToDayNumber(current)),
        TimeOnly current => traversal.Has(property, Storage.Records.TemporalCodec.ToTicks(current)),
        TimeSpan current => traversal.Has(property, Storage.Records.TemporalCodec.ToTicks(current)),
        _ => throw new NotSupportedException($"{typeof(TProperty).Name} は型付き等値比較に使用できません。"),
    };

    private static string MemberName<TProperty>(Expression<Func<T, TProperty>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Expression body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } conversion)
            body = conversion.Operand;
        return body is MemberExpression member && member.Expression == selector.Parameters[0]
            ? member.Member.Name
            : throw new ArgumentException("selector は model の直接プロパティ参照である必要があります。", nameof(selector));
    }
}

/// <summary>安定した value と key を返す Match query。</summary>
public sealed class GraphMatchQuery
{
    private readonly MatchQuery _inner;
    private readonly GraphReadAccess _read;

    internal GraphMatchQuery(MatchQuery inner, GraphReadAccess read)
    {
        _inner = inner;
        _read = read;
    }

    /// <summary>パターン変数のプロパティ述語を追加する。</summary>
    public GraphMatchQuery Where(string variable, string property, PropertyPredicate predicate)
    {
        _read.EnsureActive();
        _inner.Where(variable, property, predicate);
        return this;
    }

    /// <summary>結果の射影を指定する。</summary>
    public GraphMatchResult<TResult> Return<TResult>(Func<GraphMatchContext, TResult> selector)
    {
        _read.EnsureActive();
        ArgumentNullException.ThrowIfNull(selector);
        ReturnClause<TResult> result = _inner.Return(
            context => selector(new GraphMatchContext(context, _read)));
        return new GraphMatchResult<TResult>(result, _read);
    }

    /// <summary>マッチした行数を返す。</summary>
    public long Count()
    {
        _read.EnsureActive();
        return _inner.Count();
    }
}

/// <summary>Match の一行から安定した key と value を取得する。</summary>
public sealed class GraphMatchContext
{
    private readonly MatchContext _inner;
    private readonly GraphReadAccess _read;

    internal GraphMatchContext(MatchContext inner, GraphReadAccess read)
    {
        _inner = inner;
        _read = read;
    }

    /// <summary>Vertex 変数を不透明 key として取得する。</summary>
    public VertexKey Vertex(string variable) => new(_inner.Vertex(variable));

    /// <summary>Nexus 変数を不透明 key として取得する。</summary>
    public NexusKey Nexus(string variable) => new(_inner.Nexus(variable));

    /// <summary>Vertex 変数のプロパティを所有権付き値として取得する。</summary>
    public GraphValue Get(string variable, string property) => _read.Get(Vertex(variable), property);

    /// <summary>Vertex 変数を生成 mapper で model に復元する。</summary>
    public T Load<T>(string variable) where T : IGraphEntity<T> => T.Read(_read, Vertex(variable));
}

/// <summary>Match 射影の終端操作。</summary>
/// <typeparam name="TResult">射影結果型。</typeparam>
public sealed class GraphMatchResult<TResult>
{
    private readonly ReturnClause<TResult> _inner;
    private readonly GraphReadAccess _read;

    internal GraphMatchResult(ReturnClause<TResult> inner, GraphReadAccess read)
    {
        _inner = inner;
        _read = read;
    }

    /// <summary>すべての結果を所有権付きリストとして返す。</summary>
    public IReadOnlyList<TResult> ToList()
    {
        _read.EnsureActive();
        return _inner.ToList();
    }

    /// <summary>最初の結果を返す。結果がない場合は既定値。</summary>
    public TResult? First()
    {
        _read.EnsureActive();
        return _inner.First();
    }
}
