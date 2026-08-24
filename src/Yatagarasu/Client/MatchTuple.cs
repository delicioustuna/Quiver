using Yatagarasu;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;

namespace Yatagarasu.Api;

/// <summary>
/// <c>.Select&lt;TResult&gt;(Func&lt;MatchTuple, TResult&gt;)</c> に渡される型付きアクセサ。
/// 事前に <c>.As(name)</c> でバインドされたエイリアス名を、対応するタプルスロットへ解決する。
/// 未定義のエイリアスは <see cref="InvalidOperationException"/>、型不一致のアクセスは
/// スロットの宣言型に基づき <see cref="InvalidCastException"/> を投げる。
/// </summary>
internal readonly struct MatchTuple
{
    private readonly QueryRow _row;
    private readonly Dictionary<string, int> _aliases;

    internal MatchTuple(QueryRow row, Dictionary<string, int> aliases)
    {
        _row = row;
        _aliases = aliases;
    }

    private int Column(string alias)
    {
        if (!_aliases.TryGetValue(alias, out var col))
            throw new InvalidOperationException($"エイリアス '{alias}' は未定義です。");
        return col;
    }

    /// <summary>エイリアスに紐づくスロットを <see cref="VertexId"/> として取り出す。</summary>
    public VertexId Vertex(string alias) => _row.GetVertexId(Column(alias));

    /// <summary>エイリアスに紐づくスロットを <see cref="EdgeId"/> として取り出す。</summary>
    public EdgeId Edge(string alias) => _row.GetEdgeId(Column(alias));

    /// <summary>エイリアスに紐づくスロットを <see cref="NexusId"/> として取り出す。</summary>
    public NexusId Nexus(string alias) => _row.GetNexusId(Column(alias));

    /// <summary>エイリアスに紐づくスロットを <see cref="long"/> として取り出す。</summary>
    public long Int64(string alias) => _row.GetInt64(Column(alias));

    /// <summary>エイリアスに紐づくスロットを <see cref="string"/> として取り出す。</summary>
    public string String(string alias) => _row.GetString(Column(alias));

    /// <summary>エイリアスに紐づくスロットを <see cref="bool"/> として取り出す。</summary>
    public bool Boolean(string alias) => _row.GetBoolean(Column(alias));

    /// <summary>エイリアスに紐づくスロットを <see cref="double"/> として取り出す。</summary>
    public double Double(string alias) => _row.GetDouble(Column(alias));

    /// <summary>エイリアスに紐づくスロットの実型を返す。</summary>
    public MatchValueType TypeOf(string alias) => _row.GetSlotType(Column(alias)) switch
    {
        TupleSlotType.VertexId => MatchValueType.Vertex,
        TupleSlotType.EdgeId => MatchValueType.Edge,
        TupleSlotType.NexusId => MatchValueType.Nexus,
        TupleSlotType.Bool => MatchValueType.Boolean,
        TupleSlotType.Int64 => MatchValueType.Int64,
        TupleSlotType.Double => MatchValueType.Double,
        TupleSlotType.Utf8String => MatchValueType.String,
        TupleSlotType.Bytes => MatchValueType.Bytes,
        _ => MatchValueType.Null,
    };
}
