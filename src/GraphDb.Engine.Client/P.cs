namespace GraphDb.Engine.Client;

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
}

public enum PredicateKind { Eq, Gt, Gte, Lt, Lte, Between, Within }

public sealed class PropertyPredicate
{
    public PredicateKind Kind { get; }
    public long LongFrom { get; }
    public long LongTo { get; }
    internal string? StringValue { get; }
    internal string[]? WithinValues { get; }

    internal PropertyPredicate(PredicateKind kind, long from, long to, string? str, string[]? within = null)
    {
        Kind = kind; LongFrom = from; LongTo = to; StringValue = str; WithinValues = within;
    }
}
