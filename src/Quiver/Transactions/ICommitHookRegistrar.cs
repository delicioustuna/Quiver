namespace Quiver.Transactions;

/// <summary>
/// トランザクションの結果が永続化された後 (コミット時) またはロールバック後に発火するコールバックを
/// 登録するインタフェース。埋め込みパイプライン / キャッシュ無効化 /
/// 監査ログ送出などをトランザクション内部に結合せずにコミット完了へフックできるようにする。
/// 汎用設計 — 埋め込み専用ではない。
/// </summary>
/// <remarks>
/// セマンティクス:
/// <list type="bullet">
/// <item><see cref="OnCommitted"/> は WAL フラッシュが正常に返ったあとにのみ発火する。
/// コミットが失敗した場合は <see cref="OnRolledBack"/> ハンドラのみが走る。</item>
/// <item>ハンドラは登録順に実行される。あるハンドラで例外が発生してもキャッチして無視するため、
/// 後続ハンドラは引き続き実行され、トランザクションの結果には影響しない。</item>
/// <item>トランザクションが既に終了 (Committed / Aborted) した後に登録されたハンドラは、
/// 既に確定した結果に合わせて即座にディスパッチされる。</item>
/// </list>
/// クラッシュリカバリのシナリオ: WAL fsync 後・フック発火前にプロセスが死ぬとフックは失われる。
/// at-least-once 配送が必要な呼び出し側 (例: 埋め込みタスクキュー投入) は起動時スキャンで
/// 再調整すること — 埋め込みパイプラインの <c>ScanAndEnqueueAsync</c> を参照。
/// </remarks>
public interface ICommitHookRegistrar
{
    /// <summary>
    /// トランザクションがコミット (WAL フラッシュ完了) した後に発火する
    /// コールバックを登録する。
    /// </summary>
    void OnCommitted(Action callback);

    /// <summary>
    /// トランザクションがロールバック (明示的な Abort、Active トランザクションの Dispose、
    /// またはコミット失敗) された後に発火するコールバックを登録する。
    /// </summary>
    void OnRolledBack(Action callback);
}
