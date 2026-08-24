using System.Linq.Expressions;
using System.Reflection;

namespace Yatagarasu.Api.Internal;

/// <summary>
/// C# 式ツリー (<c>p =&gt; p.Age &gt; 20 &amp;&amp; p.Name.StartsWith("A")</c>) を
/// 既存の <see cref="PropertyPredicate"/> ベースのフィルタ (<see cref="GraphTraversal{T}.Has(string, PropertyPredicate)"/>)
/// へ変換する内部トランスレータ。Vertex述語 (<c>TypedGraphTraversal&lt;T&gt;.Where</c>) と
/// エッジ述語 (生成糖衣の <c>{Rel}(e =&gt; ...)</c>) で共有する。
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

        // 述語ドメインは「メンバの CLR 型」で決める (リテラル型ではない)。
        // これにより doubleProp > 2 (整数リテラル) でも double 比較になる。
        var memberType = Nullable.GetUnderlyingType(member.Type) ?? member.Type;

        if (memberType == typeof(string))
            return (key, op switch
            {
                ExpressionType.Equal    => P.Eq((string)value!),
                ExpressionType.NotEqual => P.Not(P.Eq((string)value!)),
                _ => throw new NotSupportedException($"文字列プロパティ '{key}' に対する比較 {op} は非対応です (== / != / StartsWith 等を使ってください)。"),
            });

        if (memberType == typeof(bool))
        {
            bool b = Convert.ToBoolean(value);
            return op switch
            {
                ExpressionType.Equal    => (key, P.Eq(b ? 1 : 0)),
                ExpressionType.NotEqual => (key, P.Eq(b ? 0 : 1)),
                _ => throw new NotSupportedException($"bool プロパティ '{key}' に対する比較 {op} は非対応です (== / != のみ)。"),
            };
        }

        if (memberType == typeof(double) || memberType == typeof(float) || memberType == typeof(Half))
            return (key, DoublePredicate(op, Convert.ToDouble(value), key));

        // 日時系は格納時と同じ TemporalCodec で long に正準化してから比較する。
        if (TryTemporalToLong(memberType, value, out long tv))
            return (key, IntegralPredicate(op, tv, key));

        if (IsIntegralType(memberType))
            return (key, IntegralPredicate(op, Convert.ToInt64(value), key));

        throw new NotSupportedException(
            $"プロパティ '{key}' の型 {memberType.Name} は式ツリー述語で非対応です " +
            "(string/bool/整数/double/float/Half/DateTime系)。decimal/Guid 等の範囲は Has(key, P.xxx) を使ってください。");
    }

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

    private static PropertyPredicate DoublePredicate(ExpressionType op, double v, string key) => op switch
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

    /// <summary>日時系 CLR 型を格納と同じ正準 long へ変換する (TemporalCodec 共有)。</summary>
    private static bool TryTemporalToLong(Type t, object? value, out long result)
    {
        if (t == typeof(DateTime))       { result = Yatagarasu.Storage.Records.TemporalCodec.ToUtcTicks((DateTime)value!); return true; }
        if (t == typeof(DateTimeOffset)) { result = Yatagarasu.Storage.Records.TemporalCodec.OffsetToUtcTicks((DateTimeOffset)value!); return true; }
        if (t == typeof(DateOnly))       { result = Yatagarasu.Storage.Records.TemporalCodec.ToDayNumber((DateOnly)value!); return true; }
        if (t == typeof(TimeOnly))       { result = Yatagarasu.Storage.Records.TemporalCodec.ToTicks((TimeOnly)value!); return true; }
        if (t == typeof(TimeSpan))       { result = Yatagarasu.Storage.Records.TemporalCodec.ToTicks((TimeSpan)value!); return true; }
        result = 0; return false;
    }

    private static bool IsIntegralType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
        t == typeof(sbyte) || t == typeof(uint) || t == typeof(ushort) || t == typeof(char) ||
        t.IsEnum;

    private static object? Eval(Expression e)
    {
        return e switch
        {
            ConstantExpression constant => constant.Value,
            MemberExpression member => ReadMember(member),
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion =>
                ConvertValue(Eval(conversion.Operand), conversion.Type),
            UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } negate =>
                Negate(Eval(negate.Operand), negate.Type),
            BinaryExpression binary when IsSupportedValueBinary(binary.NodeType) => EvaluateBinary(binary),
            NewExpression created => InvokeConstructor(created),
            _ => throw new NotSupportedException(
                $"値式 '{e}' は型付き述語で評価できません。定数、捕捉変数、単純な数値式を使ってください。"),
        };
    }

    private static object? ReadMember(MemberExpression expression)
    {
        object? owner = expression.Expression is null ? null : Eval(expression.Expression);
        return expression.Member switch
        {
            FieldInfo field => field.GetValue(owner),
            PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(owner),
            _ => throw new NotSupportedException(
                $"メンバ '{expression.Member.Name}' は型付き述語の値として評価できません。"),
        };
    }

    private static object? InvokeConstructor(NewExpression expression)
    {
        if (expression.Constructor is null)
            throw new NotSupportedException($"値式 '{expression}' に呼び出し可能なコンストラクタがありません。");
        object?[] arguments = expression.Arguments.Select(Eval).ToArray();
        return expression.Constructor.Invoke(arguments);
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
            return null;
        Type effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (effectiveType.IsInstanceOfType(value))
            return value;
        if (effectiveType.IsEnum)
            return Enum.ToObject(effectiveType, value);
        return Convert.ChangeType(value, effectiveType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsSupportedValueBinary(ExpressionType type) => type is
        ExpressionType.Add or ExpressionType.AddChecked or
        ExpressionType.Subtract or ExpressionType.SubtractChecked or
        ExpressionType.Multiply or ExpressionType.MultiplyChecked or
        ExpressionType.Divide or ExpressionType.Modulo or
        ExpressionType.Coalesce;

    private static object? EvaluateBinary(BinaryExpression expression)
    {
        object? left = Eval(expression.Left);
        if (expression.NodeType == ExpressionType.Coalesce)
            return left ?? Eval(expression.Right);
        object? right = Eval(expression.Right);
        if (left is null || right is null)
            throw new NotSupportedException($"数値式 '{expression}' に null は使用できません。");

        Type type = Nullable.GetUnderlyingType(expression.Type) ?? expression.Type;
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Int32 => CalculateInt32(expression.NodeType, Convert.ToInt32(left), Convert.ToInt32(right)),
            TypeCode.Int64 => CalculateInt64(expression.NodeType, Convert.ToInt64(left), Convert.ToInt64(right)),
            TypeCode.Single => CalculateSingle(expression.NodeType, Convert.ToSingle(left), Convert.ToSingle(right)),
            TypeCode.Double => CalculateDouble(expression.NodeType, Convert.ToDouble(left), Convert.ToDouble(right)),
            TypeCode.Decimal => CalculateDecimal(expression.NodeType, Convert.ToDecimal(left), Convert.ToDecimal(right)),
            _ => throw new NotSupportedException(
                $"型 {type.Name} の値式 '{expression}' は型付き述語で評価できません。"),
        };
    }

    private static object Negate(object? value, Type type)
    {
        if (value is null)
            throw new NotSupportedException("null は符号反転できません。");
        Type effectiveType = Nullable.GetUnderlyingType(type) ?? type;
        return Type.GetTypeCode(effectiveType) switch
        {
            TypeCode.Int32 => -Convert.ToInt32(value),
            TypeCode.Int64 => -Convert.ToInt64(value),
            TypeCode.Single => -Convert.ToSingle(value),
            TypeCode.Double => -Convert.ToDouble(value),
            TypeCode.Decimal => -Convert.ToDecimal(value),
            _ => throw new NotSupportedException($"型 {effectiveType.Name} の値は符号反転できません。"),
        };
    }

    private static int CalculateInt32(ExpressionType operation, int left, int right) => operation switch
    {
        ExpressionType.Add => left + right,
        ExpressionType.AddChecked => checked(left + right),
        ExpressionType.Subtract => left - right,
        ExpressionType.SubtractChecked => checked(left - right),
        ExpressionType.Multiply => left * right,
        ExpressionType.MultiplyChecked => checked(left * right),
        ExpressionType.Divide => left / right,
        ExpressionType.Modulo => left % right,
        _ => throw UnsupportedValueOperation(operation),
    };

    private static long CalculateInt64(ExpressionType operation, long left, long right) => operation switch
    {
        ExpressionType.Add => left + right,
        ExpressionType.AddChecked => checked(left + right),
        ExpressionType.Subtract => left - right,
        ExpressionType.SubtractChecked => checked(left - right),
        ExpressionType.Multiply => left * right,
        ExpressionType.MultiplyChecked => checked(left * right),
        ExpressionType.Divide => left / right,
        ExpressionType.Modulo => left % right,
        _ => throw UnsupportedValueOperation(operation),
    };

    private static float CalculateSingle(ExpressionType operation, float left, float right) => operation switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => left + right,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => left - right,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => left * right,
        ExpressionType.Divide => left / right,
        ExpressionType.Modulo => left % right,
        _ => throw UnsupportedValueOperation(operation),
    };

    private static double CalculateDouble(ExpressionType operation, double left, double right) => operation switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => left + right,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => left - right,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => left * right,
        ExpressionType.Divide => left / right,
        ExpressionType.Modulo => left % right,
        _ => throw UnsupportedValueOperation(operation),
    };

    private static decimal CalculateDecimal(ExpressionType operation, decimal left, decimal right) => operation switch
    {
        ExpressionType.Add or ExpressionType.AddChecked => left + right,
        ExpressionType.Subtract or ExpressionType.SubtractChecked => left - right,
        ExpressionType.Multiply or ExpressionType.MultiplyChecked => left * right,
        ExpressionType.Divide => left / right,
        ExpressionType.Modulo => left % right,
        _ => throw UnsupportedValueOperation(operation),
    };

    private static NotSupportedException UnsupportedValueOperation(ExpressionType operation) =>
        new($"値式の演算 {operation} は型付き述語で評価できません。");
}
