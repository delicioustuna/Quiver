using System.Linq.Expressions;

namespace Quiver.Api.Internal;

/// <summary>
/// GC-7: C# 式ツリー (<c>p =&gt; p.Age &gt; 20 &amp;&amp; p.Name.StartsWith("A")</c>) を
/// 既存の <see cref="PropertyPredicate"/> ベースのフィルタ (<see cref="GraphTraversal{T}.Has(string, PropertyPredicate)"/>)
/// へ変換する内部トランスレータ。ノード述語 (<c>TypedGraphTraversal&lt;T&gt;.Where</c>) と
/// エッジ述語 (生成糖衣の <c>{Rel}(e =&gt; ...)</c>) で共有する。
///
/// プロパティ名は CLR メンバ名をグラフキーとして用いる (既存の式ツリー版 <c>Has(selector, value)</c> と同規約)。
/// 対応外の式は <see cref="NotSupportedException"/> を投げ、利用者は <c>Has(key, P.xxx)</c> を escape hatch にできる。
/// </summary>
internal static class ExpressionPredicate
{
    /// <summary>
    /// <paramref name="predicate"/> を解析し、対応する <c>Has</c> フィルタを
    /// <paramref name="source"/> に連結したトラバーサルを返す。
    /// </summary>
    public static GraphTraversal<TX> Apply<TX, TEntity>(
        GraphTraversal<TX> source,
        Expression<Func<TEntity, bool>> predicate)
        => ApplyBody(source, predicate.Body);

    private static GraphTraversal<TX> ApplyBody<TX>(GraphTraversal<TX> g, Expression body)
    {
        switch (body)
        {
            // AndAlso: 暗黙 AND として .Has().Has() に連結 (キー跨ぎ可)。
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and2:
                return ApplyBody(ApplyBody(g, and2.Left), and2.Right);

            // OrElse: 同一キーのみ P.Or で結合。キーが異なる場合は非対応。
            case BinaryExpression { NodeType: ExpressionType.OrElse } or2:
            {
                var (lk, lp) = TranslateLeaf(or2.Left);
                var (rk, rp) = TranslateLeaf(or2.Right);
                if (!string.Equals(lk, rk, StringComparison.Ordinal))
                    throw new NotSupportedException(
                        $"キーを跨ぐ '||' ({lk} / {rk}) は式ツリー述語では非対応です。" +
                        "トラバーサルレベルの Or(t1, t2) を使ってください。");
                return g.Has(lk, P.Or(lp, rp));
            }

            // 単項否定: !p.IsActive / !p.Name.StartsWith(...) などをリーフとして翻訳。
            // それ以外 (比較・メソッド呼び出し) はリーフとして翻訳。
            default:
            {
                var (key, pred) = TranslateLeaf(body);
                return g.Has(key, pred);
            }
        }
    }

    /// <summary>比較・文字列メソッド・否定の 1 リーフを (キー, 述語) に翻訳する。</summary>
    private static (string Key, PropertyPredicate Pred) TranslateLeaf(Expression expr)
    {
        switch (expr)
        {
            case UnaryExpression { NodeType: ExpressionType.Not } not:
            {
                var (key, inner) = TranslateLeaf(not.Operand);
                return (key, P.Not(inner));
            }

            case BinaryExpression be when IsComparison(be.NodeType):
                return TranslateComparison(be);

            case MethodCallExpression mc:
                return TranslateStringMethod(mc);

            // bool プロパティの素の参照 (p => p.IsActive) は == true と解釈。
            case MemberExpression me when me.Type == typeof(bool):
                return (me.Member.Name, P.Eq(1));

            default:
                throw new NotSupportedException(
                    $"式 '{expr}' は型付き述語に変換できません。Has(key, P.xxx) を使ってください。");
        }
    }

    private static (string Key, PropertyPredicate Pred) TranslateComparison(BinaryExpression be)
    {
        // メンバが左右どちらにあるか判定し、定数側を評価する。
        var (member, op, valueExpr, flipped) = OrientComparison(be);
        var key = MemberName(member);
        var value = Eval(valueExpr);

        // メンバが右辺だった場合は演算子の向きを反転 (例: 20 < p.Age → p.Age > 20)。
        if (flipped) op = Flip(op);

        return value switch
        {
            string s => (key, op switch
            {
                ExpressionType.Equal              => P.Eq(s),
                ExpressionType.NotEqual           => P.Not(P.Eq(s)),
                ExpressionType.GreaterThan        => P.Gt(StringErr()),
                _ => throw new NotSupportedException($"文字列プロパティ '{key}' に対する比較 {op} は非対応です (== / != のみ)。"),
            }),
            bool b => op switch
            {
                ExpressionType.Equal    => (key, P.Eq(b ? 1 : 0)),
                ExpressionType.NotEqual => (key, P.Eq(b ? 0 : 1)),
                _ => throw new NotSupportedException($"bool プロパティ '{key}' に対する比較 {op} は非対応です (== / != のみ)。"),
            },
            _ when IsIntegral(value) => (key, IntegralPredicate(op, Convert.ToInt64(value), key)),
            _ => throw new NotSupportedException(
                $"プロパティ '{key}' の値型 {value?.GetType().Name ?? "null"} は式ツリー述語で非対応です " +
                "(int/long/string/bool のみ)。範囲は Has(key, P.xxx) を使ってください。"),
        };
    }

    private static long StringErr()
        => throw new NotSupportedException("文字列プロパティに対する大小比較は非対応です (== / != / StartsWith 等を使ってください)。");

    private static PropertyPredicate IntegralPredicate(ExpressionType op, long v, string key) => op switch
    {
        ExpressionType.Equal              => P.Eq(v),
        ExpressionType.NotEqual           => P.Not(P.Eq(v)),
        ExpressionType.GreaterThan        => P.Gt(v),
        ExpressionType.GreaterThanOrEqual => P.Gte(v),
        ExpressionType.LessThan           => P.Lt(v),
        ExpressionType.LessThanOrEqual    => P.Lte(v),
        _ => throw new NotSupportedException($"プロパティ '{key}' に対する比較 {op} は非対応です。"),
    };

    private static (string Key, PropertyPredicate Pred) TranslateStringMethod(MethodCallExpression mc)
    {
        if (mc.Object is not MemberExpression target)
            throw new NotSupportedException($"メソッド呼び出し '{mc}' は型付き述語に変換できません。");
        var key = target.Member.Name;
        var arg = Eval(mc.Arguments[0]) as string
                  ?? throw new NotSupportedException($"'{mc.Method.Name}' の引数は文字列定数である必要があります。");
        return mc.Method.Name switch
        {
            nameof(string.StartsWith) => (key, P.StartsWith(arg)),
            nameof(string.EndsWith)   => (key, P.EndsWith(arg)),
            nameof(string.Contains)   => (key, P.Contains(arg)),
            _ => throw new NotSupportedException(
                $"メソッド '{mc.Method.Name}' は非対応です (StartsWith / EndsWith / Contains のみ)。"),
        };
    }

    // ── ヘルパ ────────────────────────────────────────────────────────────────

    private static (MemberExpression Member, ExpressionType Op, Expression Value, bool Flipped) OrientComparison(BinaryExpression be)
    {
        if (Unwrap(be.Left) is MemberExpression lm)
            return (lm, be.NodeType, be.Right, false);
        if (Unwrap(be.Right) is MemberExpression rm)
            return (rm, be.NodeType, be.Left, true);
        throw new NotSupportedException($"比較式 '{be}' の片側はプロパティアクセスである必要があります。");
    }

    private static Expression Unwrap(Expression e)
        => e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u ? u.Operand : e;

    private static string MemberName(MemberExpression me) => me.Member.Name;

    private static bool IsComparison(ExpressionType t) => t is
        ExpressionType.Equal or ExpressionType.NotEqual or
        ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
        ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    private static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan        => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan           => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual    => ExpressionType.GreaterThanOrEqual,
        _ => t, // Equal / NotEqual は対称
    };

    private static bool IsIntegral(object? v) => v is int or long or short or byte or sbyte or uint or ushort;

    private static object? Eval(Expression e)
    {
        if (e is ConstantExpression c) return c.Value;
        // クロージャ変数・計算式などは compile して評価する。
        return Expression.Lambda(Expression.Convert(e, typeof(object))).Compile().DynamicInvoke();
    }
}
