namespace Quiver;

/// <summary>データベースを開くための簡易ファクトリ。<see cref="GraphDatabase.Open"/> のラッパ。</summary>
public static class QuiverDb
{
    /// <summary>既定オプションで指定パスのデータベースを開く (無ければ新規作成)。</summary>
    public static Quiver.GraphDatabase OpenDatabase(string path)
        => Quiver.GraphDatabase.Open(path);

    /// <summary>指定オプションでデータベースを開く (無ければ新規作成)。</summary>
    public static Quiver.GraphDatabase OpenDatabase(string path, Quiver.GraphDatabaseOptions options)
        => Quiver.GraphDatabase.Open(path, options);
}
