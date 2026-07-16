using System.Diagnostics;
using Quiver.Telemetry;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Storage.Wal;

namespace Quiver.Transactions;

/// <summary>
/// チェックポイントの atomicity を保証するための Begin/End sentinel 方式。
/// 手順:
///  1. <see cref="IWriteAheadLog.WriteCheckpointBegin"/> を書き fsync する (begin sentinel)。
///  2. 全データファイルのダーティページをディスクへフラッシュ (fsync) する。
/// 3. 全索引ファイルのダーティページも fsync する。
///  4. <see cref="IWriteAheadLog.WriteCheckpointEnd"/> を書き fsync する (end sentinel)。
///  5. Begin より前の WAL セグメントを truncate (削除) する。
/// recovery は「Begin と対になる End を持つチェックポイント」だけを「完了済み」と認識し、
/// Begin のみ (= 途中で kill された partial checkpoint) は無視して redo 起点を前回 End まで
/// 戻す。これにより:
///  - step 2/3 の途中で kill された場合: partial 状態のデータ / 索引ページが残っても、End
///    が無いので recovery は前回 End からやり直し、WAL から PageImage を redo して正しい
///    最終状態へ収束する。
///  - step 4/5 の途中で kill された場合: data file は全 fsync 済みなので End が無くても
///    不整合は無いが、安全側に倒し redo をやり直す (idempotent なので破損しない、recovery
///    時間が若干伸びるだけ)。
/// 順序が重要: Begin → page fsync → End → truncate。truncate は必ず End 観測後に発生する。
/// チェックポイントを「いつ」打つか (契機) は <see cref="TransactionManager"/> が判断する。
/// 本クラスは「どう」打つか (機構) のみを担当する。
/// 前提: WAL 上の PageImage はコミット時にまとめて追記される（コアレス方式）。よって
/// 任意のチェックポイント時点の WAL には「未コミットトランザクションの宙ぶらりんな
/// PageImage」が存在しない。さらに <see cref="TransactionManager"/> はアクティブ
/// トランザクションが 0 のときにのみ本チェックポイントを呼ぶため、シャープ
/// チェックポイント (全ダーティページ = コミット済み) として安全に truncate できる。
/// </summary>
internal sealed class Checkpointer(
    IPageManager pageManager,
    IWriteAheadLog wal,
    Func<long> oldestActiveLsn,
    IIndexManager? indexManager = null)
{
    private readonly IPageManager _pageManager = pageManager;
    private readonly IWriteAheadLog _wal = wal;
    private readonly Func<long> _oldestActiveLsn = oldestActiveLsn;
    // null 可。null の場合は索引 flush をスキップ (索引を持たないバックエンド向け)。
    private readonly IIndexManager? _indexManager = indexManager;

    /// <summary>
    /// テスト用: チェックポイントの各 phase 完了後にフックを呼ぶ。テストはここで
    /// 例外を投げることで「phase X 完了直後に kill された」状況を模擬する。Release ビルドの
    /// 本番経路では null。
    /// </summary>
    internal static Action<CheckpointPhase>? PhaseInjector;

    /// <summary>
    /// チェックポイントを 1 回実行する。呼び出し側 (<see cref="TransactionManager"/>) が
    /// 同時実行を排他し、アクティブトランザクションが無いことを保証すること。
    /// </summary>
    public void Checkpoint()
    {
        // チェックポイントの span と所要時間 histogram。
        using var activity = QuiverTelemetry.CheckpointActivitySource.StartActivity(
            "checkpoint", ActivityKind.Internal);
        var sw = Stopwatch.StartNew();
        // 1. Begin sentinel を書いて fsync。これより前に walk が来た場合、ここに到達する
        //   前に kill されたなら Begin すら無いので「checkpoint が始まらなかった」と等価。
        // dirtyPageCount は診断用の advisory フィールド (recovery では未使用)。
        // IPageManager は集計 API を持たないので 0 を入れる (将来必要になったら拡張)。
        long beginLsn = _wal.WriteCheckpointBegin(_oldestActiveLsn(), dirtyPageCount: 0);
        PhaseInjector?.Invoke(CheckpointPhase.AfterBegin);

        // 2. データページを永続化 (fsync)。途中で kill されると partial 状態だが、End が
        //   まだ書かれていないので recovery は前回 End からやり直し redo で収束する。
        _pageManager.FlushAll();
        PhaseInjector?.Invoke(CheckpointPhase.AfterDataFlush);

        // 3. 索引ページも永続化。索引は同じ page-WAL と単一コンテナに含まれる。
        _indexManager?.FlushAll();
        PhaseInjector?.Invoke(CheckpointPhase.AfterIndexFlush);

        // 4. End sentinel を書いて fsync。これが書かれた時点で「全 page + index が durable」
        //   が保証され、recovery は安心して redo 起点をここまで進められる。
        _wal.WriteCheckpointEnd(beginLsn);
        PhaseInjector?.Invoke(CheckpointPhase.AfterEnd);

        // 5. Begin より前の WAL セグメントを削除する。truncate は End 観測後にしか発生しない
        //   ことが明示的: ここに来た時点で End は既に fsync 済み。
        //   beginLsn - 1 を境界にすることで、Begin/End 自身を含むセグメントは削除対象に
        //   ならない (次回 recovery が End sentinel を読めるようにするため)。
        _wal.Truncate(beginLsn - 1);
        PhaseInjector?.Invoke(CheckpointPhase.AfterTruncate);
        QuiverTelemetry.CheckpointDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        activity?.SetTag("quiver.checkpoint.begin_lsn", beginLsn);
        // 運用上「実際にチェックポイントが完走した」マイルストーンとして残す。
        QuiverEventSource.Log.CheckpointCompleted(beginLsn, sw.Elapsed.TotalMilliseconds);
    }
}

/// <summary>
/// テスト用: チェックポイントの phase 識別子。<see cref="Checkpointer.PhaseInjector"/>
/// 経由でテストが kill point を選ぶのに使う。
/// </summary>
internal enum CheckpointPhase
{
    AfterBegin,
    AfterDataFlush,
    AfterIndexFlush,
    AfterEnd,
    AfterTruncate,
}
