namespace Quiver;

/// <summary>
/// <see cref="GraphDatabase.Open(string, GraphDatabaseOptions?)"/> がアクティブな
/// バックエンドの構築ファクトリ。
/// </summary>
// <see cref="GraphDatabase"/> をサブクラス化せず、
// テスト用のインメモリファクトリ等を呼び出し側で注入できる。
public interface IGraphStorageBackendFactory
{
    /// <summary>指定ディレクトリとオプションでバックエンドをオープンする。 (新規作成も含む) </summary>
    IGraphStorageBackend Open(string directoryPath, GraphDatabaseOptions options);
}
