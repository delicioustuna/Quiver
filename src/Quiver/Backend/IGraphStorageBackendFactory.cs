namespace Quiver;

/// <summary>
/// <see cref="QuiverDatabase.Open(string, QuiverDatabaseOptions?)"/> がアクティブな
/// バックエンドの構築ファクトリ。
/// </summary>
// <see cref="QuiverDatabase"/> をサブクラス化せず、
// テスト用のインメモリファクトリ等を呼び出し側で注入できる。
public interface IGraphStorageBackendFactory
{
    /// <summary>指定ディレクトリとオプションでバックエンドをオープンする。 (新規作成も含む) </summary>
    IGraphStorageBackend Open(string directoryPath, QuiverDatabaseOptions options);
}
