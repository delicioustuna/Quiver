using Quiver;
using Quiver.Api;
using Quiver.Core;

var outputPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "sample", "movie.quiver");

var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
Directory.CreateDirectory(dir);

if (File.Exists(outputPath))
    File.Delete(outputPath);

var walPath = outputPath + "-wal";
if (File.Exists(walPath))
    File.Delete(walPath);

Console.WriteLine($"Generating sample database: {Path.GetFullPath(outputPath)}");

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

    // Index entries
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
