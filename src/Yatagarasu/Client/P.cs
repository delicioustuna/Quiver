using System.Text.RegularExpressions;

namespace Yatagarasu.Api;

/// <summary>
/// プロパティ述語を構築するファクトリ。
/// <c>g.Vertices().Has("age", P.Gt(20))</c> のように <see cref="GraphTraversal{T}.Has(string, PropertyPredicate)"/>
/// に渡して使う。
/// </summary>
public static class P
{
    /// <summary>等値比較 (<see cref="long"/> 値)。</summary>
    public static PropertyPredicate Eq(long value)   => new(PredicateKind.Eq,  value,  value,  null);

    /// <summary>等値比較 (文字列値)。</summary>
    public static PropertyPredicate Eq(string value) => new(PredicateKind.Eq,  0,      0,      value);

    /// <summary>より大 (<c>&gt;</c>)。</summary>
    public static PropertyPredicate Gt(long value)   => new(PredicateKind.Gt,  value,  0,      null);

    /// <summary>以上 (<c>&gt;=</c>)。</summary>
    public static PropertyPredicate Gte(long value)  => new(PredicateKind.Gte, value,  0,      null);

    /// <summary>より小 (<c>&lt;</c>)。</summary>
    public static PropertyPredicate Lt(long value)   => new(PredicateKind.Lt,  value,  0,      null);

    /// <summary>以下 (<c>&lt;=</c>)。</summary>
    public static PropertyPredicate Lte(long value)  => new(PredicateKind.Lte, value,  0,      null);

    /// <summary>閉区間 <c>[from, to]</c> の範囲比較。</summary>
    public static PropertyPredicate Between(long from, long to) => new(PredicateKind.Between, from, to, null);

    // ── 浮動小数点 (double/float/Half) の比較・範囲述語 ──────────────────

    private static long D(double v) => BitConverter.DoubleToInt64Bits(v);

    /// <summary>等値比較 (<see cref="double"/> 値)。float/Half は double に widen して渡す。</summary>
    public static PropertyPredicate Eq(double value)  => new(PredicateKind.Eq,  D(value), 0, null, isDouble: true);

    /// <summary>より大 (<c>&gt;</c>, <see cref="double"/>)。</summary>
    public static PropertyPredicate Gt(double value)  => new(PredicateKind.Gt,  D(value), 0, null, isDouble: true);

    /// <summary>以上 (<c>&gt;=</c>, <see cref="double"/>)。</summary>
    public static PropertyPredicate Gte(double value) => new(PredicateKind.Gte, D(value), 0, null, isDouble: true);

    /// <summary>より小 (<c>&lt;</c>, <see cref="double"/>)。</summary>
    public static PropertyPredicate Lt(double value)  => new(PredicateKind.Lt,  D(value), 0, null, isDouble: true);

    /// <summary>以下 (<c>&lt;=</c>, <see cref="double"/>)。</summary>
    public static PropertyPredicate Lte(double value) => new(PredicateKind.Lte, D(value), 0, null, isDouble: true);

    /// <summary>半開区間 <c>[from, to)</c> の範囲比較 (<see cref="double"/>)。</summary>
    public static PropertyPredicate Between(double from, double to) => new(PredicateKind.Between, D(from), D(to), null, isDouble: true);

    /// <summary>文字列プロパティが指定した候補値のいずれかと一致する場合に通す。</summary>
    public static PropertyPredicate Within(params string[] values) => new(PredicateKind.Within, 0, 0, null, values);

    /// <summary>
    /// <c>P.Without(...)</c> — 文字列プロパティが指定値のいずれにも該当しない場合、
    /// またはプロパティ自体が存在しない場合に通す。
    /// </summary>
    public static PropertyPredicate Without(params string[] values) => new(PredicateKind.Without, 0, 0, null, values);

    // ── テキスト述語 ─────────────────────────────────────────────────

    /// <summary>Cypher の <c>STARTS WITH</c> — オーディナル・大文字小文字を区別する前方一致判定。</summary>
    public static PropertyPredicate StartsWith(string prefix) => new(PredicateKind.StartsWith, 0, 0, prefix ?? throw new ArgumentNullException(nameof(prefix)));

    /// <summary>Cypher の <c>ENDS WITH</c> — オーディナル・大文字小文字を区別する後方一致判定。</summary>
    public static PropertyPredicate EndsWith(string suffix) => new(PredicateKind.EndsWith, 0, 0, suffix ?? throw new ArgumentNullException(nameof(suffix)));

    /// <summary>Cypher の <c>CONTAINS</c> — オーディナル・大文字小文字を区別する部分文字列判定。</summary>
    public static PropertyPredicate Contains(string needle) => new(PredicateKind.Contains, 0, 0, needle ?? throw new ArgumentNullException(nameof(needle)));

    /// <summary>
    /// Cypher の <c>=~</c> 正規表現マッチ。先行コンパイルしておくことで、述語が走査する
    /// 各行で同一の <see cref="Regex"/> インスタンスを再利用する。
    /// </summary>
    public static PropertyPredicate Regex(string pattern, RegexOptions options = RegexOptions.None)
    {
        if (pattern is null) throw new ArgumentNullException(nameof(pattern));
        var compiled = new Regex(pattern, options | RegexOptions.Compiled);
        return new PropertyPredicate(PredicateKind.Regex, 0, 0, pattern, compiledRegex: compiled);
    }

    // ── 述語レベルの真偽演算 ────────────────────────────────

    /// <summary>
    /// <c>NOT (predicate)</c>。任意の <see cref="PropertyPredicate"/> をラップし、
    /// 内側述語の評価結果を反転する。プロパティ欠落時は内側述語が <c>false</c> となり、
    /// 反転して <c>true</c> を返す (Cypher の <c>NOT n.age = 30</c> が欠落 <c>age</c> に対して
    /// <c>NOT false = true</c> となる挙動と一致)。
    /// </summary>
    public static PropertyPredicate Not(PropertyPredicate inner)
    {
        if (inner is null) throw new ArgumentNullException(nameof(inner));
        return new PropertyPredicate(PredicateKind.Not, 0, 0, null, inner: inner);
    }

    /// <summary>
    /// 同一キーに対する AND 結合 (例: <c>P.And(P.Gt(20), P.Lt(40))</c> で半開区間)。
    /// 異なるキーをまたぐ AND は <c>.Has(...).Has(...)</c> の連結 (暗黙の AND) を使うこと。
    /// </summary>
    public static PropertyPredicate And(params PropertyPredicate[] predicates) => Compound(PredicateKind.And, predicates);

    /// <summary>
    /// 同一キーに対する OR 結合 (例: <c>P.Or(P.StartsWith("Al"), P.StartsWith("Bo"))</c>)。
    /// 異なるキーをまたぐ OR にはトラバーサルレベルの <c>g.Vertices().Or(t1, t2)</c> を使うこと。
    /// </summary>
    public static PropertyPredicate Or(params PropertyPredicate[] predicates) => Compound(PredicateKind.Or, predicates);

    private static PropertyPredicate Compound(PredicateKind kind, PropertyPredicate[] predicates)
    {
        if (predicates is null) throw new ArgumentNullException(nameof(predicates));
        if (predicates.Length == 0) throw new ArgumentException("複合述語には少なくとも 1 つの内部述語が必要です。", nameof(predicates));
        return new PropertyPredicate(kind, 0, 0, null, innerArray: predicates);
    }
}

/// <summary><see cref="PropertyPredicate"/> の判定種別を表す列挙体。</summary>
public enum PredicateKind
{
    /// <summary>等値比較。</summary>
    Eq,
    /// <summary>より大 (<c>&gt;</c>)。</summary>
    Gt,
    /// <summary>以上 (<c>&gt;=</c>)。</summary>
    Gte,
    /// <summary>より小 (<c>&lt;</c>)。</summary>
    Lt,
    /// <summary>以下 (<c>&lt;=</c>)。</summary>
    Lte,
    /// <summary>閉区間範囲。</summary>
    Between,
    /// <summary>候補値のいずれかとの一致。</summary>
    Within,
    /// <summary>候補値のいずれにも一致しない、または欠落。</summary>
    Without,
    /// <summary>前方一致。</summary>
    StartsWith,
    /// <summary>後方一致。</summary>
    EndsWith,
    /// <summary>部分文字列一致。</summary>
    Contains,
    /// <summary>正規表現マッチ。</summary>
    Regex,
    /// <summary>述語の否定。</summary>
    Not,
    /// <summary>述語の AND 結合。</summary>
    And,
    /// <summary>述語の OR 結合。</summary>
    Or,
}

/// <summary>
/// プロパティに対する述語の表現。<see cref="P"/> ファクトリで生成し、
/// <see cref="GraphTraversal{T}.Has(string, PropertyPredicate)"/> に渡す。
/// </summary>
public sealed class PropertyPredicate
{
    /// <summary>判定種別。</summary>
    public PredicateKind Kind { get; }

    /// <summary>範囲の下限 (種別によっては比較対象値)。</summary>
    public long LongFrom { get; }

    /// <summary>範囲の上限。</summary>
    public long LongTo { get; }

    internal string? StringValue { get; }
    internal string[]? WithinValues { get; }

    // 複合述語のキャリア
    internal PropertyPredicate? Inner { get; }
    internal PropertyPredicate[]? InnerArray { get; }
    internal Regex? CompiledRegex { get; }

    /// <summary>
    /// 比較対象が浮動小数点 (double/float/Half) であることを示す。true のとき
    /// <see cref="LongFrom"/>/<see cref="LongTo"/> は <see cref="System.BitConverter.DoubleToInt64Bits"/>
    /// でエンコードされた double ビットを保持する。
    /// </summary>
    internal bool IsDouble { get; }

    /// <summary>浮動小数点比較の下限値 (<see cref="IsDouble"/> が true のとき有効)。</summary>
    internal double DoubleFrom => BitConverter.Int64BitsToDouble(LongFrom);
    /// <summary>浮動小数点比較の上限値 (<see cref="IsDouble"/> が true のとき有効)。</summary>
    internal double DoubleTo => BitConverter.Int64BitsToDouble(LongTo);

    internal PropertyPredicate(
        PredicateKind kind,
        long from,
        long to,
        string? str,
        string[]? within = null,
        PropertyPredicate? inner = null,
        PropertyPredicate[]? innerArray = null,
        Regex? compiledRegex = null,
        bool isDouble = false)
    {
        Kind = kind; LongFrom = from; LongTo = to; StringValue = str;
        WithinValues = within; Inner = inner; InnerArray = innerArray; CompiledRegex = compiledRegex;
        IsDouble = isDouble;
    }
}
