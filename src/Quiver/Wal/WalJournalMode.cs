namespace Quiver.Storage.Wal;

/// <summary>
/// FTS-7: ページ単位の WAL journaling モード。<see cref="PinForWrite"/> 時に
/// 指定し、<see cref="WalPageContext"/> が per-tx に記録して before-image (pin 時) と
/// after-image (UnpinDirty 時) の両発火点で参照する単一チョークポイントを構成する。
///
/// escalation 規則: 同一 tx 内で同一ページが複数モードで pin された場合、**強い方 (= ログ量が多い方)
/// が勝つ** (<c>Suppressed &lt; RedoOnly &lt; Full</c>, 数値の大きい方を採用)。論理 leaf として
/// <see cref="Suppressed"/> で pin したページが後で SMO 当事者 (あふれた旧 leaf) になったら
/// <see cref="RedoOnly"/> へ昇格し、構造の after-image を確実に残す。
/// </summary>
internal enum WalJournalMode : byte
{
    /// <summary>論理レコードで覆う leaf: before-image (CLR) も after-image (PageImage) も出さない。</summary>
    Suppressed = 0,

    /// <summary>
    /// SMO (split/merge) の構造ページ: after-image (PageImage) のみを **eager** に追記し
    /// (commit 時 coalesce に乗せない)、before-image (CLR) は出さない
    /// (nested top action = redo-only)。
    /// </summary>
    RedoOnly = 1,

    /// <summary>既定: before-image (CLR) + after-image (PageImage) の両方を出す (FT 以外の全ページ)。</summary>
    Full = 2,
}
