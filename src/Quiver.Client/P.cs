using System.Text.RegularExpressions;

namespace Quiver.Client;

public static class P
{
    public static PropertyPredicate Eq(long value)   => new(PredicateKind.Eq,  value,  value,  null);
    public static PropertyPredicate Eq(string value) => new(PredicateKind.Eq,  0,      0,      value);
    public static PropertyPredicate Gt(long value)   => new(PredicateKind.Gt,  value,  0,      null);
    public static PropertyPredicate Gte(long value)  => new(PredicateKind.Gte, value,  0,      null);
    public static PropertyPredicate Lt(long value)   => new(PredicateKind.Lt,  value,  0,      null);
    public static PropertyPredicate Lte(long value)  => new(PredicateKind.Lte, value,  0,      null);
    public static PropertyPredicate Between(long from, long to) => new(PredicateKind.Between, from, to, null);
    public static PropertyPredicate Within(params string[] values) => new(PredicateKind.Within, 0, 0, null, values);

    /// <summary>
    /// GC-1: <c>P.Without(...)</c> — element passes when the string property
    /// is NOT in the listed values, or when the property is missing entirely
    /// (Gremlin's convention).
    /// </summary>
    public static PropertyPredicate Without(params string[] values) => new(PredicateKind.Without, 0, 0, null, values);

    // ── GC-2: text predicates ─────────────────────────────────────────────────

    /// <summary>GC-2: Cypher <c>STARTS WITH</c> — ordinal, case-sensitive prefix test.</summary>
    public static PropertyPredicate StartsWith(string prefix) => new(PredicateKind.StartsWith, 0, 0, prefix ?? throw new ArgumentNullException(nameof(prefix)));

    /// <summary>GC-2: Cypher <c>ENDS WITH</c> — ordinal, case-sensitive suffix test.</summary>
    public static PropertyPredicate EndsWith(string suffix) => new(PredicateKind.EndsWith, 0, 0, suffix ?? throw new ArgumentNullException(nameof(suffix)));

    /// <summary>GC-2: Cypher <c>CONTAINS</c> — ordinal, case-sensitive substring test.</summary>
    public static PropertyPredicate Contains(string needle) => new(PredicateKind.Contains, 0, 0, needle ?? throw new ArgumentNullException(nameof(needle)));

    /// <summary>
    /// GC-2: Cypher <c>=~</c> regex match. Compiled eagerly so a single
    /// <see cref="Regex"/> instance is reused for every row the predicate sees.
    /// </summary>
    public static PropertyPredicate Regex(string pattern, RegexOptions options = RegexOptions.None)
    {
        if (pattern is null) throw new ArgumentNullException(nameof(pattern));
        var compiled = new Regex(pattern, options | RegexOptions.Compiled);
        return new PropertyPredicate(PredicateKind.Regex, 0, 0, pattern, compiledRegex: compiled);
    }

    // ── GC-2: predicate-level boolean composition ────────────────────────────

    /// <summary>
    /// GC-2: <c>NOT (predicate)</c>. Wraps any other <see cref="PropertyPredicate"/>;
    /// the negation is applied after the inner predicate has been evaluated, so
    /// a property that is missing entirely still results in <c>false → true</c>
    /// (Cypher <c>NOT n.age = 30</c> on a missing <c>age</c> is <c>NOT false = true</c>).
    /// </summary>
    public static PropertyPredicate Not(PropertyPredicate inner)
    {
        if (inner is null) throw new ArgumentNullException(nameof(inner));
        return new PropertyPredicate(PredicateKind.Not, 0, 0, null, inner: inner);
    }

    /// <summary>
    /// GC-2: same-key conjunction (e.g. <c>P.And(P.Gt(20), P.Lt(40))</c> for a
    /// half-open range). For cross-key conditions, chain <c>.Has(...).Has(...)</c>
    /// (implicit AND) instead.
    /// </summary>
    public static PropertyPredicate And(params PropertyPredicate[] predicates) => Compound(PredicateKind.And, predicates);

    /// <summary>
    /// GC-2: same-key disjunction (e.g. <c>P.Or(P.StartsWith("Al"), P.StartsWith("Bo"))</c>).
    /// For cross-key OR, use the traversal-level <c>g.V().Or(t1, t2)</c>.
    /// </summary>
    public static PropertyPredicate Or(params PropertyPredicate[] predicates) => Compound(PredicateKind.Or, predicates);

    private static PropertyPredicate Compound(PredicateKind kind, PropertyPredicate[] predicates)
    {
        if (predicates is null) throw new ArgumentNullException(nameof(predicates));
        if (predicates.Length == 0) throw new ArgumentException("Compound predicate requires at least one inner predicate.", nameof(predicates));
        return new PropertyPredicate(kind, 0, 0, null, innerArray: predicates);
    }
}

public enum PredicateKind
{
    Eq, Gt, Gte, Lt, Lte, Between, Within, Without,
    // GC-2
    StartsWith, EndsWith, Contains, Regex,
    Not, And, Or,
}

public sealed class PropertyPredicate
{
    public PredicateKind Kind { get; }
    public long LongFrom { get; }
    public long LongTo { get; }
    internal string? StringValue { get; }
    internal string[]? WithinValues { get; }

    // GC-2 carriers
    internal PropertyPredicate? Inner { get; }
    internal PropertyPredicate[]? InnerArray { get; }
    internal Regex? CompiledRegex { get; }

    internal PropertyPredicate(
        PredicateKind kind,
        long from,
        long to,
        string? str,
        string[]? within = null,
        PropertyPredicate? inner = null,
        PropertyPredicate[]? innerArray = null,
        Regex? compiledRegex = null)
    {
        Kind = kind; LongFrom = from; LongTo = to; StringValue = str;
        WithinValues = within; Inner = inner; InnerArray = innerArray; CompiledRegex = compiledRegex;
    }
}
