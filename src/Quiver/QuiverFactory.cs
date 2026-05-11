namespace Quiver;

public static class QuiverDb
{
    public static Quiver.GraphDatabase OpenDatabase(string path)
        => Quiver.GraphDatabase.Open(path);

    public static Quiver.GraphDatabase OpenDatabase(string path, Quiver.GraphDatabaseOptions options)
        => Quiver.GraphDatabase.Open(path, options);
}
