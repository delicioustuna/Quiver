using System.Text.RegularExpressions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Api.Internal;

internal sealed class LabelPredicate : IPredicate
{
    private readonly int _column;
    private readonly LabelId _labelId;
    internal LabelPredicate(LabelId labelId, int column = 0) { _labelId = labelId; _column = column; }

    /// <summary>ARCH-7: optimizer の LabelScanRewrite / KNN cost-fallback がラベルを introspect するための公開。</summary>
    internal LabelId Label => _labelId;
    /// <summary>ARCH-7: 述語が参照するタプル列番号 (col 0 のみ scan へ畳める)。</summary>
    internal int Column => _column;

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_column].LongValue);
        using var h = tx.Nodes.Read(nodeId);
        return h.Label == _labelId;
    }
}

internal sealed class PropertyEqStringPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly string _value;

    internal PropertyEqStringPredicate(int nodeColumn, PropertyKeyId keyId, string value)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _value = value;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Nodes.EnumerateProperties(nodeId, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.String) return false;
            return System.Text.Encoding.UTF8.GetString(prop.Value.Utf8StringValue) == _value;
        }
        return false;
    }
}

internal sealed class PropertyInt64Predicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly PropertyPredicate _pred;

    internal PropertyInt64Predicate(int nodeColumn, PropertyKeyId keyId, PropertyPredicate pred)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _pred = pred;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Nodes.EnumerateProperties(nodeId, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            // BA-8: reject non-integer values without decoding.
            // (The previous fallback to Int64Value returned scrambled bits for
            // Double / Bool / String, so Gt(30) on a Double property would lie.)
            var flags = prop.Value.Type.ToFlags();
            if ((flags & (PropertyTypeFlags.Int32 | PropertyTypeFlags.Int64)) == PropertyTypeFlags.None)
                return false;
            long v = prop.Value.Type == PropertyValueType.Int32
                ? prop.Value.Int32Value
                : prop.Value.Int64Value;
            return _pred.Kind switch
            {
                PredicateKind.Eq      => v == _pred.LongFrom,
                PredicateKind.Gt      => v >  _pred.LongFrom,
                PredicateKind.Gte     => v >= _pred.LongFrom,
                PredicateKind.Lt      => v <  _pred.LongFrom,
                PredicateKind.Lte     => v <= _pred.LongFrom,
                PredicateKind.Between => v >= _pred.LongFrom && v < _pred.LongTo,
                _                     => false,
            };
        }
        return false;
    }
}

internal sealed class PropertyWithinStringPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly HashSet<string> _values;

    internal PropertyWithinStringPredicate(int nodeColumn, PropertyKeyId keyId, IEnumerable<string> values)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _values = new HashSet<string>(values);
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Nodes.EnumerateProperties(nodeId, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.String) return false;
            return _values.Contains(System.Text.Encoding.UTF8.GetString(prop.Value.Utf8StringValue));
        }
        return false;
    }
}

internal sealed class PropertyDoublePredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly long _encodedValue;

    internal PropertyDoublePredicate(int nodeColumn, PropertyKeyId keyId, long encodedValue)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _encodedValue = encodedValue;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Nodes.EnumerateProperties(nodeId, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.Double) return false;
            return BitConverter.DoubleToInt64Bits(prop.Value.DoubleValue) == _encodedValue;
        }
        return false;
    }
}

/// <summary>
/// GC-1: existence test for a property key on a node. Used by
/// <c>.Has(key)</c> (mustExist=true) and <c>.HasNot(key)</c> (mustExist=false).
/// The mustExist flag inlines negation so GC-1 does not need to wait on
/// GC-2's NegatedPredicate.
/// </summary>
internal sealed class PropertyExistsPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly bool _mustExist;

    internal PropertyExistsPredicate(int nodeColumn, PropertyKeyId keyId, bool mustExist)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _mustExist = mustExist;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        // Unknown property key (token store never observed it) ⇒ definitely
        // absent. HasNot(key) returns true, Has(key) returns false.
        if (!_keyId.IsValid) return !_mustExist;

        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Nodes.EnumerateProperties(nodeId, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            if (en.Current.KeyId == _keyId) return _mustExist;
        }
        return !_mustExist;
    }
}

/// <summary>GC-1: <c>P.Without(...)</c> — string property must not match any listed value.</summary>
internal sealed class PropertyWithoutStringPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly HashSet<string> _values;

    internal PropertyWithoutStringPredicate(int nodeColumn, PropertyKeyId keyId, IEnumerable<string> values)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _values = new HashSet<string>(values);
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Nodes.EnumerateProperties(nodeId, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.String) return true;
            return !_values.Contains(System.Text.Encoding.UTF8.GetString(prop.Value.Utf8StringValue));
        }
        // Missing property: caller's choice; we follow the Gremlin convention
        // that "without X" includes elements that don't have the key at all.
        return true;
    }
}

/// <summary>
/// GC-2: builds an <see cref="IPredicate"/> for a single <c>(column, keyId)</c>
/// pair from a public-facing <see cref="PropertyPredicate"/>. Centralises the
/// dispatch so <see cref="GraphTraversal{T}"/> and <see cref="SubTraversal"/>
/// share one truth table for every <see cref="PredicateKind"/>.
/// </summary>
internal static class PredicateDispatch
{
    internal static IPredicate Build(int nodeColumn, PropertyKeyId keyId, PropertyPredicate pred)
    {
        switch (pred.Kind)
        {
            case PredicateKind.Eq when pred.StringValue != null:
                return new PropertyEqStringPredicate(nodeColumn, keyId, pred.StringValue);
            case PredicateKind.Within when pred.WithinValues != null:
                return new PropertyWithinStringPredicate(nodeColumn, keyId, pred.WithinValues);
            case PredicateKind.Without when pred.WithinValues != null:
                return new PropertyWithoutStringPredicate(nodeColumn, keyId, pred.WithinValues);
            case PredicateKind.StartsWith:
                return new StringPrefixPredicate(nodeColumn, keyId, pred.StringValue ?? string.Empty);
            case PredicateKind.EndsWith:
                return new StringSuffixPredicate(nodeColumn, keyId, pred.StringValue ?? string.Empty);
            case PredicateKind.Contains:
                return new StringContainsPredicate(nodeColumn, keyId, pred.StringValue ?? string.Empty);
            case PredicateKind.Regex when pred.CompiledRegex != null:
                return new RegexPropertyPredicate(nodeColumn, keyId, pred.CompiledRegex);
            case PredicateKind.Not when pred.Inner != null:
                return new NegatedPredicate(Build(nodeColumn, keyId, pred.Inner));
            case PredicateKind.And when pred.InnerArray != null:
                return new AndPredicate(BuildAll(nodeColumn, keyId, pred.InnerArray));
            case PredicateKind.Or when pred.InnerArray != null:
                return new OrPredicate(BuildAll(nodeColumn, keyId, pred.InnerArray));
            default:
                // Eq/Gt/Gte/Lt/Lte/Between with numeric comparand fall through
                // to the int64 predicate, which already enforces type flags.
                return new PropertyInt64Predicate(nodeColumn, keyId, pred);
        }
    }

    private static IPredicate[] BuildAll(int nodeColumn, PropertyKeyId keyId, PropertyPredicate[] preds)
    {
        var result = new IPredicate[preds.Length];
        for (int i = 0; i < preds.Length; i++) result[i] = Build(nodeColumn, keyId, preds[i]);
        return result;
    }
}

/// <summary>
/// GC-2: shared base for string predicates that load a single string property
/// and test it against a fixed comparand. Encapsulates the "find the property,
/// reject non-string types, decode UTF-8" boilerplate so the prefix/suffix/
/// contains/regex variants only differ in the final match test.
/// </summary>
internal abstract class StringPropertyPredicateBase : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;

    protected StringPropertyPredicateBase(int nodeColumn, PropertyKeyId keyId)
    {
        _nodeColumn = nodeColumn; _keyId = keyId;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Nodes.EnumerateProperties(nodeId, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.String) return false;
            var s = System.Text.Encoding.UTF8.GetString(prop.Value.Utf8StringValue);
            return Match(s);
        }
        return false;
    }

    protected abstract bool Match(string value);
}

/// <summary>GC-2: Cypher <c>STARTS WITH</c> / Gremlin <c>TextP.startingWith</c>.</summary>
internal sealed class StringPrefixPredicate : StringPropertyPredicateBase
{
    private readonly string _prefix;
    internal StringPrefixPredicate(int nodeColumn, PropertyKeyId keyId, string prefix) : base(nodeColumn, keyId) => _prefix = prefix;
    protected override bool Match(string value) => value.StartsWith(_prefix, StringComparison.Ordinal);
}

/// <summary>GC-2: Cypher <c>ENDS WITH</c> / Gremlin <c>TextP.endingWith</c>.</summary>
internal sealed class StringSuffixPredicate : StringPropertyPredicateBase
{
    private readonly string _suffix;
    internal StringSuffixPredicate(int nodeColumn, PropertyKeyId keyId, string suffix) : base(nodeColumn, keyId) => _suffix = suffix;
    protected override bool Match(string value) => value.EndsWith(_suffix, StringComparison.Ordinal);
}

/// <summary>GC-2: Cypher <c>CONTAINS</c> / Gremlin <c>TextP.containing</c>.</summary>
internal sealed class StringContainsPredicate : StringPropertyPredicateBase
{
    private readonly string _needle;
    internal StringContainsPredicate(int nodeColumn, PropertyKeyId keyId, string needle) : base(nodeColumn, keyId) => _needle = needle;
    protected override bool Match(string value) => value.Contains(_needle, StringComparison.Ordinal);
}

/// <summary>GC-2: Cypher <c>=~</c> regex match. The Regex is compiled once at
/// construction and reused per row.</summary>
internal sealed class RegexPropertyPredicate : StringPropertyPredicateBase
{
    private readonly Regex _regex;
    internal RegexPropertyPredicate(int nodeColumn, PropertyKeyId keyId, Regex regex) : base(nodeColumn, keyId) => _regex = regex;
    protected override bool Match(string value) => _regex.IsMatch(value);
}

/// <summary>GC-2: <c>NOT (predicate)</c> — inverts any IPredicate.</summary>
internal sealed class NegatedPredicate : IPredicate
{
    private readonly IPredicate _inner;
    internal NegatedPredicate(IPredicate inner) => _inner = inner;
    public bool Evaluate(in TupleRef tuple, ITransaction tx) => !_inner.Evaluate(in tuple, tx);
}

/// <summary>GC-2: short-circuiting AND across multiple IPredicates.</summary>
internal sealed class AndPredicate : IPredicate
{
    private readonly IPredicate[] _inners;
    internal AndPredicate(IPredicate[] inners) => _inners = inners;
    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        for (int i = 0; i < _inners.Length; i++)
            if (!_inners[i].Evaluate(in tuple, tx)) return false;
        return true;
    }
}

/// <summary>GC-2: short-circuiting OR across multiple IPredicates.</summary>
internal sealed class OrPredicate : IPredicate
{
    private readonly IPredicate[] _inners;
    internal OrPredicate(IPredicate[] inners) => _inners = inners;
    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        for (int i = 0; i < _inners.Length; i++)
            if (_inners[i].Evaluate(in tuple, tx)) return true;
        return false;
    }
}

internal sealed class PropertyBoolPredicate : IPredicate
{
    private readonly int _nodeColumn;
    private readonly PropertyKeyId _keyId;
    private readonly long _scalar;

    internal PropertyBoolPredicate(int nodeColumn, PropertyKeyId keyId, long scalar)
    {
        _nodeColumn = nodeColumn; _keyId = keyId; _scalar = scalar;
    }

    public bool Evaluate(in TupleRef tuple, ITransaction tx)
    {
        var nodeId = new NodeId(tuple[_nodeColumn].LongValue);
        using var node = tx.Nodes.Read(nodeId);
        var en = tx.Nodes.EnumerateProperties(nodeId, tx.Properties); // ARCH-5c: inline + overflow
        while (en.MoveNext())
        {
            var prop = en.Current;
            if (prop.KeyId != _keyId) continue;
            if (prop.Value.Type != PropertyValueType.Bool) return false;
            return (prop.Value.BoolValue ? 1L : 0L) == _scalar;
        }
        return false;
    }
}
