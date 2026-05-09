using GraphDb.Engine.Client.Internal;
using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;
using GraphDb.Engine.Stores;

namespace GraphDb.Engine.Client.Match;

public sealed class MatchQuery
{
    private readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;
    private readonly GraphPattern _pattern;
    private readonly List<(string variable, string key, PropertyPredicate pred)> _wherePredicates = new();

    internal MatchQuery(IGraphTransaction tx, ISchemaApi schema, GraphPattern pattern)
    {
        _tx = tx; _schema = schema; _pattern = pattern;
    }

    public MatchQuery Where(string variable, string key, PropertyPredicate pred)
    {
        _wherePredicates.Add((variable, key, pred));
        return this;
    }

    public ReturnClause<TResult> Return<TResult>(Func<MatchContext, TResult> selector)
        => new(_tx, _schema, _pattern, _wherePredicates, selector);

    public long Count()
    {
        long count = 0;
        var (plan, varMap) = MatchCompiler.Compile(_tx, _schema, _pattern, _wherePredicates);
        var result = _tx.Execute(plan);
        foreach (var _ in result.Rows())
            count++;
        return count;
    }
}

public sealed class ReturnClause<TResult>
{
    private readonly IGraphTransaction _tx;
    private readonly ISchemaApi _schema;
    private readonly GraphPattern _pattern;
    private readonly List<(string variable, string key, PropertyPredicate pred)> _where;
    private readonly Func<MatchContext, TResult> _selector;

    internal ReturnClause(
        IGraphTransaction tx, ISchemaApi schema,
        GraphPattern pattern,
        List<(string variable, string key, PropertyPredicate pred)> where,
        Func<MatchContext, TResult> selector)
    {
        _tx = tx; _schema = schema; _pattern = pattern; _where = where; _selector = selector;
    }

    public List<TResult> ToList()
    {
        var results = new List<TResult>();
        var (plan, varMap) = MatchCompiler.Compile(_tx, _schema, _pattern, _where);
        var queryResult = _tx.Execute(plan);
        foreach (var row in queryResult.Rows())
        {
            var ctx = new MatchContext(row, _tx, varMap);
            results.Add(_selector(ctx));
        }
        return results;
    }

    public TResult? First()
    {
        var (plan, varMap) = MatchCompiler.Compile(_tx, _schema, _pattern, _where);
        var queryResult = _tx.Execute(plan);
        foreach (var row in queryResult.Rows())
        {
            var ctx = new MatchContext(row, _tx, varMap);
            return _selector(ctx);
        }
        return default;
    }
}
