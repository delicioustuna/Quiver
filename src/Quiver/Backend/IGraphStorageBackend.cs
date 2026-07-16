using Quiver.Core;
using Quiver.Maintenance;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// Quiver ストレージエンジンのバックエンド定義側コントラクト
/// </summary>
public interface IGraphStorageBackend : IDisposable
{
    // バックエンドがこのインタフェースを実装することで、
    // <see cref="QuiverDatabase"/> は薄いファサードに留まり、ストレージレイアウトを
    // ファクトリレベルで差し替え可能にする。

    // Transactions (ITransactionManager) / Access (IGraphAccessMethods) /
    // BulkLoad (BulkLoadCapabilities) は内部実装型を露出するため公開面から外し、
    // internal な IGraphStorageBackendInternal へ移設した。

    /// <summary>スキーマ API</summary>
    ISchemaApi Schema { get; }

    /// <summary>診断 API</summary>
    IDiagnosticsApi Diagnostics { get; }

    /// <summary>
    /// <see cref="IGraphAccessMethods.KnnSearch"/> が利用するベクトルストア
    /// </summary>
    IVectorStore Vectors { get; } // ユーザにも <c>CreateVectorIndex</c> / <c>SetVector</c> 用に公開される。

    /// <summary>
    /// アクティブなバックエンドに整合したトークン解決を伴う形で
    /// 新しいトランザクションを <see cref="IGraphTransaction"/> でラップして開始する。
    /// </summary>
    IGraphTransaction BeginGraphTransaction(IsolationLevel level, bool readOnly);

    /// <summary>
    /// 書き込みを止めずに <paramref name="targetDirectory"/> に
    /// クラッシュ整合なライブスナップショットを取る。
    /// </summary>
    /// <exception cref="NotSupportedException">規定実装</exception>
    // バイナリバックエンドの実装は: ベストエフォートでシャープチェックポイントを起動し、
    // 全データページファイル / 索引ファイルを page-by-page で複製した後、WAL セグメントを
    // 末尾までコピーする。並行で書き込むトランザクションは block されず、target を
    // <see cref="QuiverDatabase.Open"/> で開いたときに recovery が WAL から redo / undo して
    // snapshot 時点までの整合状態に収束する。
    void CreateSnapshot(string targetDirectory, SnapshotOptions? options = null)
        => throw new NotSupportedException(
            "CreateSnapshot is not supported by this backend.");

    /// <summary>
    /// dead version の物理回収 / free list 圧縮を行う vacuum を実行する。
    /// </summary>
    /// <exception cref="NotSupportedException">規定実装</exception>
    VacuumReport Vacuum(VacuumOptions? options = null)
        => throw new NotSupportedException(
            "Vacuum is not supported by this backend.");    // バイナリバックエンドのみ実装
}
