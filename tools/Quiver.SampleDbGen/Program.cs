using Quiver;
using Quiver.Api;
using Quiver.Core;

var mode = args.Length > 0 ? args[0] : "movie";
var pathArg = args.Length > 1 ? args[1] : null;

switch (mode)
{
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
    Console.WriteLine($"  FT Indexes:    {db.Schema.ListFullTextIndexes().Count}");
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
        schema.CreateFullTextIndex("ft_movie_title", "Movie", "title");
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

    EditSchema(db, schema => schema.CreateIndex(
        new ScalarIndexDefinition("idx_doc_title", new PropertyTarget(PropertyOwnerKind.Vertex, "title", "Document"), IndexKind.StringEquality)));
    db.Vectors.CreateVectorIndex(new VectorIndexSpec(
        "vec_doc", EntityKind.Vertex, default, 8, DistanceMetric.Cosine, "sample"));

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

            tx.SetVector(EntityKind.Vertex, docId.Sequence, "vec_doc", vec);
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
    Console.WriteLine("  db.Vectors.KnnSearch(\"vec_doc\", new float[] { 0.1f, 0.2f, 0.9f, 0.85f, 0.1f, 0.05f, 0.1f, 0.3f }, 8)");
}

static void GenerateHierarchical(string outputPath)
{
    PrepareFile(outputPath);
    using var db = QuiverDatabase.Open(outputPath);

    EditSchema(db, schema =>
    {
        schema.CreateIndex(new ScalarIndexDefinition("idx_dept_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Department"), IndexKind.StringEquality));
        schema.CreateIndex(new ScalarIndexDefinition("idx_person_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality));
        schema.CreateFullTextIndex("ft_person_bio", "Person", "bio");
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
