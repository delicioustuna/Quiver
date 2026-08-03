using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

var mode = args.Length > 0 ? args[0] : "movie";
var pathArg = args.Length > 1 ? args[1] : null;

switch (mode)
{
    case "--portability-produce":
        ProducePortabilityBundle(
            pathArg ?? throw new ArgumentException("bundle path is required"),
            args.Length > 2 ? args[2] : "unknown");
        break;
    case "--portability-verify-update":
        VerifyAndUpdatePortabilityBundle(
            pathArg ?? throw new ArgumentException("bundle path is required"));
        break;
    case "--portability-verify":
        VerifyPortabilityBundle(
            pathArg ?? throw new ArgumentException("bundle path is required"),
            expectedStage: 1);
        break;
    case "--hierarchical":
        GenerateHierarchical(pathArg ?? Path.Combine(AppContext.BaseDirectory, "sample2.quiver"));
        break;
    case "--vector":
        GenerateVector(pathArg ?? Path.Combine(AppContext.BaseDirectory, "sample3.quiver"));
        break;
    default:
        GenerateMovie(pathArg ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sample", "movie.quiver"));
        break;
}

static void PrepareFile(string outputPath)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
    Directory.CreateDirectory(dir);
    if (File.Exists(outputPath)) File.Delete(outputPath);
    var walPath = outputPath + "-wal";
    if (File.Exists(walPath)) File.Delete(walPath);
    Console.WriteLine($"Generating sample database: {Path.GetFullPath(outputPath)}");
}

static void PrintStats(QuiverDatabase db)
{
    var stats = db.Diagnostics.GetStatistics();
    Console.WriteLine($"  Vertices:         {stats.VertexCount}");
    Console.WriteLine($"  Edges: {stats.EdgeCount}");
    Console.WriteLine($"  Properties:    {stats.PropertyCount}");
    Console.WriteLine($"  Labels:        [{string.Join(", ", db.Schema.ListLabels())}]");
    Console.WriteLine($"  Rel Types:     [{string.Join(", ", db.Schema.ListEdgeTypes())}]");
    Console.WriteLine($"  Prop Keys:     [{string.Join(", ", db.Schema.ListPropertyKeys())}]");
    Console.WriteLine($"  Indexes:       {db.Schema.ListIndexes().Count}");
    Console.WriteLine($"  FT Indexes:    {db.Schema.ListIndexes().Count(i => i.Definition is FullTextIndexDefinition)}");
    Console.WriteLine("Done.");
}

static void EditSchema(QuiverDatabase db, Action<ISchemaEditor> edit)
{
    using var tx = db.BeginWriteTransaction();
    edit(tx.EditSchema);
    tx.Commit();
}

static void GenerateMovie(string outputPath)
{
    PrepareFile(outputPath);
    using var db = QuiverDatabase.Open(outputPath);

    EditSchema(db, schema =>
    {
        schema.CreateIndex(new ScalarIndexDefinition("idx_person_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality));
        schema.CreateIndex(new ScalarIndexDefinition("idx_movie_year", new PropertyTarget(PropertyOwnerKind.Vertex, "year", "Movie"), IndexKind.Int64Equality));
        schema.CreateIndex(new FullTextIndexDefinition("ft_movie_title", new PropertyTarget(PropertyOwnerKind.Vertex, "title", "Movie")));
    });

    using (var tx = db.BeginWriteTransaction())
    {
        var g = tx.Query;

        var keanu = tx.Mutate.AddVertex("Person").P("name", "Keanu Reeves").P("born", 1964L).Next();
        var carrie = tx.Mutate.AddVertex("Person").P("name", "Carrie-Anne Moss").P("born", 1967L).Next();
        var hugo = tx.Mutate.AddVertex("Person").P("name", "Hugo Weaving").P("born", 1960L).Next();
        var laurence = tx.Mutate.AddVertex("Person").P("name", "Laurence Fishburne").P("born", 1961L).Next();
        var lana = tx.Mutate.AddVertex("Person").P("name", "Lana Wachowski").P("born", 1965L).Next();
        var lilly = tx.Mutate.AddVertex("Person").P("name", "Lilly Wachowski").P("born", 1967L).Next();
        var tom = tx.Mutate.AddVertex("Person").P("name", "Tom Hanks").P("born", 1956L).Next();
        var robert = tx.Mutate.AddVertex("Person").P("name", "Robert Zemeckis").P("born", 1952L).Next();

        var matrix = tx.Mutate.AddVertex("Movie").P("title", "The Matrix").P("year", 1999L).P("tagline", "Welcome to the Real World").Next();
        var reloaded = tx.Mutate.AddVertex("Movie").P("title", "The Matrix Reloaded").P("year", 2003L).Next();
        var forrest = tx.Mutate.AddVertex("Movie").P("title", "Forrest Gump").P("year", 1994L).P("tagline", "Life is like a box of chocolates").Next();

        tx.Mutate.AddEdge("ACTED_IN").From(keanu).To(matrix).P("role", "Neo").Next();
        tx.Mutate.AddEdge("ACTED_IN").From(carrie).To(matrix).P("role", "Trinity").Next();
        tx.Mutate.AddEdge("ACTED_IN").From(hugo).To(matrix).P("role", "Agent Smith").Next();
        tx.Mutate.AddEdge("ACTED_IN").From(laurence).To(matrix).P("role", "Morpheus").Next();
        tx.Mutate.AddEdge("ACTED_IN").From(keanu).To(reloaded).P("role", "Neo").Next();
        tx.Mutate.AddEdge("ACTED_IN").From(carrie).To(reloaded).P("role", "Trinity").Next();
        tx.Mutate.AddEdge("ACTED_IN").From(hugo).To(reloaded).P("role", "Agent Smith").Next();
        tx.Mutate.AddEdge("ACTED_IN").From(tom).To(forrest).P("role", "Forrest Gump").Next();

        tx.Mutate.AddEdge("DIRECTED").From(lana).To(matrix).Next();
        tx.Mutate.AddEdge("DIRECTED").From(lilly).To(matrix).Next();
        tx.Mutate.AddEdge("DIRECTED").From(lana).To(reloaded).Next();
        tx.Mutate.AddEdge("DIRECTED").From(lilly).To(reloaded).Next();
        tx.Mutate.AddEdge("DIRECTED").From(robert).To(forrest).Next();

        tx.Mutate.AddEdge("KNOWS").From(keanu).To(carrie).Next();
        tx.Mutate.AddEdge("KNOWS").From(keanu).To(hugo).Next();

        tx.Commit();
    }

    PrintStats(db);
}

static void GenerateVector(string outputPath)
{
    PrepareFile(outputPath);
    using var db = QuiverDatabase.Open(outputPath);

    EditSchema(db, schema =>
    {
        schema.CreateIndex(
            new ScalarIndexDefinition("idx_doc_title", new PropertyTarget(PropertyOwnerKind.Vertex, "title", "Document"), IndexKind.StringEquality));
        schema.CreateIndex(
            new VectorIndexDefinition("vec_doc", new PropertyTarget(PropertyOwnerKind.Vertex, "embedding", "Document"), 8));
    });

    var rng = new Random(42);

    using (var tx = db.BeginWriteTransaction())
    {
        var g = tx.Query;

        var topics = new[]
        {
            ("Intro to Graph Databases",     "database",   new float[] { 0.9f, 0.8f, 0.1f, 0.2f, 0.0f, 0.1f, 0.3f, 0.1f }),
            ("Property Graph Model",         "database",   new float[] { 0.85f, 0.7f, 0.15f, 0.25f, 0.05f, 0.1f, 0.2f, 0.15f }),
            ("SQL vs Graph Queries",         "database",   new float[] { 0.7f, 0.6f, 0.3f, 0.2f, 0.1f, 0.05f, 0.25f, 0.1f }),
            ("Vector Similarity Search",     "vector",     new float[] { 0.1f, 0.2f, 0.9f, 0.85f, 0.1f, 0.05f, 0.1f, 0.3f }),
            ("HNSW Algorithm Explained",     "vector",     new float[] { 0.15f, 0.1f, 0.85f, 0.9f, 0.05f, 0.1f, 0.15f, 0.25f }),
            ("Embeddings for RAG",           "vector",     new float[] { 0.2f, 0.3f, 0.8f, 0.7f, 0.15f, 0.1f, 0.2f, 0.35f }),
            ("Full-Text Search Internals",   "search",     new float[] { 0.3f, 0.4f, 0.2f, 0.1f, 0.8f, 0.7f, 0.1f, 0.05f }),
            ("BM25 Scoring",                 "search",     new float[] { 0.25f, 0.35f, 0.15f, 0.1f, 0.85f, 0.75f, 0.15f, 0.1f }),
            ("Transaction Isolation",        "internals",  new float[] { 0.5f, 0.3f, 0.1f, 0.1f, 0.1f, 0.1f, 0.9f, 0.8f }),
            ("WAL and Recovery",             "internals",  new float[] { 0.4f, 0.25f, 0.15f, 0.05f, 0.1f, 0.15f, 0.85f, 0.9f }),
            ("Query Optimization",           "query",      new float[] { 0.6f, 0.5f, 0.2f, 0.1f, 0.3f, 0.2f, 0.4f, 0.3f }),
            ("Index Design Patterns",        "database",   new float[] { 0.75f, 0.65f, 0.25f, 0.15f, 0.4f, 0.3f, 0.3f, 0.2f }),
        };

        var topicVertex = tx.Mutate.AddVertex("Topic").P("name", "Database Engineering").Next();
        var vertexIds = new List<(VertexId id, string topic)>();

        foreach (var (title, topic, vec) in topics)
        {
            var docId = tx.Mutate.AddVertex("Document")
                .P("title", title)
                .P("topic", topic)
                .P("wordCount", (long)(rng.Next(500, 3000)))
                .Next();

            tx.SetVectorProperty(EntityRef.From(docId), "embedding", vec);
            vertexIds.Add((docId, topic));

            tx.Mutate.AddEdge("BELONGS_TO").From(docId).To(topicVertex).Next();
        }

        // 関連文書間の相互参照
        for (var i = 0; i < vertexIds.Count; i++)
        {
            for (var j = i + 1; j < vertexIds.Count; j++)
            {
                if (vertexIds[i].topic == vertexIds[j].topic)
                {
                    tx.Mutate.AddEdge("REFERENCES").From(vertexIds[i].id).To(vertexIds[j].id).Next();
                }
            }
        }

        tx.Commit();
    }

    PrintStats(db);
    Console.WriteLine("  Vector Indexes: 1 (vec_doc)");
    Console.WriteLine();
    Console.WriteLine("Test query in Studio:");
    Console.WriteLine("  readTx.KnnSearch(\"vec_doc\", new float[] { 0.1f, 0.2f, 0.9f, 0.85f, 0.1f, 0.05f, 0.1f, 0.3f }, 8)");
}

static void GenerateHierarchical(string outputPath)
{
    PrepareFile(outputPath);
    using var db = QuiverDatabase.Open(outputPath);

    EditSchema(db, schema =>
    {
        schema.CreateIndex(new ScalarIndexDefinition("idx_dept_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Department"), IndexKind.StringEquality));
        schema.CreateIndex(new ScalarIndexDefinition("idx_person_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality));
        schema.CreateIndex(new FullTextIndexDefinition("ft_person_bio", new PropertyTarget(PropertyOwnerKind.Vertex, "bio", "Person")));
    });

    using (var tx = db.BeginWriteTransaction())
    {
        var g = tx.Query;

        // --- 会社 (ルート) ---
        var company = tx.Mutate.AddVertex("Company").P("name", "Quiver Corp").P("founded", 2024L).Next();

        // --- 部門 (第 1 層) ---
        var eng = tx.Mutate.AddVertex("Department").P("name", "Engineering").P("headcount", 8L).Next();
        var prod = tx.Mutate.AddVertex("Department").P("name", "Product").P("headcount", 3L).Next();
        var design = tx.Mutate.AddVertex("Department").P("name", "Design").P("headcount", 2L).Next();

        tx.Mutate.AddEdge("HAS_DEPT").From(company).To(eng).Next();
        tx.Mutate.AddEdge("HAS_DEPT").From(company).To(prod).Next();
        tx.Mutate.AddEdge("HAS_DEPT").From(company).To(design).Next();

        // --- 開発部門配下のチーム (第 2 層) ---
        var frontend = tx.Mutate.AddVertex("Team").P("name", "Frontend").P("tech", "TypeScript").Next();
        var backend = tx.Mutate.AddVertex("Team").P("name", "Backend").P("tech", "C#").Next();
        var infra = tx.Mutate.AddVertex("Team").P("name", "Infrastructure").P("tech", "Terraform").Next();

        tx.Mutate.AddEdge("HAS_TEAM").From(eng).To(frontend).Next();
        tx.Mutate.AddEdge("HAS_TEAM").From(eng).To(backend).Next();
        tx.Mutate.AddEdge("HAS_TEAM").From(eng).To(infra).Next();

        // --- 従業員 (第 3 層) ---
        var alice = tx.Mutate.AddVertex("Person").P("name", "Alice").P("role", "Frontend Lead").P("bio", "Alice leads the frontend team and specializes in React and TypeScript").Next();
        var bob = tx.Mutate.AddVertex("Person").P("name", "Bob").P("role", "Frontend Dev").P("bio", "Bob is a frontend developer focused on accessibility and design systems").Next();
        var carol = tx.Mutate.AddVertex("Person").P("name", "Carol").P("role", "Backend Lead").P("bio", "Carol architects backend services and manages the API layer").Next();
        var dave = tx.Mutate.AddVertex("Person").P("name", "Dave").P("role", "Backend Dev").P("bio", "Dave works on database integration and query optimization").Next();
        var eve = tx.Mutate.AddVertex("Person").P("name", "Eve").P("role", "Backend Dev").P("bio", "Eve specializes in microservice patterns and event-driven architecture").Next();
        var frank = tx.Mutate.AddVertex("Person").P("name", "Frank").P("role", "SRE").P("bio", "Frank manages cloud infrastructure and CI/CD pipelines").Next();
        var grace = tx.Mutate.AddVertex("Person").P("name", "Grace").P("role", "SRE").P("bio", "Grace focuses on monitoring, alerting, and incident response").Next();
        var heidi = tx.Mutate.AddVertex("Person").P("name", "Heidi").P("role", "PM").P("bio", "Heidi is a product manager driving the roadmap for core features").Next();
        var ivan = tx.Mutate.AddVertex("Person").P("name", "Ivan").P("role", "PM").P("bio", "Ivan handles customer research and prioritization of feature requests").Next();
        var judy = tx.Mutate.AddVertex("Person").P("name", "Judy").P("role", "Designer").P("bio", "Judy creates user interfaces and maintains the design system").Next();
        var ken = tx.Mutate.AddVertex("Person").P("name", "Ken").P("role", "UX Researcher").P("bio", "Ken conducts user research and usability testing").Next();

        // HAS_MEMBER: チームまたは部門 → 従業員 (Sugiyama 用の親 → 子方向)
        tx.Mutate.AddEdge("HAS_MEMBER").From(frontend).To(alice).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(frontend).To(bob).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(backend).To(carol).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(backend).To(dave).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(backend).To(eve).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(infra).To(frank).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(infra).To(grace).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(prod).To(heidi).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(prod).To(ivan).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(design).To(judy).Next();
        tx.Mutate.AddEdge("HAS_MEMBER").From(design).To(ken).Next();

        // MANAGES: 管理者 → 部下 (親 → 子)
        tx.Mutate.AddEdge("MANAGES").From(alice).To(bob).Next();
        tx.Mutate.AddEdge("MANAGES").From(carol).To(dave).Next();
        tx.Mutate.AddEdge("MANAGES").From(carol).To(eve).Next();
        tx.Mutate.AddEdge("MANAGES").From(frank).To(grace).Next();
        tx.Mutate.AddEdge("MANAGES").From(judy).To(ken).Next();
        tx.Mutate.AddEdge("MANAGES").From(heidi).To(ivan).Next();

        // COLLABORATES: チーム間のリンク
        tx.Mutate.AddEdge("COLLABORATES").From(alice).To(judy).P("project", "Design System").Next();
        tx.Mutate.AddEdge("COLLABORATES").From(carol).To(heidi).P("project", "API Roadmap").Next();
        tx.Mutate.AddEdge("COLLABORATES").From(frank).To(carol).P("project", "Deploy Pipeline").Next();

        // MENTORS: 階層をまたぐ接続
        tx.Mutate.AddEdge("MENTORS").From(carol).To(bob).Next();
        tx.Mutate.AddEdge("MENTORS").From(frank).To(eve).Next();

        tx.Commit();
    }

    PrintStats(db);
}

static void ProducePortabilityBundle(string bundlePath, string origin)
{
    string root = Path.GetFullPath(bundlePath);
    string databasePath = Path.Combine(root, "graph.quiver");
    if (File.Exists(databasePath))
        throw new InvalidOperationException($"portability bundle already exists: {root}");
    Directory.CreateDirectory(root);

    var state = new PortabilityOracle(
        FormatVersion: 1,
        Origin: origin,
        Stage: 0,
        PrimaryVertexId: 0,
        SecondaryVertexId: 0,
        UpdatedVertexId: null,
        SnapshotRelativePath: "snapshot/stage-0/graph.quiver");

    using (var database = QuiverDatabase.Open(databasePath))
    {
        EditSchema(database, schema =>
        {
            schema.CreateIndex(new ScalarIndexDefinition(
                "uq_portable_key",
                new PropertyTarget(PropertyOwnerKind.Vertex, "key", "Document"),
                IndexKind.StringEquality,
                Unique: true));
            schema.CreateIndex(new VectorIndexDefinition(
                "vec_portable",
                new PropertyTarget(PropertyOwnerKind.Vertex, "embedding", "Document"),
                Dimensions: 3,
                SegmentPolicy: new VectorSegmentPolicy(
                    MaximumDeltaEntries: 1,
                    MaximumSegments: 2)));
            schema.CreateIndex(new FullTextIndexDefinition(
                "ft_portable",
                new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Document"),
                SegmentPolicy: new FullTextSegmentPolicy(
                    MaximumDeltaEntries: 1,
                    MaximumSegments: 2)));
        });

        VertexId primary;
        VertexId secondary;
        using (var write = database.BeginWriteTransaction())
        {
            primary = write.CreateVertex("Document");
            write.SetProperty(primary, "key", PropertyValue.FromString("portable-0"));
            write.SetProperty(primary, "body", PropertyValue.FromString("日本語の可搬性を検証する文書"));
            write.SetVectorProperty(EntityRef.From(primary), "embedding", [1f, 0f, 0f]);

            secondary = write.CreateVertex("Document");
            write.SetProperty(secondary, "key", PropertyValue.FromString("portable-1"));
            write.SetProperty(secondary, "body", PropertyValue.FromString("cross platform database fixture"));
            write.SetVectorProperty(EntityRef.From(secondary), "embedding", [0f, 1f, 0f]);
            write.Commit();
        }

        state = state with
        {
            PrimaryVertexId = primary.Value,
            SecondaryVertexId = secondary.Value,
        };
        WaitForPortableArtifacts(database);
        AssertPortableDatabase(database, state);
        CreatePortableSnapshot(database, root, state.SnapshotRelativePath);
    }

    WriteOracle(root, state);
    WriteBundleHashes(root);
    VerifyBundleHashes(root);
    Console.WriteLine($"PORTABILITY produce ok: {origin} -> {root}");
}

static void VerifyAndUpdatePortabilityBundle(string bundlePath)
{
    string root = Path.GetFullPath(bundlePath);
    VerifyBundleHashes(root);
    PortabilityOracle state = ReadOracle(root);
    if (state.Stage != 0)
        throw new InvalidOperationException($"expected stage 0 bundle, found stage {state.Stage}");

    using (var recoverySnapshot = QuiverDatabase.Open(
               Path.Combine(root, state.SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar))))
        AssertPortableDatabase(recoverySnapshot, state);

    string databasePath = Path.Combine(root, "graph.quiver");
    using (var database = QuiverDatabase.Open(databasePath))
    {
        AssertPortableDatabase(database, state);
        VertexId updated;
        using (var write = database.BeginWriteTransaction())
        {
            var primary = new VertexId(state.PrimaryVertexId);
            write.SetProperty(
                primary,
                "body",
                PropertyValue.FromString("日本語の可搬性を更新後も検証する文書"));
            write.SetVectorProperty(EntityRef.From(primary), "embedding", [0.9f, 0.1f, 0f]);

            updated = write.CreateVertex("Document");
            write.SetProperty(updated, "key", PropertyValue.FromString("portable-cross"));
            write.SetProperty(updated, "body", PropertyValue.FromString("cross platform update marker"));
            write.SetVectorProperty(EntityRef.From(updated), "embedding", [0f, 0f, 1f]);
            write.Commit();
        }

        state = state with
        {
            Stage = 1,
            UpdatedVertexId = updated.Value,
            SnapshotRelativePath = "snapshot/stage-1/graph.quiver",
        };
        WaitForPortableArtifacts(database);
        AssertPortableDatabase(database, state);
        CreatePortableSnapshot(database, root, state.SnapshotRelativePath);
    }

    WriteOracle(root, state);
    WriteBundleHashes(root);
    VerifyBundleHashes(root);
    Console.WriteLine($"PORTABILITY update ok: {state.Origin} -> {root}");
}

static void VerifyPortabilityBundle(string bundlePath, int expectedStage)
{
    string root = Path.GetFullPath(bundlePath);
    VerifyBundleHashes(root);
    PortabilityOracle state = ReadOracle(root);
    if (state.Stage != expectedStage)
        throw new InvalidOperationException(
            $"expected stage {expectedStage} bundle, found stage {state.Stage}");

    using (var database = QuiverDatabase.Open(Path.Combine(root, "graph.quiver")))
        AssertPortableDatabase(database, state);
    using (var snapshot = QuiverDatabase.Open(
               Path.Combine(root, state.SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar))))
        AssertPortableDatabase(snapshot, state);
    var original = state with
    {
        Stage = 0,
        UpdatedVertexId = null,
        SnapshotRelativePath = "snapshot/stage-0/graph.quiver",
    };
    using (var recoverySnapshot = QuiverDatabase.Open(
               Path.Combine(root, original.SnapshotRelativePath.Replace('/', Path.DirectorySeparatorChar))))
        AssertPortableDatabase(recoverySnapshot, original);

    Console.WriteLine($"PORTABILITY verify ok: {state.Origin}, stage {state.Stage} -> {root}");
}

static void WaitForPortableArtifacts(QuiverDatabase database)
{
    var backend = (BinaryGraphStorageBackend)database.BackendInternal;
    backend.WaitForVectorSegmentMergeForTest();
    backend.WaitForFullTextSegmentMergeForTest();
    if (backend.VectorSegmentMergeErrorForTest is { } vectorError)
        throw new InvalidOperationException("vector segment merge failed", vectorError);
    if (backend.FullTextSegmentMergeErrorForTest is { } textError)
        throw new InvalidOperationException("full-text segment merge failed", textError);
}

static void AssertPortableDatabase(QuiverDatabase database, PortabilityOracle state)
{
    ConsistencyReport consistency = database.Diagnostics.CheckConsistency();
    if (!consistency.IsConsistent)
        throw new InvalidOperationException(
            $"database is inconsistent: {string.Join("; ", consistency.Issues)}");

    using var read = database.BeginReadTransaction();
    var primary = new VertexId(state.PrimaryVertexId);
    var secondary = new VertexId(state.SecondaryVertexId);
    Require(read.VertexExists(primary), "primary vertex is missing");
    Require(read.VertexExists(secondary), "secondary vertex is missing");
    Require(ReadString(read, primary, "key") == "portable-0", "primary key mismatch");
    Require(ReadString(read, secondary, "key") == "portable-1", "secondary key mismatch");

    string expectedPrimaryBody = state.Stage == 0
        ? "日本語の可搬性を検証する文書"
        : "日本語の可搬性を更新後も検証する文書";
    Require(ReadString(read, primary, "body") == expectedPrimaryBody, "primary body mismatch");
    Require(Seek(read, "portable-0").SequenceEqual([primary]), "scalar index mismatch");
    Require(read.Query.Search("ft_portable", "可搬性", 10).ToList().Contains(primary),
        "full-text index mismatch");

    using (VectorSearchCursor vector = read.KnnSearch("vec_portable", [1f, 0f, 0f], 3))
    {
        Require(vector.MoveNext(), "vector index returned no result");
        Require(vector.Current.Owner == EntityRef.From(primary), "vector nearest neighbor mismatch");
    }

    if (state.Stage == 1)
    {
        VertexId updated = new(state.UpdatedVertexId
            ?? throw new InvalidOperationException("stage 1 oracle has no updated vertex"));
        Require(read.VertexExists(updated), "updated vertex is missing");
        Require(ReadString(read, updated, "key") == "portable-cross", "updated key mismatch");
        Require(Seek(read, "portable-cross").SequenceEqual([updated]), "updated scalar index mismatch");
        Require(read.Query.Search("ft_portable", "update", 10).ToList().Contains(updated),
            "updated full-text index mismatch");
    }
}

static IReadOnlyList<VertexId> Seek(IReadTransaction read, string key)
{
    var result = new List<VertexId>();
    using EntityRefEnumerator cursor = read.SeekIndex(
        "uq_portable_key",
        PropertyValue.FromString(key));
    while (cursor.MoveNext())
    {
        if (cursor.Current.Kind == EntityKind.Vertex)
            result.Add(new VertexId(cursor.Current.Value));
    }
    return result;
}

static string ReadString(IReadTransaction read, VertexId vertex, string key)
    => Encoding.UTF8.GetString(read.GetProperty(vertex, key).Utf8StringValue);

static void CreatePortableSnapshot(
    QuiverDatabase database,
    string root,
    string relativePath)
{
    string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    database.CreateSnapshot(path);
    Require(File.Exists(path), $"snapshot was not created: {path}");
    Require(Directory.Exists(path + "-ftseg"), "snapshot full-text artifacts are missing");
    if (relativePath.Contains("stage-0", StringComparison.Ordinal))
    {
        Require(File.Exists(path + "-wal"), "recovery snapshot WAL is missing");
        Require(new FileInfo(path + "-wal").Length > 0, "recovery snapshot WAL is empty");
    }
}

static PortabilityOracle ReadOracle(string root)
{
    string json = File.ReadAllText(Path.Combine(root, "oracle.json"), Encoding.UTF8);
    return JsonSerializer.Deserialize<PortabilityOracle>(json)
        ?? throw new InvalidDataException("oracle.json is empty");
}

static void WriteOracle(string root, PortabilityOracle state)
{
    string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(root, "oracle.json"), json + "\n", new UTF8Encoding(false));
}

static void WriteBundleHashes(string root)
{
    string hashPath = Path.Combine(root, "hashes.sha256");
    string[] files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetFullPath(path).Equals(
            Path.GetFullPath(hashPath),
            StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal)
        .ToArray();
    var lines = new List<string>(files.Length);
    foreach (string file in files)
    {
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        lines.Add($"{hash}  {relative}");
    }
    File.WriteAllText(hashPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
}

static void VerifyBundleHashes(string root)
{
    string hashPath = Path.Combine(root, "hashes.sha256");
    var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
    foreach (string line in File.ReadLines(hashPath, Encoding.UTF8))
    {
        if (string.IsNullOrWhiteSpace(line))
            continue;
        int separator = line.IndexOf("  ", StringComparison.Ordinal);
        if (separator != 64)
            throw new InvalidDataException($"invalid hash line: {line}");
        string expected = line[..separator];
        string relative = line[(separator + 2)..];
        if (!expectedFiles.Add(relative))
            throw new InvalidDataException($"duplicate hash entry: {relative}");
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException($"hash mismatch: {relative}");
    }

    var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetFullPath(path).Equals(
            Path.GetFullPath(hashPath),
            StringComparison.OrdinalIgnoreCase))
        .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
        .ToHashSet(StringComparer.Ordinal);
    if (!actualFiles.SetEquals(expectedFiles))
    {
        string extra = string.Join(", ", actualFiles.Except(expectedFiles).Order(StringComparer.Ordinal));
        string missing = string.Join(", ", expectedFiles.Except(actualFiles).Order(StringComparer.Ordinal));
        throw new InvalidDataException($"hash file set mismatch; extra=[{extra}], missing=[{missing}]");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}

internal sealed record PortabilityOracle(
    int FormatVersion,
    string Origin,
    int Stage,
    long PrimaryVertexId,
    long SecondaryVertexId,
    long? UpdatedVertexId,
    string SnapshotRelativePath);
