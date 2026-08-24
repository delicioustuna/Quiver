using Yatagarasu.Core;
using Yatagarasu.Maintenance;
using Yatagarasu.Transactions;

namespace Yatagarasu;

/// <summary>
/// Yatagarasu ストレージエンジンのバックエンド定義側コントラクト
/// </summary>
internal interface IGraphStorageBackend : IDisposable
{
    // バックエンドがこのインタフェースを実装することで、
    // <see cref="YatagarasuDatabase"/> は薄いファサードに留まり、ストレージレイアウトを
    // ファクトリレベルで差し替え可能にする。

    // Transactions (ITransactionManager) / Access (IGraphAccessMethods) /
    // BulkLoad (BulkLoadCapabilities) は内部実装型を露出するため公開面から外し、
    // internal な IGraphStorageBackendInternal へ移設した。

    /// <summary>診断 API</summary>
    IDiagnosticsApi Diagnostics { get; }

    /// <summary>
    /// backend 固有の snapshot reader を開始する。
    /// </summary>
    IReadTransaction BeginReadTransaction();

    /// <summary>backend 固有の single-writer transaction を開始する。</summary>
    IWriteTransaction BeginWriteTransaction();

    /// <summary>
    /// backendを閉じずに<paramref name="targetDirectory"/>へ
    /// クラッシュ整合なライブスナップショットを取る。
    /// </summary>
    /// <param name="targetDirectory">
    /// 互換上の名前はdirectoryだが、binary backendではコピー先primary file (<c>*.yata</c>)のパス。
    /// </param>
    /// <param name="options">snapshotオプション。</param>
    /// <exception cref="NotSupportedException">規定実装</exception>
    // binary backendはsingle-writer mutation lease内でprimary fileをpage-by-pageに複製し、
    // WALとimmutable全文artifactを同じベース名へコピーする。targetを
    // <see cref="YatagarasuDatabase.Open"/>で開いたときにrecoveryが明示Commitを持つ
    // transactionだけをredoし、snapshot時点までの整合状態に収束する。
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
