namespace Quiver;

public static class QuiverDb
{
    public static GraphDb.Engine.GraphDatabase OpenDatabase(string path)
        => GraphDb.Engine.GraphDatabase.Open(path);

    public static GraphDb.Engine.GraphDatabase OpenDatabase(string path, GraphDb.Engine.GraphDatabaseOptions options)
        => GraphDb.Engine.GraphDatabase.Open(path, options);
}
