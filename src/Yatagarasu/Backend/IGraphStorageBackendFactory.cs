namespace Yatagarasu;

/// <summary>
/// <see cref="YatagarasuDatabase.Open(string, YatagarasuDatabaseOptions?)"/> がアクティブな
/// バックエンドの構築ファクトリ。
/// </summary>
// <see cref="YatagarasuDatabase"/> をサブクラス化せず、
// テスト用のインメモリファクトリ等を呼び出し側で注入できる。
internal interface IGraphStorageBackendFactory
{
    /// <summary>指定ディレクトリとオプションでバックエンドをオープンする。 (新規作成も含む) </summary>
    IGraphStorageBackend Open(string directoryPath, YatagarasuDatabaseOptions options);
}
