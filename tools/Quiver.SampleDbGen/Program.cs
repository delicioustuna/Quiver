using Quiver;
using Quiver.Api;
using Quiver.Core;

var mode = args.Length > 0 && args[0] == "--hierarchical" ? "hierarchical" : "movie";
var pathArg = mode == "hierarchical" && args.Length > 1 ? args[1]
    : args.Length > 0 && args[0] != "--hierarchical" ? args[0]
    : null;

if (mode == "hierarchical")
    GenerateHierarchical(pathArg ?? Path.Combine(AppContext.BaseDirectory, "sample2.quiver"));
else
    GenerateMovie(pathArg ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sample", "movie.quiver"));

static void PrepareFile(string outputPath)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
    Directory.CreateDirectory(dir);
    if (File.Exists(outputPath)) File.Delete(outputPath);
    var walPath = outputPath + "-wal";
    if (File.Exists(walPath)) File.Delete(walPath);
    Console.WriteLine($"Generating sample database: {Path.GetFullPath(outputPath)}");
}

static void PrintStats(GraphDatabase db)
{
    var stats = db.Diagnostics.GetStatistics();
    Console.WriteLine($"  Nodes:         {stats.NodeCount}");
    Console.WriteLine($"  Relationships: {stats.RelationshipCount}");
    Console.WriteLine($"  Properties:    {stats.PropertyCount}");
    Console.WriteLine($"  Labels:        [{string.Join(", ", db.Schema.ListLabels())}]");
    Console.WriteLine($"  Rel Types:     [{string.Join(", ", db.Schema.ListRelationshipTypes())}]");
    Console.WriteLine($"  Prop Keys:     [{string.Join(", ", db.Schema.ListPropertyKeys())}]");
    Console.WriteLine($"  Indexes:       {db.Schema.ListIndexes().Count}");
    Console.WriteLine($"  FT Indexes:    {db.Schema.ListFullTextIndexes().Count}");
    Console.WriteLine("Done.");
}

static void GenerateMovie(string outputPath)
{
    PrepareFile(outputPath);
    using var db = GraphDatabase.Open(outputPath);

    db.Schema.CreateIndex("idx_person_name", "Person", "name", IndexKind.StringEquality);
    db.Schema.CreateIndex("idx_movie_year", "Movie", "year", IndexKind.Int64Equality);
    db.Schema.CreateFullTextIndex("ft_movie_title", "Movie", "title");

    using (var tx = db.BeginTransaction())
    {
        var g = tx.G(db.Schema);

        var keanu = g.AddNode("Person").P("name", "Keanu Reeves").P("born", 1964L).Next();
        var carrie = g.AddNode("Person").P("name", "Carrie-Anne Moss").P("born", 1967L).Next();
        var hugo = g.AddNode("Person").P("name", "Hugo Weaving").P("born", 1960L).Next();
        var laurence = g.AddNode("Person").P("name", "Laurence Fishburne").P("born", 1961L).Next();
        var lana = g.AddNode("Person").P("name", "Lana Wachowski").P("born", 1965L).Next();
        var lilly = g.AddNode("Person").P("name", "Lilly Wachowski").P("born", 1967L).Next();
        var tom = g.AddNode("Person").P("name", "Tom Hanks").P("born", 1956L).Next();
        var robert = g.AddNode("Person").P("name", "Robert Zemeckis").P("born", 1952L).Next();

        var matrix = g.AddNode("Movie").P("title", "The Matrix").P("year", 1999L).P("tagline", "Welcome to the Real World").Next();
        var reloaded = g.AddNode("Movie").P("title", "The Matrix Reloaded").P("year", 2003L).Next();
        var forrest = g.AddNode("Movie").P("title", "Forrest Gump").P("year", 1994L).P("tagline", "Life is like a box of chocolates").Next();

        g.AddRelationship("ACTED_IN").From(keanu).To(matrix).P("role", "Neo").Next();
        g.AddRelationship("ACTED_IN").From(carrie).To(matrix).P("role", "Trinity").Next();
        g.AddRelationship("ACTED_IN").From(hugo).To(matrix).P("role", "Agent Smith").Next();
        g.AddRelationship("ACTED_IN").From(laurence).To(matrix).P("role", "Morpheus").Next();
        g.AddRelationship("ACTED_IN").From(keanu).To(reloaded).P("role", "Neo").Next();
        g.AddRelationship("ACTED_IN").From(carrie).To(reloaded).P("role", "Trinity").Next();
        g.AddRelationship("ACTED_IN").From(hugo).To(reloaded).P("role", "Agent Smith").Next();
        g.AddRelationship("ACTED_IN").From(tom).To(forrest).P("role", "Forrest Gump").Next();

        g.AddRelationship("DIRECTED").From(lana).To(matrix).Next();
        g.AddRelationship("DIRECTED").From(lilly).To(matrix).Next();
        g.AddRelationship("DIRECTED").From(lana).To(reloaded).Next();
        g.AddRelationship("DIRECTED").From(lilly).To(reloaded).Next();
        g.AddRelationship("DIRECTED").From(robert).To(forrest).Next();

        g.AddRelationship("KNOWS").From(keanu).To(carrie).Next();
        g.AddRelationship("KNOWS").From(keanu).To(hugo).Next();

        (string name, NodeId id)[] people =
        [
            ("Keanu Reeves", keanu), ("Carrie-Anne Moss", carrie), ("Hugo Weaving", hugo),
            ("Laurence Fishburne", laurence), ("Lana Wachowski", lana), ("Lilly Wachowski", lilly),
            ("Tom Hanks", tom), ("Robert Zemeckis", robert),
        ];
        foreach (var (name, id) in people)
            tx.IndexInsert("idx_person_name", name, id);

        tx.IndexInsert("idx_movie_year", 1999L, matrix);
        tx.IndexInsert("idx_movie_year", 2003L, reloaded);
        tx.IndexInsert("idx_movie_year", 1994L, forrest);

        tx.Commit();
    }

    PrintStats(db);
}

static void GenerateHierarchical(string outputPath)
{
    PrepareFile(outputPath);
    using var db = GraphDatabase.Open(outputPath);

    db.Schema.CreateIndex("idx_dept_name", "Department", "name", IndexKind.StringEquality);
    db.Schema.CreateIndex("idx_person_name", "Person", "name", IndexKind.StringEquality);
    db.Schema.CreateFullTextIndex("ft_person_bio", "Person", "bio");

    using (var tx = db.BeginTransaction())
    {
        var g = tx.G(db.Schema);

        // --- Company (root) ---
        var company = g.AddNode("Company").P("name", "Quiver Corp").P("founded", 2024L).Next();

        // --- Departments (layer 1) ---
        var eng = g.AddNode("Department").P("name", "Engineering").P("headcount", 8L).Next();
        var prod = g.AddNode("Department").P("name", "Product").P("headcount", 3L).Next();
        var design = g.AddNode("Department").P("name", "Design").P("headcount", 2L).Next();

        g.AddRelationship("HAS_DEPT").From(company).To(eng).Next();
        g.AddRelationship("HAS_DEPT").From(company).To(prod).Next();
        g.AddRelationship("HAS_DEPT").From(company).To(design).Next();

        // --- Teams under Engineering (layer 2) ---
        var frontend = g.AddNode("Team").P("name", "Frontend").P("tech", "TypeScript").Next();
        var backend = g.AddNode("Team").P("name", "Backend").P("tech", "C#").Next();
        var infra = g.AddNode("Team").P("name", "Infrastructure").P("tech", "Terraform").Next();

        g.AddRelationship("HAS_TEAM").From(eng).To(frontend).Next();
        g.AddRelationship("HAS_TEAM").From(eng).To(backend).Next();
        g.AddRelationship("HAS_TEAM").From(eng).To(infra).Next();

        // --- People (layer 3) ---
        var alice = g.AddNode("Person").P("name", "Alice").P("role", "Frontend Lead").P("bio", "Alice leads the frontend team and specializes in React and TypeScript").Next();
        var bob = g.AddNode("Person").P("name", "Bob").P("role", "Frontend Dev").P("bio", "Bob is a frontend developer focused on accessibility and design systems").Next();
        var carol = g.AddNode("Person").P("name", "Carol").P("role", "Backend Lead").P("bio", "Carol architects backend services and manages the API layer").Next();
        var dave = g.AddNode("Person").P("name", "Dave").P("role", "Backend Dev").P("bio", "Dave works on database integration and query optimization").Next();
        var eve = g.AddNode("Person").P("name", "Eve").P("role", "Backend Dev").P("bio", "Eve specializes in microservice patterns and event-driven architecture").Next();
        var frank = g.AddNode("Person").P("name", "Frank").P("role", "SRE").P("bio", "Frank manages cloud infrastructure and CI/CD pipelines").Next();
        var grace = g.AddNode("Person").P("name", "Grace").P("role", "SRE").P("bio", "Grace focuses on monitoring, alerting, and incident response").Next();
        var heidi = g.AddNode("Person").P("name", "Heidi").P("role", "PM").P("bio", "Heidi is a product manager driving the roadmap for core features").Next();
        var ivan = g.AddNode("Person").P("name", "Ivan").P("role", "PM").P("bio", "Ivan handles customer research and prioritization of feature requests").Next();
        var judy = g.AddNode("Person").P("name", "Judy").P("role", "Designer").P("bio", "Judy creates user interfaces and maintains the design system").Next();
        var ken = g.AddNode("Person").P("name", "Ken").P("role", "UX Researcher").P("bio", "Ken conducts user research and usability testing").Next();

        // HAS_MEMBER: team/dept → person (parent→child direction for Sugiyama)
        g.AddRelationship("HAS_MEMBER").From(frontend).To(alice).Next();
        g.AddRelationship("HAS_MEMBER").From(frontend).To(bob).Next();
        g.AddRelationship("HAS_MEMBER").From(backend).To(carol).Next();
        g.AddRelationship("HAS_MEMBER").From(backend).To(dave).Next();
        g.AddRelationship("HAS_MEMBER").From(backend).To(eve).Next();
        g.AddRelationship("HAS_MEMBER").From(infra).To(frank).Next();
        g.AddRelationship("HAS_MEMBER").From(infra).To(grace).Next();
        g.AddRelationship("HAS_MEMBER").From(prod).To(heidi).Next();
        g.AddRelationship("HAS_MEMBER").From(prod).To(ivan).Next();
        g.AddRelationship("HAS_MEMBER").From(design).To(judy).Next();
        g.AddRelationship("HAS_MEMBER").From(design).To(ken).Next();

        // MANAGES: manager → subordinate (parent→child)
        g.AddRelationship("MANAGES").From(alice).To(bob).Next();
        g.AddRelationship("MANAGES").From(carol).To(dave).Next();
        g.AddRelationship("MANAGES").From(carol).To(eve).Next();
        g.AddRelationship("MANAGES").From(frank).To(grace).Next();
        g.AddRelationship("MANAGES").From(judy).To(ken).Next();
        g.AddRelationship("MANAGES").From(heidi).To(ivan).Next();

        // COLLABORATES: cross-team links
        g.AddRelationship("COLLABORATES").From(alice).To(judy).P("project", "Design System").Next();
        g.AddRelationship("COLLABORATES").From(carol).To(heidi).P("project", "API Roadmap").Next();
        g.AddRelationship("COLLABORATES").From(frank).To(carol).P("project", "Deploy Pipeline").Next();

        // MENTORS: skip-level connections
        g.AddRelationship("MENTORS").From(carol).To(bob).Next();
        g.AddRelationship("MENTORS").From(frank).To(eve).Next();

        // Index entries
        (string name, NodeId id)[] depts = [("Engineering", eng), ("Product", prod), ("Design", design)];
        foreach (var (name, id) in depts)
            tx.IndexInsert("idx_dept_name", name, id);

        (string name, NodeId id)[] people =
        [
            ("Alice", alice), ("Bob", bob), ("Carol", carol), ("Dave", dave),
            ("Eve", eve), ("Frank", frank), ("Grace", grace), ("Heidi", heidi),
            ("Ivan", ivan), ("Judy", judy), ("Ken", ken),
        ];
        foreach (var (name, id) in people)
            tx.IndexInsert("idx_person_name", name, id);

        tx.Commit();
    }

    PrintStats(db);
}
