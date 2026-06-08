using Quiver.Core;
using Quiver.Maintenance;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// Quiver ストレージエンジンのバックエンド側コントラクト。各バックエンド
/// (バイナリ、SQLite 等) がこのインタフェースを実装することで、
/// <see cref="GraphDatabase"/> は薄いファサードに留まり、ストレージレイアウトを
/// ファクトリレベルで差し替え可能にする。
/// </summary>
public interface IGraphStorageBackend : IDisposable
{
    // ARCH-2: Transactions (ITransactionManager) / Access (IGraphAccessMethods) /
    // BulkLoad (BulkLoadCapabilities) は内部実装型を露出するため公開面から外し、
    // internal な IGraphStorageBackendInternal へ移設した。

    /// <summary>このバックエンドのスキーマ API。</summary>
    ISchemaApi Schema { get; }

    /// <summary>このバックエンドの診断 API。</summary>
    IDiagnosticsApi Diagnostics { get; }

    /// <summary>
    /// <see cref="IGraphAccessMethods.KnnSearch"/> が利用するベクトルストア。
    /// ユーザにも <c>CreateVectorIndex</c> / <c>SetVector</c> 用に公開される。
    /// ベクトルの永続化に未対応のバックエンド (バイナリ、SQLite MVP) ではインメモリストアが既定。
    /// </summary>
    IVectorStore Vectors { get; }

    /// <summary>
    /// アクティブなバックエンドに整合したトークン解決を伴う形で
    /// 新しいトランザクションを <see cref="IGraphTransaction"/> でラップして開始する。
    /// </summary>
    IGraphTransaction BeginGraphTransaction(IsolationLevel level, bool readOnly);

    /// <summary>
    /// 書き込みを止めずに <paramref name="targetDirectory"/> に
    /// クラッシュ整合なライブスナップショットを取る。
    /// 既定実装は <see cref="NotSupportedException"/>。
    ///
    /// バイナリバックエンドの実装は: ベストエフォートでシャープチェックポイントを起動し、
    /// 全データページファイル / 索引ファイルを page-by-page で複製した後、WAL セグメントを
    /// 末尾までコピーする。並行で書き込むトランザクションは block されず、target を
    /// <see cref="GraphDatabase.Open"/> で開いたときに recovery が WAL から redo / undo して
    /// snapshot 時点までの整合状態に収束する。
    /// </summary>
    void CreateSnapshot(string targetDirectory, SnapshotOptions? options = null)
        => throw new NotSupportedException(
            "CreateSnapshot is not supported by this backend.");

    /// <summary>
    /// dead version の物理回収 / free list 圧縮を行う vacuum を実行する。
    /// 既定実装は <see cref="NotSupportedException"/>。バイナリバックエンドのみ実装。
    /// </summary>
    VacuumReport Vacuum(VacuumOptions? options = null)
        => throw new NotSupportedException(
            "Vacuum is not supported by this backend.");
}
