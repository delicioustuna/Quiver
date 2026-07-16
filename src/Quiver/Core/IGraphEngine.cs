namespace Quiver.Core;

/// <summary>
/// <c>Quiver.Embedding</c> ヘルパが直接 <c>Quiver.QuiverDatabase</c> に依存せずに済むための薄い抽象。
/// アダプタを介することで、ヘルパはエンジン内部実装から疎結合に保たれる。
/// </summary>
/// <remarks>
/// エンジンはベクトルストア・ベクトルカタログ、およびスキャン / バックフィル経路 (Z') 用の
/// 短命な読み取りセッションのオープン権を所有する。post-commit フック (Y') は呼び出し側が
/// <c>ICommitHookRegistrar</c> をトランザクション上で直接呼び出して接続するため、エンジンが仲介する必要は無い。
/// </remarks>
public interface IGraphEngine
{
    /// <summary>エンジンのベクトルストア。</summary>
    IVectorStore Vectors { get; }

    /// <summary>エンジンのベクトルカタログ。</summary>
    IVectorCatalog Catalog { get; }

    /// <summary>
    /// エンティティ列挙とその source-text プロパティ読み出しのために、グラフに対する
    /// 読み取り専用セッションを開く。セッションは必ず Dispose して下層トランザクションを解放すること。
    /// </summary>
    IGraphEngineReadSession BeginRead();
}

/// <summary>
/// <c>ScanAndEnqueueAsync</c> が利用するワンショット読み取りセッション。埋め込みヘルパが必要とする
/// 操作 (全エンティティ列挙と文字列プロパティ参照) のみを公開し、将来のエンジン内部変更が
/// ヘルパアセンブリに波及しないようにする。
/// </summary>
public interface IGraphEngineReadSession : IDisposable
{
    /// <summary>
    /// 要求された種別の生存中エンティティをすべて列挙する。順序は実装依存で、
    /// ヘルパは順序に依存しない。
    /// </summary>
    IEnumerable<EntityRef> EnumerateEntities(EntityKind kind);

    /// <summary>
    /// 名前で UTF-8 文字列プロパティを読み出す。エンティティが該当プロパティを持たない、
    /// もしくは文字列以外の場合は false を返す。文字列以外の値は強制変換せず「source text 無し」として扱う。
    /// </summary>
    bool TryReadStringProperty(EntityRef entity, string propertyKey, out string text);
}
