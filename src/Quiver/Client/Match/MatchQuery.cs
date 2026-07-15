using Quiver.Api.Internal;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Api.Match;

/// <summary>
/// Match DSL のクエリビルダ。<see cref="Where"/> で述語を蓄積し、
/// <see cref="Return{TResult}"/> で射影クロージャを指定する。
/// </summary>
public sealed class MatchQuery
{
    private readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;
    private readonly CompileFunc _compile;
    private readonly List<(string variable, string key, PropertyPredicate pred)> _wherePredicates = new();

    // node/edge パターンと星型ハイパーエッジパターンを同じ実行経路 (Execute) に載せるため、
    // パターン種別ごとの compile 呼び出しをクロージャに閉じ込める。
    internal delegate (IPhysicalOperator plan, Dictionary<string, int> varToColumn) CompileFunc(
        IGraphTransaction tx,
        ISchemaApi schema,
        List<(string variable, string key, PropertyPredicate pred)> where);

    internal MatchQuery(IGraphTransaction tx, ISchemaApi schema, GraphPattern pattern)
        : this(tx, schema, (t, s, w) => MatchCompiler.Compile(t, s, pattern, w))
    {
    }

    internal MatchQuery(IGraphTransaction tx, ISchemaApi schema, HyperedgePattern pattern)
        : this(tx, schema, (t, s, w) => MatchCompiler.Compile(t, s, pattern, w))
    {
    }

    private MatchQuery(IGraphTransaction tx, ISchemaApi schema, CompileFunc compile)
    {
        _tx = tx; _schema = schema; _compile = compile;
    }

    /// <summary>
    /// パターン変数 <paramref name="variable"/> のプロパティ <paramref name="key"/> に対する
    /// 述語 <paramref name="pred"/> を蓄積する (Cypher の <c>WHERE</c> 相当)。
    /// </summary>
    public MatchQuery Where(string variable, string key, PropertyPredicate pred)
    {
        _wherePredicates.Add((variable, key, pred));
        return this;
    }

    /// <summary>射影クロージャを設定して <see cref="ReturnClause{TResult}"/> に進む。</summary>
    public ReturnClause<TResult> Return<TResult>(Func<MatchContext, TResult> selector)
        => new(_tx, _schema, _compile, _wherePredicates, selector);

    /// <summary>マッチした行の件数を返す。</summary>
    public long Count()
    {
        long count = 0;
        var (plan, varMap) = _compile(_tx, _schema, _wherePredicates);
        var result = _tx.Execute(plan);
        foreach (var _ in result.Rows())
            count++;
        return count;
    }

}

/// <summary>
/// <see cref="MatchQuery.Return{TResult}"/> から派生する終端ステップ。
/// 結果の取得方法 (<see cref="ToList"/> / <see cref="First"/> / <see cref="AsCursor"/> /
/// <see cref="AsEnumerable"/>) を提供する。
/// </summary>
public sealed class ReturnClause<TResult>
{
    private readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;
    private readonly MatchQuery.CompileFunc _compile;
    private readonly List<(string variable, string key, PropertyPredicate pred)> _where;
    private readonly Func<MatchContext, TResult> _selector;

    internal ReturnClause(
        IGraphTransaction tx, ISchemaApi schema,
        MatchQuery.CompileFunc compile,
        List<(string variable, string key, PropertyPredicate pred)> where,
        Func<MatchContext, TResult> selector)
    {
        _tx = tx; _schema = schema; _compile = compile; _where = where; _selector = selector;
    }

    /// <summary>すべての結果をリストとして返す。</summary>
    public List<TResult> ToList()
    {
        var results = new List<TResult>();
        var (plan, varMap) = _compile(_tx, _schema, _where);
        var queryResult = _tx.Execute(plan);
        foreach (var row in queryResult.Rows())
        {
            var ctx = new MatchContext(row, _tx, varMap);
            results.Add(_selector(ctx));
        }
        return results;
    }

    /// <summary>最初の 1 件を返す。結果が空のときは <see langword="default"/>。</summary>
    public TResult? First()
    {
        var (plan, varMap) = _compile(_tx, _schema, _where);
        var queryResult = _tx.Execute(plan);
        foreach (var row in queryResult.Rows())
        {
            var ctx = new MatchContext(row, _tx, varMap);
            return _selector(ctx);
        }
        return default;
    }

    /// <summary>
    /// 結果のストリーミングカーソルを返す。カーソルの寿命は呼び出し側が管理し、
    /// 必ず破棄すること。所属トランザクションが生きている間だけ有効。
    /// </summary>
    public ITraversalCursor<TResult> AsCursor()
    {
        var (plan, varMap) = _compile(_tx, _schema, _where);
        var inner = _tx.ExecuteCursor(plan);
        return new TraversalCursor<TResult>(inner, row => _selector(new MatchContext(row, _tx, varMap)));
    }

    /// <summary>
    /// 結果を逐次列挙する <see cref="IEnumerable{TResult}"/> を返す。全件を一度に
    /// メモリに乗せず、所属トランザクションが生きている間だけ有効。
    /// </summary>
    public IEnumerable<TResult> AsEnumerable()
    {
        var (plan, varMap) = _compile(_tx, _schema, _where);
        using var cursor = _tx.ExecuteCursor(plan);
        while (cursor.MoveNext())
            yield return _selector(new MatchContext(cursor.Current, _tx, varMap));
    }

}
