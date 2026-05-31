using Quiver.Api.Internal;
using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// GC-1: プロパティ値トラバーサル向けの <c>.is(value)</c> ステップ。
/// <c>t.Values(key).Is(v)</c> を <c>t.Has(key, v).Values(key)</c> に書き換えることで、
/// 等値比較を (バイト列が存在する) プロパティストア側で行わせる。タプルストリームに
/// 射影後フィルタを掛ける方法だと、単一パスの Volcano モデルでは UTF-8 バイト列を
/// 汎用述語に渡し直す経路が無いため、書き換えが必要になる。
/// </summary>
public static class IsExtensions
{
    /// <summary>GC-1: <c>.Values(key).Is(value)</c> 用の文字列等値フィルタ。</summary>
    public static GraphTraversal<string> Is(this GraphTraversal<string> traversal, string value)
    {
        ArgumentNullException.ThrowIfNull(traversal);
        ArgumentNullException.ThrowIfNull(value);
        if (traversal._builder is not PropertyLookupBuilder lookup)
            throw new InvalidOperationException(
                "GraphTraversal<string>.Is(value) は .Values(key) の直後でのみ有効です。" +
                "他の文字列ソース (例: .Label()) はフィルタ対象のプロパティキー文脈を保持しません。");

        var keyId = traversal._schema.GetOrCreatePropertyKey(lookup.Key);
        var sourceCol = lookup.Source.CurrentEntityColumn;
        var filtered = new FilterBuilder(
            lookup.Source,
            _ => new PropertyEqStringPredicate(sourceCol, keyId, value));
        var rebuiltLookup = new PropertyLookupBuilder(filtered, lookup.Key);
        int valueCol = rebuiltLookup.PredictedOutputColumnCount - 1;
        return new GraphTraversal<string>(
            traversal._tx, traversal._schema, rebuiltLookup,
            row => row.GetString(valueCol), traversal._entityColumn);
    }

    /// <summary>GC-1: <c>.Values(key).Is(value)</c> 用の <see cref="long"/> 等値フィルタ。</summary>
    public static GraphTraversal<long> Is(this GraphTraversal<long> traversal, long value)
    {
        ArgumentNullException.ThrowIfNull(traversal);
        if (traversal._builder is not PropertyLookupBuilder lookup)
            throw new InvalidOperationException(
                "GraphTraversal<long>.Is(value) は .Values(key) または .Id() の直後でのみ有効です。" +
                "生の ID をフィルタするには .Has(key, P.Eq(value)) を直接使用してください。");

        var keyId = traversal._schema.GetOrCreatePropertyKey(lookup.Key);
        var sourceCol = lookup.Source.CurrentEntityColumn;
        var pred = P.Eq(value);
        var filtered = new FilterBuilder(
            lookup.Source,
            _ => new PropertyInt64Predicate(sourceCol, keyId, pred));
        var rebuiltLookup = new PropertyLookupBuilder(filtered, lookup.Key);
        int valueCol = rebuiltLookup.PredictedOutputColumnCount - 1;
        return new GraphTraversal<long>(
            traversal._tx, traversal._schema, rebuiltLookup,
            row => row.GetInt64(valueCol), traversal._entityColumn);
    }
}
