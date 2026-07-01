namespace Quiver;

/// <summary>
/// <see cref="GraphDatabase"/>インスタンスが使用するストレージバックエンドの識別用列挙体
/// </summary>
public enum BackendKind
{
    /// <summary>独自バイナリページフォーマットを用いるネイティブバックエンド</summary>
    Binary = 1,

    /// <summary>データをプロセス内メモリだけに保持する非永続バックエンド</summary>
    InMemory = 2,
}
