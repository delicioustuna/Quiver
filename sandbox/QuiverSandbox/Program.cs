using Quiver;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;

string baseDir = Path.Combine(Path.GetTempPath(), "quiver_sandbox_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    Demo1_BasicCrud(Path.Combine(baseDir, "01_crud"));
    Demo2_AllPropertyTypes(Path.Combine(baseDir, "02_props"));
    Demo3_OperatorPipeline(Path.Combine(baseDir, "03_pipeline"));
    Demo4_ExpandOperator(Path.Combine(baseDir, "04_expand"));
    Demo5_IndexSearch(Path.Combine(baseDir, "05_index"));
    Demo6_Persistence(Path.Combine(baseDir, "06_persist"));
    Demo7_Diagnostics(Path.Combine(baseDir, "07_diag"));
    Demo8_SourceGenCrud(Path.Combine(baseDir, "08_sourcegen"));
    Demo9_GremlinAndMatch(Path.Combine(baseDir, "09_gremlin"));
}
finally
{
    if (Directory.Exists(baseDir))
        Directory.Delete(baseDir, recursive: true);
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo 1: ノード・リレーションの基本 CRUD
// ─────────────────────────────────────────────────────────────────────────────
static void Demo1_BasicCrud(string dir)
{
    H("Demo 1: 基本 CRUD — ソーシャルグラフ");
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
    using var tx = db.BeginWriteTransaction();

    // ── ノード作成 & プロパティ設定 ──────────────────────────────────────
    var alice = tx.CreateVertex("Person");
    var bob   = tx.CreateVertex("Person");
    var carol = tx.CreateVertex("Person");

    tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
    tx.SetProperty(bob,   "name", PropertyValue.FromString("Bob"));
    tx.SetProperty(carol, "name", PropertyValue.FromString("Carol"));
    tx.SetProperty(alice, "age",  PropertyValue.FromInt32(30));
    tx.SetProperty(bob,   "age",  PropertyValue.FromInt32(25));

    Console.WriteLine($"  alice exists : {tx.VertexExists(alice)}");
    Console.WriteLine($"  alice.name   = {Str(tx.GetProperty(alice, "name"))}");
    Console.WriteLine($"  alice.age    = {tx.GetProperty(alice, "age").Int32Value}");
    Console.WriteLine($"  alice has email: {tx.HasProperty(alice, "email")}");

    // ── リレーション作成 & 隣接ノード列挙 ──────────────────────────────
    tx.CreateEdge(alice, bob,   "KNOWS");
    tx.CreateEdge(alice, carol, "KNOWS");
    tx.CreateEdge(bob,   carol, "FOLLOWS");

    Console.WriteLine("  alice の隣接ノード (Direction.Both):");
    var en = tx.EnumerateEdges(alice);
    while (en.MoveNext())
    {
        var r = en.Current;
        var neighbor = r.Source == alice ? r.Target : r.Source;
        Console.WriteLine($"    → {Str(tx.GetProperty(neighbor, "name"))}");
    }

    // ── プロパティ削除 ────────────────────────────────────────────────
    tx.RemoveProperty(alice, "age");
    Console.WriteLine($"  age 削除後: HasProperty(age)={tx.HasProperty(alice, "age")}");

    // ── リレーション削除 ──────────────────────────────────────────────
    var relEn = tx.EnumerateEdges(alice, Direction.Both, "KNOWS");
    if (relEn.MoveNext())
        tx.DeleteEdge(relEn.Current.Id);

    int remaining = 0;
    var cntEn = tx.EnumerateEdges(alice);
    while (cntEn.MoveNext()) remaining++;
    Console.WriteLine($"  KNOWS 1件削除後のリレーション数: {remaining}");

    tx.Commit();
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo 2: プロパティ各型 (bool / int32 / int64 / double / string)
// ─────────────────────────────────────────────────────────────────────────────
static void Demo2_AllPropertyTypes(string dir)
{
    H("Demo 2: プロパティ各型");
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
    using var tx = db.BeginWriteTransaction();

    var n = tx.CreateVertex("Item");

    tx.SetProperty(n, "flag",   PropertyValue.FromBool(true));
    tx.SetProperty(n, "count",  PropertyValue.FromInt32(42));
    tx.SetProperty(n, "score",  PropertyValue.FromInt64(9_000_000_000L));
    tx.SetProperty(n, "price",  PropertyValue.FromDouble(3.14159));
    tx.SetProperty(n, "label",  PropertyValue.FromString("hello, graph!"));

    Console.WriteLine($"  bool   : {tx.GetProperty(n, "flag").BoolValue}");
    Console.WriteLine($"  int32  : {tx.GetProperty(n, "count").Int32Value}");
    Console.WriteLine($"  int64  : {tx.GetProperty(n, "score").Int64Value}");
    Console.WriteLine($"  double : {tx.GetProperty(n, "price").DoubleValue:F5}");
    Console.WriteLine($"  string : {Str(tx.GetProperty(n, "label"))}");

    // 上書き
    tx.SetProperty(n, "count", PropertyValue.FromInt32(99));
    Console.WriteLine($"  count (上書き後): {tx.GetProperty(n, "count").Int32Value}");

    // プロパティ一覧 (KeyId と型を表示)
    Console.WriteLine("  プロパティ一覧:");
    var pe = tx.EnumerateProperties(n);
    while (pe.MoveNext())
    {
        var p = pe.Current;
        Console.WriteLine($"    keyId={p.KeyId.Value}  type={p.Value.Type}");
    }

    tx.Rollback();
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo 3: Volcano オペレータパイプライン
//   AllVerticesScan / VertexByLabelScan → PropertyLookup → Filter → Limit
// ─────────────────────────────────────────────────────────────────────────────
static void Demo3_OperatorPipeline(string dir)
{
    H("Demo 3: Volcano オペレータパイプライン");
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
    using var tx = db.BeginWriteTransaction();

    for (int i = 0; i < 10; i++)
    {
        var p = tx.CreateVertex("Product");
        tx.SetProperty(p, "name",  PropertyValue.FromString($"Product-{i:D2}"));
        tx.SetProperty(p, "price", PropertyValue.FromInt64(100L * (i + 1)));
    }

    var nameKey = tx.EditSchema.GetOrCreatePropertyKey("name");
    var priceKey = tx.EditSchema.GetOrCreatePropertyKey("price");
    var prodLabel = tx.EditSchema.GetOrCreateLabel("Product");

    // (A) VertexByLabelScan → Limit(3)  ─ ラベルフィルタ + ページング
    Console.WriteLine("  Product 先頭 3 件 (vertexId):");
    {
        var scan  = new VertexByLabelScanOperator(prodLabel);
        var limit = new LimitOperator(scan, limit: 3);
        using var r = tx.Execute(limit);
        foreach (var row in r.Rows())
            Console.WriteLine($"    vertexId={row.GetVertexId(0).Value}");
    }

    // (B) AllVerticesScan → PropertyLookup(name) → Limit(skip:2, limit:3)  ─ SKIP/TAKE
    Console.WriteLine("  全 Product の name を SKIP 2, TAKE 3:");
    {
        var scan   = new AllVerticesScanOperator(prodLabel);
        var lookup = new PropertyLookupOperator(scan, 0, nameKey, "name");
        var limit  = new LimitOperator(lookup, limit: 3, skip: 2);
        using var r = tx.Execute(limit);
        foreach (var row in r.Rows())
            Console.WriteLine($"    {row.GetString(1)}");
    }

    // (C) AllVerticesScan → PropertyLookup(price) → Filter(price >= 600) → PropertyLookup(name)
    Console.WriteLine("  price ≥ 600 の Product:");
    {
        var scan    = new AllVerticesScanOperator(prodLabel);
        var lookupP = new PropertyLookupOperator(scan,    0, priceKey, "price");
        var filter  = new FilterOperator(lookupP, new Int64MinPredicate(column: 1, min: 600L));
        var lookupN = new PropertyLookupOperator(filter,  0, nameKey,  "name");
        using var r = tx.Execute(lookupN);
        foreach (var row in r.Rows())
            Console.WriteLine($"    {row.GetString(2)}  (price={row.GetInt64(1)})");
        Console.WriteLine($"  統計: RowsProduced={r.Statistics.RowsProduced}");
    }

    tx.Rollback();
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo 4: ExpandOperator — グラフパターンマッチング
// ─────────────────────────────────────────────────────────────────────────────
static void Demo4_ExpandOperator(string dir)
{
    H("Demo 4: ExpandOperator — グラフパターンマッチング");
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
    using var tx = db.BeginWriteTransaction();

    // グラフ構築: Alice→Bob→Dave, Alice→Carol→Dave (KNOWS), Carol→Dave (FOLLOWS)
    string[] names = ["Alice", "Bob", "Carol", "Dave"];
    var vertices = names.Select(name =>
    {
        var id = tx.CreateVertex("Person");
        tx.SetProperty(id, "name", PropertyValue.FromString(name));
        return id;
    }).ToArray();

    tx.CreateEdge(vertices[0], vertices[1], "KNOWS");   // Alice→Bob
    tx.CreateEdge(vertices[0], vertices[2], "KNOWS");   // Alice→Carol
    tx.CreateEdge(vertices[1], vertices[3], "KNOWS");   // Bob→Dave
    tx.CreateEdge(vertices[2], vertices[3], "FOLLOWS"); // Carol→Dave

    var personLabel = tx.EditSchema.GetOrCreateLabel("Person");
    var knowsType = tx.EditSchema.GetOrCreateEdgeType("KNOWS");
    var nameKey = tx.EditSchema.GetOrCreatePropertyKey("name");

    // (A) Outgoing KNOWS の隣接ノード (NeighborOnly モード)
    Console.WriteLine("  Outgoing KNOWS の隣接ノード:");
    {
        var scan   = new VertexByLabelScanOperator(personLabel);
        var expand = new ExpandOperator(scan, 0, Direction.Outgoing, knowsType, ExpandOutputMode.NeighborOnly);
        using var r = tx.Execute(expand);
        foreach (var row in r.Rows())
            Console.WriteLine($"    → {Str(tx.GetProperty(row.GetVertexId(0), "name"))}");
    }

    // (B) 全エッジを source / rel / neighbor として取得 (Full モード)
    Console.WriteLine("  全 Outgoing エッジ (Full モード):");
    {
        var scan   = new VertexByLabelScanOperator(personLabel);
        var expand = new ExpandOperator(scan, 0, Direction.Outgoing, null, ExpandOutputMode.Full);
        using var r = tx.Execute(expand);
        foreach (var row in r.Rows())
        {
            var src = Str(tx.GetProperty(row.GetVertexId(0), "name"));
            var nbr = Str(tx.GetProperty(row.GetVertexId(2), "name"));
            Console.WriteLine($"    {src} -[rel#{row.GetEdgeId(1).Value}]-> {nbr}");
        }
    }

    tx.Rollback();
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo 5: インデックス検索 (等値 Seek / 範囲 Range)
// ─────────────────────────────────────────────────────────────────────────────
static void Demo5_IndexSearch(string dir)
{
    H("Demo 5: インデックス検索 (Seek / Range)");
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));

    EditSchema(db, schema =>
    {
        schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality));
        schema.CreateIndex(new ScalarIndexDefinition("idx_score", new PropertyTarget(PropertyOwnerKind.Vertex, "score", "Person"), IndexKind.Int64Equality));
    });

    using (var tx = db.BeginWriteTransaction())
    {
        (string name, long score)[] data =
        [
            ("Alice", 100), ("Bob", 200), ("Carol", 150), ("Dave", 300), ("Eve", 200),
        ];
        foreach (var (name, score) in data)
        {
            var vertex = tx.CreateVertex("Person");
            tx.SetProperty(vertex, "name",  PropertyValue.FromString(name));
            tx.SetProperty(vertex, "score", PropertyValue.FromInt64(score));
        }
        tx.Commit();
    }

    using (var tx = db.BeginWriteTransaction())
    {
        var nameKey = tx.EditSchema.GetOrCreatePropertyKey("name");

        // 等値検索: name == "Carol"
        Console.WriteLine("  idx_name で \"Carol\" を等値検索:");
        {
            var definition = tx.Schema.ListIndexes()
                .Single(index => index.Name == "idx_name")
                .Definition;
            tx.Schema.TryGetLabelId("Person", out var personLabel);
            var seek = new VertexIndexSeekOperator(
                definition,
                nameKey,
                personLabel,
                LiteralProvider.String("Carol"));
            var lookup = new PropertyLookupOperator(seek, 0, nameKey, "name");
            using var r = tx.Execute(lookup);
            foreach (var row in r.Rows())
                Console.WriteLine($"    → {row.GetString(1)}");
        }

        // 範囲検索: 150 ≤ score ≤ 250
        Console.WriteLine("  idx_score で 150 ≤ score ≤ 250 の範囲検索:");
        {
            var definition = tx.Schema.ListIndexes()
                .Single(index => index.Name == "idx_score")
                .Definition;
            tx.Schema.TryGetPropertyKeyId("score", out var scoreKey);
            tx.Schema.TryGetLabelId("Person", out var personLabel);
            var range = new VertexIndexRangeScanOperator(
                definition,
                scoreKey,
                personLabel,
                LiteralProvider.Int64(150),
                fromInclusive: true,
                LiteralProvider.Int64(250),
                toInclusive: true);
            var lookup = new PropertyLookupOperator(range, 0, nameKey, "name");
            using var r = tx.Execute(lookup);
            foreach (var row in r.Rows())
                Console.WriteLine($"    → {row.GetString(1)}");
        }

        Console.WriteLine("  登録済みインデックス:");
        foreach (var idx in db.Schema.ListIndexes())
            Console.WriteLine($"    {idx.Name}");

        tx.Rollback();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo 6: 永続性 — DB 再オープン後もデータが残る
// ─────────────────────────────────────────────────────────────────────────────
static void Demo6_Persistence(string dir)
{
    H("Demo 6: 永続性 — DB 再オープン後もデータが残る");

    VertexId savedId;

    // 書き込み & クローズ
    using (var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
    {
        using var tx = db.BeginWriteTransaction();
        savedId = tx.CreateVertex("Config");
        tx.SetProperty(savedId, "version", PropertyValue.FromString("1.0"));
        tx.SetProperty(savedId, "build",   PropertyValue.FromInt64(42L));
        tx.Commit();
        Console.WriteLine($"  書き込み完了: vertexId={savedId.Value}, version=1.0, build=42");
    }

    // 再オープン & 読み出し
    using (var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
    {
        using var tx = db.BeginWriteTransaction();
        Console.WriteLine($"  再オープン後: VertexExists={tx.VertexExists(savedId)}");
        Console.WriteLine($"  version = {Str(tx.GetProperty(savedId, "version"))}");
        Console.WriteLine($"  build   = {tx.GetProperty(savedId, "build").Int64Value}");
        tx.Rollback();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo 7: 診断 API (GetStatistics / CheckConsistency)
// ─────────────────────────────────────────────────────────────────────────────
static void Demo7_Diagnostics(string dir)
{
    H("Demo 7: 診断 API");
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));

    using (var tx = db.BeginWriteTransaction())
    {
        for (int i = 0; i < 5; i++)
        {
            var a = tx.CreateVertex("N");
            var b = tx.CreateVertex("N");
            tx.SetProperty(a, "idx", PropertyValue.FromInt32(i));
            tx.CreateEdge(a, b, "LINK");
        }
        tx.Commit();
    }

    var stats = db.Diagnostics.GetStatistics();
    Console.WriteLine($"  VertexCount         = {stats.VertexCount}");
    Console.WriteLine($"  EdgeCount = {stats.EdgeCount}");
    Console.WriteLine($"  DataFileSize      = {stats.DataFileSize / 1024.0:F1} KB");
    Console.WriteLine($"  WalFileSize       = {stats.WalFileSize  / 1024.0:F1} KB");

    var report = db.Diagnostics.CheckConsistency();
    Console.WriteLine($"  IsConsistent      = {report.IsConsistent}");
    foreach (var issue in report.Issues)
        Console.WriteLine($"  Issue: {issue}");
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo 8: SourceGenerator CRUD  ([Vertex] / [Property] / [Indexed])
// ─────────────────────────────────────────────────────────────────────────────
static void Demo8_SourceGenCrud(string dir)
{
    H("Demo 8: SourceGenerator CRUD + g.Insert / g.Load / g.Update / g.Delete");
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
    EditSchema(db, schema => schema.CreateIndex(
        new ScalarIndexDefinition("idx_person_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));

    using var tx = db.BeginWriteTransaction();
    var g = tx.Query;
    var m = tx.Mutate;

    // SourceGen 経由で、エンティティと索引を同じトランザクションに反映する。
    var aliceId = m.InsertIndexed(new Person { Name = "Alice", Age = 30 });
    var bobId   = m.InsertIndexed(new Person { Name = "Bob",   Age = 25 });
    tx.CreateEdge(aliceId, bobId, "KNOWS");
    Console.WriteLine($"  Inserted Alice (vertexId={aliceId.Value}) and Bob (vertexId={bobId.Value})");

    // g.Load<T>
    var alice = g.Load<Person>(aliceId);
    Console.WriteLine($"  Loaded Alice: Name={alice.Name}, Age={alice.Age}");

    // g.Update<T>
    alice.Age = 31;
    m.Update(aliceId, alice);
    Console.WriteLine($"  After Update: Age={g.Load<Person>(aliceId).Age}");

    // FindByName — SourceGen 生成の検索メソッド
    var found = Person.FindByName(tx, "Alice");
    Console.WriteLine($"  FindByName(\"Alice\"): {found.Count} 件, Age={found[0].Entity.Age}");

    // g.Delete<T>
    m.Delete<Person>(bobId);
    Console.WriteLine($"  Bob deleted: VertexExists={tx.VertexExists(bobId)}");

    tx.Commit();
}

// ─────────────────────────────────────────────────────────────────────────────
// Demo 9: Gremlin ライク API + Match DSL
// ─────────────────────────────────────────────────────────────────────────────
static void Demo9_GremlinAndMatch(string dir)
{
    H("Demo 9: Gremlin ライク API + Match DSL");
    using var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"));
    using var tx = db.BeginWriteTransaction();

    var g = tx.Query;
    var m = tx.Mutate;

    // ── 書き込み: 命名統一後の API ──────────────────────────────────────
    var alice = m.AddVertex("Person").P("Name", "Alice").P("Age", 30).Next();
    var bob   = m.AddVertex("Person").P("Name", "Bob").P("Age", 25).Next();
    var carol = m.AddVertex("Person").P("Name", "Carol").P("Age", 35).Next();
    m.AddEdge("KNOWS").From(alice).To(bob).Next();
    m.AddEdge("KNOWS").From(alice).To(carol).Next();
    m.AddEdge("FOLLOWS").From(bob).To(carol).Next();
    Console.WriteLine($"  Created vertices: alice={alice.Value}, bob={bob.Value}, carol={carol.Value}");

    // ── 型なしトラバーサル ─────────────────────────────────────────────
    var names = g.Vertices().HasLabel("Person").Has("Age", P.Gt(25L)).Values("Name").ToList();
    Console.WriteLine($"  Age > 25 の Person: [{string.Join(", ", names)}]");

    var aliceFriends = g.Vertex(alice).Out("KNOWS").Values("Name").ToList();
    Console.WriteLine($"  Alice の KNOWS 先: [{string.Join(", ", aliceFriends)}]");

    var knowsCount = g.Vertices().HasLabel("Person").OutEdges("KNOWS").Count();
    Console.WriteLine($"  KNOWS エッジ数: {knowsCount}");

    // ── 型付きトラバーサル g.Vertices<T>() + expression-based Has ──────────────
    var people = g.Vertices<Person>()
                  .Has(p => p.Age, P.Gt(25L))
                  .ToList();
    Console.WriteLine($"  V<Person>().Has(p=>p.Age, Gt(25)): [{string.Join(", ", people.Select(p => p.Name))}]");

    var peopleWithIds = g.Vertices<Person>()
                         .Has(p => p.Name, "Alice")
                         .ToListWithIds();
    Console.WriteLine($"  V<Person>().Has(p=>p.Name,\"Alice\"): id={peopleWithIds[0].Id.Value}, name={peopleWithIds[0].Entity.Name}");

    // ── Match DSL ─────────────────────────────────────────────────────
    var results = g.Match(
        GraphPattern.Vertex("n", "Person")
                    .Out("KNOWS", GraphPattern.Vertex("m", "Person"))
    )
    .Where("n", "Age", P.Gt(25L))
    .Return(v => new
    {
        PersonName = v["n"].Get<string>("Name"),
        FriendName = v["m"].Get<string>("Name"),
    })
    .ToList();

    Console.WriteLine("  Match DSL (n:Person)-[:KNOWS]->(m:Person) WHERE n.Age > 25:");
    foreach (var row in results)
        Console.WriteLine($"    {row.PersonName} → {row.FriendName}");

    tx.Commit();
}

// ─────────────────────────────────────────────────────────────────────────────
// 共通ヘルパー
// ─────────────────────────────────────────────────────────────────────────────
static void H(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('─', 56));
    Console.WriteLine($"  {title}");
    Console.WriteLine(new string('─', 56));
}

static string Str(PropertyValue v) =>
    System.Text.Encoding.UTF8.GetString(v.Utf8StringValue);

static void EditSchema(QuiverDatabase db, Action<ISchemaEditor> edit)
{
    using var tx = db.BeginWriteTransaction();
    edit(tx.EditSchema);
    tx.Commit();
}

// ─────────────────────────────────────────────────────────────────────────────
// カスタム演算子
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>指定列の Int64 値が min 以上かを判定する述語。</summary>
sealed class Int64MinPredicate(int column, long min) : IPredicate
{
    public bool Evaluate(in TupleRef tuple, ITransaction tx) =>
        tuple[column].Type == TupleSlotType.Int64 && tuple[column].LongValue >= min;
}
