using Quiver.Api;

namespace Quiver.Api.Match;

/// <summary>
/// Match DSL のグラフパターン (Vertex - エッジ - Vertex) を表す不変オブジェクト。
/// <see cref="Vertex"/> をエントリポイントとし、<see cref="VertexPattern.Out{TEdge}(VertexPattern)"/> や
/// <see cref="VertexPattern.In{TEdge}(VertexPattern)"/> を連結してパターンを構築する。
/// </summary>
public sealed class GraphPattern
{
    internal VertexPattern StartVertex { get; }
    internal EdgePattern? Edge { get; }
    internal VertexPattern? EndVertex { get; }

    private GraphPattern(VertexPattern start, EdgePattern? edge = null, VertexPattern? end = null)
    {
        StartVertex = start; Edge = edge; EndVertex = end;
    }

    /// <summary>新しいVertexパターンを開始する。<paramref name="variable"/> は <c>WHERE</c>/<c>RETURN</c> で参照する識別子。</summary>
    /// <param name="variable">パターン変数名 (例: <c>"n"</c>)。</param>
    /// <param name="label">マッチ対象のラベル (省略可)。</param>
    public static VertexPattern Vertex(string variable, string? label = null)
        => new(variable, label, null);

    /// <summary>
    /// 一つのNexusと、その役割別メンバーを同じ行に束ねる星型パターンを開始する。
    /// <see cref="NexusPattern.Member(string, VertexPattern)"/> でロールごとにメンバーVertexを
    /// 追加し、<c>g.Match(pattern)</c> に渡す。
    /// </summary>
    /// <param name="variable"><c>WHERE</c>/<c>RETURN</c> でNexusを参照する識別子。</param>
    /// <param name="type">マッチ対象のNexus型 (省略可、null は全型)。</param>
    /// <example>
    /// <code>
    /// var pattern = GraphPattern.Nexus("f", "Fact")
    ///     .Member("subject", GraphPattern.Vertex("s", "Entity"))
    ///     .Member("object", GraphPattern.Vertex("o"));
    /// </code>
    /// </example>
    public static NexusPattern Nexus(string variable, string? type = null)
        => new(variable, type);

    internal static GraphPattern From(VertexPattern start, string edgeType, bool outgoing, VertexPattern end)
        => new(start, new EdgePattern(edgeType, outgoing), end);
}

/// <summary>Match DSL におけるVertexパターン。<see cref="Out(string, VertexPattern)"/> 等でエッジを伸ばせる。</summary>
public sealed class VertexPattern
{
    /// <summary>パターン変数名 (例: <c>"n"</c>)。</summary>
    public string Variable { get; }

    /// <summary>マッチ対象のラベル名 (未指定なら <c>null</c>)。</summary>
    public string? Label { get; }

    private readonly GraphPattern? _parent;

    internal VertexPattern(string variable, string? label, GraphPattern? parent)
    {
        Variable = variable; Label = label; _parent = parent;
    }

    /// <summary>外向 (Outgoing) のエッジで <paramref name="end"/> Vertexに連結する。</summary>
    public GraphPattern Out(string edgeType, VertexPattern end)
        => GraphPattern.From(this, edgeType, outgoing: true, end);

    /// <summary>型付きの外向エッジで <paramref name="end"/> Vertexに連結する。</summary>
    public GraphPattern Out<TEdge>(VertexPattern end) where TEdge : global::Quiver.IGraphEdgeEntity<TEdge>
        => GraphPattern.From(this, TEdge.GraphType, outgoing: true, end);

    /// <summary>内向 (Incoming) のエッジで <paramref name="end"/> Vertexに連結する。</summary>
    public GraphPattern In(string edgeType, VertexPattern end)
        => GraphPattern.From(this, edgeType, outgoing: false, end);

    /// <summary>型付きの内向エッジで <paramref name="end"/> Vertexに連結する。</summary>
    public GraphPattern In<TEdge>(VertexPattern end) where TEdge : global::Quiver.IGraphEdgeEntity<TEdge>
        => GraphPattern.From(this, TEdge.GraphType, outgoing: false, end);
}

internal sealed class EdgePattern
{
    public string Type { get; }
    public bool Outgoing { get; }
    internal EdgePattern(string type, bool outgoing) { Type = type; Outgoing = outgoing; }
}

/// <summary>
/// Match DSL の星型Nexusパターン。一つのNexus変数と、役割ごとの
/// メンバーVertexを保持する不変オブジェクト。<see cref="GraphPattern.Nexus(string, string?)"/>
/// を起点とし、<see cref="Member(string, VertexPattern)"/> を重ねてメンバーを追加する。
/// </summary>
/// <remarks>
/// 各 <see cref="Member(string, VertexPattern)"/> は新しいインスタンスを返すため、途中結果を
/// 変数に保持して分岐させても副作用は生じない。同一ロールを複数回追加すると、行はその組み合わせ
/// を放出する。
/// </remarks>
public sealed class NexusPattern
{
    /// <summary>Nexusのパターン変数名 (例: <c>"f"</c>)。</summary>
    public string Variable { get; }

    /// <summary>マッチ対象のNexus型名 (未指定なら <c>null</c>)。</summary>
    public string? Type { get; }

    // 追加順を保持する。最初のメンバーは label scan の anchor になるため順序が意味を持つ。
    internal IReadOnlyList<MemberPattern> Members { get; }

    internal NexusPattern(string variable, string? type)
        : this(variable, type, Array.Empty<MemberPattern>())
    {
    }

    private NexusPattern(string variable, string? type, IReadOnlyList<MemberPattern> members)
    {
        Variable = variable; Type = type; Members = members;
    }

    /// <summary>
    /// Nexusの <paramref name="role"/> ロールを担うメンバーVertexを追加した
    /// 新しいパターンを返す。複数回呼び出してメンバーを増やせる。
    /// </summary>
    /// <param name="role">メンバーが担うロール名。</param>
    /// <param name="vertex">マッチするメンバーVertexのパターン。</param>
    public NexusPattern Member(string role, VertexPattern vertex)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        var members = new List<MemberPattern>(Members.Count + 1);
        members.AddRange(Members);
        members.Add(new MemberPattern(role, vertex));
        return new NexusPattern(Variable, Type, members);
    }
}

/// <summary>星型パターンの 1 メンバー。ロール名とメンバーVertexパターンの組。</summary>
internal sealed record MemberPattern(string Role, VertexPattern Vertex);
