using Yatagarasu.Core;

namespace Yatagarasu.Logical;

/// <summary>
/// コミット済みトランザクションの論理ミューテーションストリームを受信するシンク。
/// 成功したコミットごとに物理的な永続化境界 (WAL フラッシュ) 完了後に一度だけ発火する。
/// ロールバックされたトランザクションは配信されない。
/// </summary>
/// <remarks>
/// <para>
/// 実装はスレッドセーフであること。長時間 / ブロッキングな処理は帯域外にキューすること
/// (パブリッシャはハンドオフ後にトランザクションを保持しない)。
/// </para>
/// <para>
/// シンクが投げた例外は飲み込まれる (不良シンクが成功したコミットを隠蔽してはならない)。
/// at-least-once 配信が必要なシンクは返却前に永続化し、起動時に自前で reconciliation を行うこと。
/// </para>
/// </remarks>
internal interface ILogicalMutationSink
{
    /// <summary>
    /// コミット済みトランザクションの順序付きミューテーション一覧とともに呼び出される。
    /// リストの所有権は呼び出し後にシンクへ移る (保持可能)。
    /// </summary>
    void OnCommitted(TransactionId transactionId, IReadOnlyList<LogicalMutation> mutations);
}
