using Quiver.Api;

namespace Quiver.Samples.Nexuses;

// KG のエンティティVertex。
[Vertex("Entity")]
public partial class Entity
{
    [Property]
    public string Name { get; set; } = "";
}

// Quiver.Rag が取込時に作るチャンクVertex (label "Chunk") を型付きで読むための宣言。
// 本文 (text) だけを写像する。
[Vertex("Chunk")]
public partial class Chunk
{
    [Property("text")]
    public string Text { get; set; } = "";
}

// ファクトの成立時点を表すVertex。
[Vertex("TimePoint")]
public partial class TimePoint
{
    [Property]
    public string Date { get; set; } = "";
}

// n 項ファクト。[Role] プロパティがロール名と参照先Vertex型を宣言し、
// SourceGenerator が Insert / Load / Update / Delete と型保存トラバーサル糖衣
// (FactAsSubject / Objects / OtherObjects / Source など) を生成する。
[Nexus("Fact")]
public partial class Fact
{
    [Role("subject")]
    public GraphVertexRef<Entity> Subject { get; set; }

    // 同一ロールの複数メンバーは IReadOnlyList で宣言する。
    [Role("object")]
    public IReadOnlyList<GraphVertexRef<Entity>> Objects { get; set; } = [];

    // 出典 (provenance) はファクトのメンバーとして構造的に付随する。
    [Role("source")]
    public GraphVertexRef<Chunk> Source { get; set; }

    // nullable = 省略可能ロール。
    [Role("asOf")]
    public GraphVertexRef<TimePoint>? AsOf { get; set; }

    [Property]
    public string Status { get; set; } = "";
}
