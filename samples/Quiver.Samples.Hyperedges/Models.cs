using Quiver.Api;

namespace Quiver.Samples.Hyperedges;

// KG のエンティティノード。
[Node("Entity")]
public partial class Entity
{
    [Property]
    public string Name { get; set; } = "";
}

// Quiver.Rag が取込時に作るチャンクノード (label "Chunk") を型付きで読むための宣言。
// 本文 (text) だけを写像する。
[Node("Chunk")]
public partial class Chunk
{
    [Property("text")]
    public string Text { get; set; } = "";
}

// ファクトの成立時点を表すノード。
[Node("TimePoint")]
public partial class TimePoint
{
    [Property]
    public string Date { get; set; } = "";
}

// n 項ファクト。[Role] プロパティがロール名と参照先ノード型を宣言し、
// SourceGenerator が Insert / Load / Update / Delete と型保存トラバーサル糖衣
// (FactAsSubject / Objects / OtherObjects / Source など) を生成する。
[Hyperedge("Fact")]
public partial class Fact
{
    [Role("subject")]
    public GraphNodeRef<Entity> Subject { get; set; }

    // 同一ロールの複数メンバーは IReadOnlyList で宣言する。
    [Role("object")]
    public IReadOnlyList<GraphNodeRef<Entity>> Objects { get; set; } = [];

    // 出典 (provenance) はファクトのメンバーとして構造的に付随する。
    [Role("source")]
    public GraphNodeRef<Chunk> Source { get; set; }

    // nullable = 省略可能ロール。
    [Role("asOf")]
    public GraphNodeRef<TimePoint>? AsOf { get; set; }

    [Property]
    public string Status { get; set; } = "";
}
