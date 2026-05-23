using Quiver.Index;
using Quiver.Storage;
using Quiver.Wal;

namespace Quiver.Transactions;

/// <summary>
/// シャープチェックポイントの実行機構 (案A) + FT-18 索引 flush。
///
/// 手順:
///   1. 全データファイルのダーティページをディスクへフラッシュ (fsync) する。
///   2. FT-18: 全索引ファイルのダーティページも fsync する (これが無いと WAL truncate
///      後に commit 済み索引エントリが恒久消失する)。
///   3. WAL に Checkpoint レコードを書き、fsync する。
///   4. Checkpoint より前の WAL セグメントを truncate (削除) する。
///
/// 順序が重要: データページ + 索引ページを永続化してから WAL を truncate する。逆順だと、
/// クラッシュ時に「データファイル / 索引ファイルにまだ無い変更の redo ログ」を失う。
///
/// チェックポイントを「いつ」打つか (契機) は <see cref="TransactionManager"/> が判断する。
/// 本クラスは「どう」打つか (機構) のみを担当する。
///
/// 前提: 案C により、WAL 上の PageImage はコミット時にまとめて追記される。よって
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
    // FT-18: null 可。null の場合は索引 flush をスキップ (索引を持たないバックエンド向け)。
    private readonly IIndexManager? _indexManager = indexManager;

    /// <summary>
    /// チェックポイントを 1 回実行する。呼び出し側 (<see cref="TransactionManager"/>) が
    /// 同時実行を排他し、アクティブトランザクションが無いことを保証すること。
    /// </summary>
    public void Checkpoint()
    {
        // 1. データページを永続化 (fsync)。
        _pageManager.FlushAll();

        // 2. FT-18: 索引ファイルも永続化。後続の WAL truncate で IndexMutation レコードが
        //    捨てられても、ここで .idx に書き出された内容が durable なので復旧時の起点として
        //    使える。
        _indexManager?.FlushAll();

        // 3. Checkpoint レコードを書いて fsync (WriteCheckpoint 内で FlushTo 済み)。
        long checkpointLsn = _wal.WriteCheckpoint(_oldestActiveLsn(), _wal.CurrentLsn);

        // 4. Checkpoint より前の WAL セグメントを削除する。
        //    checkpointLsn - 1 を境界にすることで、Checkpoint レコード自身を含む
        //    セグメントは (たとえ現行セグメントでなくても) 削除対象にならない。
        _wal.Truncate(checkpointLsn - 1);
    }
}
