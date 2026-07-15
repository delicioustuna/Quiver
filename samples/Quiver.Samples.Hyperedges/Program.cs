// Quiver.Samples.Hyperedges — 第一級ハイパーエッジによる RAG n 項ファクト。
// Quiver.Rag の取込で得た出典チャンクをロール source に束ねた 4 ロールの Fact を作り、
// 同じファクトを型なし DSL / Match / 型付き API の三経路で読み戻して、
// 最後に出典チャンクの本文を回収する。
//
// 実行: dotnet run --project samples/Quiver.Samples.Hyperedges

using Quiver;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Rag;
using Quiver.Samples.Hyperedges;

string dir = Path.Combine(Path.GetTempPath(), "quiver_hyperedges_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    // subject → object のロール対を co-membership の物理ビューとして登録して開く。
    // 登録の無いロール対は incidence チェーン走査へ自動フォールバックする。
    var options = new GraphDatabaseOptions();
    options.CoMembershipRolePairs.Add(new CoMembershipRolePair("subject", "object"));
    using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"), options);

    // ── 0. Quiver.Rag で文書を取込み、出典チャンクを特定する ──
    var embedder = new HashEmbedder(dim: 16);
    var store = new RagStore(db, new RagStoreOptions
    {
        EmbeddingDimensions = embedder.Dimensions,
        Chunking = new ChunkingOptions { TargetSize = 40, Overlap = 0 },
    });
    await store.UpsertDocumentAsync(new IngestedDocument(
        "news/2026-07", "導入事例",
        new Dictionary<string, string>(),
        [
            new IngestedBlock(BlockKind.Paragraph, "Acme 社は 2026 年にグラフエンジン Quiver を導入した"),
            new IngestedBlock(BlockKind.Paragraph, "同社は全文検索とベクトル検索の併用も評価している"),
        ]), embedder);

    var searcher = new RagSearcher(store);
    var hit = searcher.Search("導入", queryVector: null,
        new RagSearchOptions { NeighborExpansion = 0 })[0];
    Console.WriteLine("── 0. 取込 → 出典チャンク (BM25 でヒットさせて NodeId を得る) ──");
    Console.WriteLine($"  {hit.ChunkText}");

    // ── 1. エンティティと 4 ロールの Fact を作成する ──
    NodeId acme, quiver, graphdb, y2026;
    HyperedgeId verifiedFact;
    using (var tx = db.BeginTransaction())
    {
        acme    = Entity.Insert(tx, new Entity { Name = "Acme" });
        quiver  = Entity.Insert(tx, new Entity { Name = "Quiver" });
        graphdb = Entity.Insert(tx, new Entity { Name = "GraphDatabase" });
        y2026   = TimePoint.Insert(tx, new TimePoint { Date = "2026" });

        // 型付き Insert: ロールの型取り違え (subject に Chunk を入れる等) はコンパイルエラーになる。
        verifiedFact = Fact.Insert(tx, new Fact
        {
            Subject = acme,
            Objects = [quiver],
            Source  = hit.ChunkNodeId,
            AsOf    = y2026,
            Status  = "verified",
        });

        // 型なし builder でも作れる。同一ロール (object) の複数メンバーを持つ 2 件目。
        var g = tx.G(db.Schema);
        g.AddHyperedge("Fact")
         .Member("subject", acme)
         .Member("object", quiver)
         .Member("object", graphdb)
         .Member("source", hit.ChunkNodeId)
         .P("Status", "draft")
         .Next();

        tx.Commit();
    }
    var names = new Dictionary<NodeId, string>
    {
        [acme] = "Acme", [quiver] = "Quiver", [graphdb] = "GraphDatabase",
        [y2026] = "2026", [hit.ChunkNodeId] = "(出典チャンク)",
    };

    // ── 2. 型なし DSL: 1 つのオペレータツリーで object と source を同時に取る ──
    Console.WriteLine();
    Console.WriteLine("── 2. 型なし DSL (Hyperedges / Members / Select<HyperedgeId>) ──");
    using (var tx = db.BeginReadOnlyTransaction())
    {
        var g = tx.G(db.Schema);
        var rows = g.Node(acme)
            .Hyperedges("Fact", role: "subject").As("fact")
            .Members("object").As("object")
            .Select<HyperedgeId>("fact")        // alias でハイパーエッジへ型検査付きで戻る
            .Members("source").As("source")
            .Select(row => (Object: row.Node("object"), Source: row.Node("source")));
        foreach (var r in rows)
            Console.WriteLine($"  Acme --[Fact]--> object={names[r.Object]}, source={names[r.Source]}");

        // 起点除外 co-membership。subject → object は登録済みロール対なので物理ビューで走る。
        var peers = g.Node(acme).Hyperedges("Fact", "subject").OtherMembers("object").ToList();
        Console.WriteLine($"  OtherMembers(object): {string.Join(", ", peers.Select(p => names[p]))}");

        // ローレベル API でも同じメンバー集合が見える。
        var members = tx.GetMembers(verifiedFact);
        while (members.MoveNext())
            Console.WriteLine($"  GetMembers: {members.Current.Role} = {names[members.Current.NodeId]}");
        members.Dispose();
    }

    // ── 3. Match: 星型パターンで 1 つのファクトと複数ロールを同じ行に束縛する ──
    Console.WriteLine();
    Console.WriteLine("── 3. Match (GraphPattern.Hyperedge 星型パターン) ──");
    using (var tx = db.BeginReadOnlyTransaction())
    {
        var g = tx.G(db.Schema);
        var rows = g.Match(
            GraphPattern.Hyperedge("f", "Fact")
                .Member("subject", GraphPattern.Node("s", "Entity"))
                .Member("object",  GraphPattern.Node("o", "Entity"))
                .Member("source",  GraphPattern.Node("src", "Chunk")))
            .Where("f", "Status", P.Eq("verified"))
            .Return(ctx => (
                Subject: ctx["s"].Get<string>("Name"),
                Object:  ctx["o"].Get<string>("Name"),
                Status:  ctx.HyperedgeGet<string>("f", "Status"),
                Source:  ctx.Node("src")))
            .ToList();
        foreach (var r in rows)
            Console.WriteLine($"  {r.Subject} --[Fact:{r.Status}]--> {r.Object} (source={names[r.Source]})");
    }

    // ── 4. 型付き API: Load と型保存トラバーサルで出典チャンクの本文を回収する ──
    Console.WriteLine();
    Console.WriteLine("── 4. 型付き API (Fact.Load / FactAsSubject / Source) ──");
    using (var tx = db.BeginReadOnlyTransaction())
    {
        var f = Fact.Load(tx, verifiedFact);
        Console.WriteLine($"  Fact.Load: subject={names[f.Subject.NodeId]}, " +
                          $"objects=[{string.Join(", ", f.Objects.Select(o => names[o.NodeId]))}], " +
                          $"status={f.Status}, asOf={(f.AsOf is null ? "-" : names[f.AsOf.Value.NodeId])}");

        // Entity → Fact → source Chunk を型を保ったまま辿り、出典本文 (grounded citation) を得る。
        var g = tx.G(db.Schema);
        var sources = g.Nodes<Entity>().Has(e => e.Name, "Acme")
            .FactAsSubject()
            .Has(fa => fa.Status, "verified")
            .Source()
            .ToList();
        foreach (var chunk in sources)
            Console.WriteLine($"  出典本文: {chunk.Text}");
    }
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

// 決定的なダミー埋め込み器。文字ヒストグラムを正規化しただけの素朴なベクトル。
// 実運用では使う埋め込みモデルに対して IChunkEmbedder を直接実装する。
sealed class HashEmbedder(int dim) : IChunkEmbedder
{
    public int Dimensions { get; } = dim;

    public ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            var v = new float[Dimensions];
            foreach (var ch in texts[i]) v[ch % Dimensions] += 1f;
            float norm = MathF.Sqrt(v.Sum(x => x * x));
            if (norm > 0) for (int d = 0; d < Dimensions; d++) v[d] /= norm;
            else v[0] = 1f;
            result[i] = v;
        }
        return ValueTask.FromResult(result);
    }
}
