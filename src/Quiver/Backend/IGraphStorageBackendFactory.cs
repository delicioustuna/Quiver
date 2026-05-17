namespace Quiver;

/// <summary>
/// <see cref="GraphDatabase.Open(string, GraphDatabaseOptions?)"/> がアクティブな
/// バックエンドを構築する際に使うファクトリ。<see cref="GraphDatabase"/> をサブクラス化せず、
/// テスト用のインメモリファクトリ等を呼び出し側で注入できる。
/// </summary>
public interface IGraphStorageBackendFactory
{
    /// <summary>指定ディレクトリとオプションでバックエンドをオープン (新規作成も含む) する。</summary>
    IGraphStorageBackend Open(string directoryPath, GraphDatabaseOptions options);
}
