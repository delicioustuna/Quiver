using Quiver.Api;

namespace Quiver.Api.Match;

/// <summary>
/// Match DSL のグラフパターン (ノード - エッジ - ノード) を表す不変オブジェクト。
/// <see cref="Node"/> をエントリポイントとし、<see cref="NodePattern.Out{TRel}(NodePattern)"/> や
/// <see cref="NodePattern.In{TRel}(NodePattern)"/> を連結してパターンを構築する。
/// </summary>
public sealed class GraphPattern
{
    internal NodePattern StartNode { get; }
    internal EdgePattern? Edge { get; }
    internal NodePattern? EndNode { get; }

    private GraphPattern(NodePattern start, EdgePattern? edge = null, NodePattern? end = null)
    {
        StartNode = start; Edge = edge; EndNode = end;
    }

    /// <summary>新しいノードパターンを開始する。<paramref name="variable"/> は <c>WHERE</c>/<c>RETURN</c> で参照する識別子。</summary>
    /// <param name="variable">パターン変数名 (例: <c>"n"</c>)。</param>
    /// <param name="label">マッチ対象のラベル (省略可)。</param>
    public static NodePattern Node(string variable, string? label = null)
        => new(variable, label, null);

    /// <summary>
    /// 一つのハイパーエッジと、その役割別メンバーを同じ行に束ねる星型パターンを開始する。
    /// <see cref="HyperedgePattern.Member(string, NodePattern)"/> でロールごとにメンバーノードを
    /// 追加し、<c>g.Match(pattern)</c> に渡す。
    /// </summary>
    /// <param name="variable"><c>WHERE</c>/<c>RETURN</c> でハイパーエッジを参照する識別子。</param>
    /// <param name="type">マッチ対象のハイパーエッジ型 (省略可、null は全型)。</param>
    /// <example>
    /// <code>
    /// var pattern = GraphPattern.Hyperedge("f", "Fact")
    ///     .Member("subject", GraphPattern.Node("s", "Entity"))
    ///     .Member("object", GraphPattern.Node("o"));
    /// </code>
    /// </example>
    public static HyperedgePattern Hyperedge(string variable, string? type = null)
        => new(variable, type);

    internal static GraphPattern From(NodePattern start, string edgeType, bool outgoing, NodePattern end)
        => new(start, new EdgePattern(edgeType, outgoing), end);
}

/// <summary>Match DSL におけるノードパターン。<see cref="Out(string, NodePattern)"/> 等でエッジを伸ばせる。</summary>
public sealed class NodePattern
{
    /// <summary>パターン変数名 (例: <c>"n"</c>)。</summary>
    public string Variable { get; }

    /// <summary>マッチ対象のラベル名 (未指定なら <c>null</c>)。</summary>
    public string? Label { get; }

    private readonly GraphPattern? _parent;

    internal NodePattern(string variable, string? label, GraphPattern? parent)
    {
        Variable = variable; Label = label; _parent = parent;
    }

    /// <summary>外向 (Outgoing) のエッジで <paramref name="end"/> ノードに連結する。</summary>
    public GraphPattern Out(string edgeType, NodePattern end)
        => GraphPattern.From(this, edgeType, outgoing: true, end);

    /// <summary>型付きの外向エッジで <paramref name="end"/> ノードに連結する。</summary>
    public GraphPattern Out<TRel>(NodePattern end) where TRel : IGraphRelationship<TRel>
        => GraphPattern.From(this, TRel.GraphType, outgoing: true, end);

    /// <summary>内向 (Incoming) のエッジで <paramref name="end"/> ノードに連結する。</summary>
    public GraphPattern In(string edgeType, NodePattern end)
        => GraphPattern.From(this, edgeType, outgoing: false, end);

    /// <summary>型付きの内向エッジで <paramref name="end"/> ノードに連結する。</summary>
    public GraphPattern In<TRel>(NodePattern end) where TRel : IGraphRelationship<TRel>
        => GraphPattern.From(this, TRel.GraphType, outgoing: false, end);
}

internal sealed class EdgePattern
{
    public string Type { get; }
    public bool Outgoing { get; }
    internal EdgePattern(string type, bool outgoing) { Type = type; Outgoing = outgoing; }
}

/// <summary>
/// Match DSL の星型ハイパーエッジパターン。一つのハイパーエッジ変数と、役割ごとの
/// メンバーノードを保持する不変オブジェクト。<see cref="GraphPattern.Hyperedge(string, string?)"/>
/// を起点とし、<see cref="Member(string, NodePattern)"/> を重ねてメンバーを追加する。
/// </summary>
/// <remarks>
/// 各 <see cref="Member(string, NodePattern)"/> は新しいインスタンスを返すため、途中結果を
/// 変数に保持して分岐させても副作用は生じない。同一ロールを複数回追加すると、行はその組み合わせ
/// を放出する。
/// </remarks>
public sealed class HyperedgePattern
{
    /// <summary>ハイパーエッジのパターン変数名 (例: <c>"f"</c>)。</summary>
    public string Variable { get; }

    /// <summary>マッチ対象のハイパーエッジ型名 (未指定なら <c>null</c>)。</summary>
    public string? Type { get; }

    // 追加順を保持する。最初のメンバーは label scan の anchor になるため順序が意味を持つ。
    internal IReadOnlyList<MemberPattern> Members { get; }

    internal HyperedgePattern(string variable, string? type)
        : this(variable, type, Array.Empty<MemberPattern>())
    {
    }

    private HyperedgePattern(string variable, string? type, IReadOnlyList<MemberPattern> members)
    {
        Variable = variable; Type = type; Members = members;
    }

    /// <summary>
    /// ハイパーエッジの <paramref name="role"/> ロールを担うメンバーノードを追加した
    /// 新しいパターンを返す。複数回呼び出してメンバーを増やせる。
    /// </summary>
    /// <param name="role">メンバーが担うロール名。</param>
    /// <param name="node">マッチするメンバーノードのパターン。</param>
    public HyperedgePattern Member(string role, NodePattern node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var members = new List<MemberPattern>(Members.Count + 1);
        members.AddRange(Members);
        members.Add(new MemberPattern(role, node));
        return new HyperedgePattern(Variable, Type, members);
    }
}

/// <summary>星型パターンの 1 メンバー。ロール名とメンバーノードパターンの組。</summary>
internal sealed record MemberPattern(string Role, NodePattern Node);
