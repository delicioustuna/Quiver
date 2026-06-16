using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Telemetry;
using Quiver.Index;
using Quiver.Storage;
using Quiver.Storage.Wal;

namespace Quiver.Transactions;

internal sealed class RecoveryManager : IRecoveryManager
{
    private readonly IPageManager _pageManager;
    private readonly IWriteAheadLog _wal;
    private readonly Dictionary<byte, IPagedFile> _fileRegistry;
    private readonly CommittedTxRegistry? _committedRegistry;

    // FTS-7: 物理相 Recover() が計算した tx 分類 / redo 起点 LSN を保持し (spec: 07_fulltext.md#ft-recovery)、
    // 論理相 RecoverLogical() が FtLeafMutation の redo/undo 判定に再利用する。
    // 重要: 論理相は物理層と同じ **presume-committed** (= committed ∪ (PageImage ∧ ¬Abort)) を使う。
    // 厳格 committed (Commit レコード必須) で分類すると、torn-commit (FlushPending 完了後 Commit レコード
    // 前に crash) した FT 取込 tx が「Pass 0/物理層では committed・可視」なのに論理相だけ loser 扱いで
    // postings を消し、可視文書が検索不能になる (レビュー指摘の CRITICAL)。
    private HashSet<long> _committedTxs = new();
    private HashSet<long> _txsWithPageImage = new();
    private HashSet<long> _abortedTxs = new();
    private long _recoverCheckpointLsn;

    public RecoveryManager(IPageManager pageManager, IWriteAheadLog wal)
        : this(pageManager, wal, []) { }

    public RecoveryManager(
        IPageManager pageManager,
        IWriteAheadLog wal,
        Dictionary<byte, IPagedFile> fileRegistry,
        IIndexManager? indexManager = null,
        CommittedTxRegistry? committedRegistry = null)
    {
        // FT-19: indexManager 引数は呼び出し側互換のため受け取るが、索引が ARIES page-WAL
        // 対象になったので recovery 内では使わない。fileRegistry に索引も登録されている前提で、
        // PageImage / CLR の replay 経路が透過的に索引にも適用される。
        _ = indexManager;
        _pageManager = pageManager;
        _wal = wal;
        _fileRegistry = fileRegistry;
        _committedRegistry = committedRegistry;
    }

    public long Recover()
    {
        // OB-2: crash-recovery-count レートカウンタ。Recover() は起動 1 回につき 1 度呼ばれる。
        QuiverEventSource.Log.CrashRecovery();
        long checkpointLsn = FindLastCheckpointLsn();

        // Pass 0 (FT-26): WAL を走査し、CommittedTxRegistry を再構築する。
        // committed と認識する条件: Commit レコードがある OR (PageImage を持ち、かつ Abort が無い)。
        // 後者は Pass 3 で「PageImage を持つ tx は FlushPending を通過 = データファイルに
        // commit 内容が durable なので、torn Commit レコードでも undo しない」とする扱いと整合させる。
        // これがないと、data file には xmin=T の record があるのに registry に T が居ないため
        // visibility 判定で invisible となり、torn-tail テストで「直前 commit が消える」誤動作になる。
        // 他にも:
        //   - 最小 TxId → RecoveryHorizon (truncate 区間より古い xmin は presumed-committed)
        //   - 最大 TxId → MaxObservedTxId (_nextTxId 起点)
        if (_committedRegistry != null)
        {
            long maxObservedTxId = TransactionId.Bootstrap.Value;
            long minTxIdInWal = long.MaxValue;
            var committedSet = new HashSet<long>();
            var pageImageSet = new HashSet<long>();
            var abortedSet = new HashSet<long>();
            using (var fullReader = _wal.OpenReader(0))
            {
                while (fullReader.TryReadNext(out var record))
                {
                    long txIdValue = record.TransactionId.Value;
                    switch (record.Type)
                    {
                        case WalRecordType.Commit: committedSet.Add(txIdValue); break;
                        case WalRecordType.Abort: abortedSet.Add(txIdValue); break;
                        case WalRecordType.PageImage: pageImageSet.Add(txIdValue); break;
                    }
                    if (txIdValue > 0 && txIdValue > maxObservedTxId)
                        maxObservedTxId = txIdValue;
                    if (txIdValue > 0 && txIdValue < minTxIdInWal)
                        minTxIdInWal = txIdValue;
                }
            }
            foreach (long txId in committedSet)
                _committedRegistry.MarkCommitted(new TransactionId(txId));
            // PageImage を持ち Abort も Commit も無い tx は torn-Commit 扱いで presume-committed。
            foreach (long txId in pageImageSet)
            {
                if (committedSet.Contains(txId)) continue;
                if (abortedSet.Contains(txId)) continue;
                _committedRegistry.MarkCommitted(new TransactionId(txId));
            }
            _committedRegistry.RecordMaxObservedTxId(maxObservedTxId);
            if (minTxIdInWal != long.MaxValue && minTxIdInWal > TransactionId.Bootstrap.Value)
                _committedRegistry.RecoveryHorizon = minTxIdInWal - 1;
        }

        // Pass 1: コミット済み / アボート済みトランザクションを分類し、最終 LSN を求める。
        // PageImage を持つトランザクションも記録する: PageImage (after-image) は
        // Transaction.Commit() のコミットパス (FlushPending) でのみ書かれるため、
        // 「PageImage はあるが Commit が無い」= コミット直前で Commit レコードが torn /
        // 欠落しただけ、と判別できる。クラッシュで宙ぶらりんになった真の未完了
        // トランザクションは FlushPending を通らないので PageImage を 1 件も持たない。
        var committedTxs = new HashSet<long>();
        var abortedTxs = new HashSet<long>();
        var txsWithPageImage = new HashSet<long>();
        long lastLsn = -1;
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                lastLsn = record.Lsn;
                if (record.Type == WalRecordType.Commit)
                    committedTxs.Add(record.TransactionId.Value);
                else if (record.Type == WalRecordType.Abort)
                    abortedTxs.Add(record.TransactionId.Value);
                else if (record.Type == WalRecordType.PageImage)
                    txsWithPageImage.Add(record.TransactionId.Value);
                // FT-19: WalRecordType.IndexMutation は予約値として残置されているが、
                // 新規 WAL には現れず、古い WAL に残っていても recovery では無視する
                // (索引は PageImage / CLR で覆われる)。
            }
        }

        // FTS-7: 論理相 (RecoverLogical) 用に tx 分類 / redo 起点を保持する (presume-committed 計算に使う)。
        _committedTxs = committedTxs;
        _txsWithPageImage = txsWithPageImage;
        _abortedTxs = abortedTxs;
        _recoverCheckpointLsn = checkpointLsn;

        // Pass 2a (物理 redo): コミット済みトランザクションの PageImage (after-image) を replay する。
        // FT-19: 索引 PagedFile も EnableWalLogging により PageImage 経路を共有するため、
        // ここで fileRegistry に登録された索引ファイルにも自動的に redo が適用される。
        // OP-5: FileTruncate も同じパスで replay する。LSN 順に処理することで
        // 「先行 LSN の PageImage で必要なら一度拡張 → 後続 LSN の FileTruncate で再縮減」
        // が再現される (= 物理操作の冪等再生)。
        // FTS-7: FtStructureImage (postings/norms の SMO 構造ページ) は nested
        // top action として **commit/abort を問わず無条件に redo** する (committed フィルタを通さない)。
        // FtLeafMutation がキーの redo/undo を担い、FtStructureImage が構造を担う責務分離。
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                if (record.Type == WalRecordType.PageImage &&
                    committedTxs.Contains(record.TransactionId.Value))
                {
                    ApplyPagePayload(record);
                }
                else if (record.Type == WalRecordType.FtStructureImage)
                {
                    ApplyPagePayload(record); // 無条件 (nested top action)
                }
                else if (record.Type == WalRecordType.FileTruncate)
                {
                    ApplyFileTruncate(record);
                }
            }
        }

        // Pass 3 (undo) — FT-15 Tier2 ARIES undo:
        // クラッシュで宙ぶらりんになった真の未完了トランザクションの CompensationLogRecord
        // (ページ before-image) を再適用し、未コミットの変更を巻き戻す。
        // undo 対象は以下をすべて満たすトランザクションのみ:
        //   - Commit レコードを持たない (コミット済みは redo 済み)
        //   - Abort レコードを持たない (インプロセス abort 済みは巻き戻しが durable)
        //   - PageImage を 1 件も持たない (PageImage があるなら FlushPending を通過した
        //     = コミットパスに入っており、Commit レコードが torn しただけ。これを undo
        //     するとデータファイルへ flush 済みのコミット内容まで消してしまう)
        // FT-19: 索引ファイルの CLR も同じ経路で扱われる (fileRegistry 経由)。
        using (var reader = _wal.OpenReader(checkpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                if (record.Type != WalRecordType.CompensationLogRecord) continue;
                long txId = record.TransactionId.Value;
                if (committedTxs.Contains(txId) ||
                    abortedTxs.Contains(txId) ||
                    txsWithPageImage.Contains(txId))
                    continue;
                ApplyPagePayload(record);
            }
        }

        return lastLsn;
    }

    /// <summary>
    /// FTS-7: recovery 論理相。物理相 <see cref="Recover"/> が FtStructureImage で
    /// FT 木の構造を復元し、IndexManager が 2a 後のヘッダから live `FullTextIndex` を構築した**後**に
    /// 呼ぶ。Pass 2b (committed tx の `FtLeafMutation` を LSN 順に再実行 = state-setting last-write-wins) +
    /// Pass 3 論理 undo (Commit を持たない全 tx = abort 含む loser の `FtLeafMutation` を逆操作、LIFO) を行う。
    /// </summary>
    public void RecoverLogical(IIndexManager indexManager)
    {
        // presume-committed = 物理層 (Pass 0 / 物理 Pass 3 の skip 条件) と同一意味論。
        //   committed (Commit レコード有) ∪ (PageImage 有 ∧ Abort 無) = torn-commit を committed 扱い。
        // torn-commit した FT 取込 tx の body+postings は FlushPending で durable なので、これを redo 対象
        // (= 取りこぼさない) かつ undo 対象外 (= 消さない) にする。明示 Abort 済み tx は presume-committed で
        // 無いので undo される (in-process abort の leaf inverse は durable でなく、inverse は冪等で安全)。
        bool PresumeCommitted(long tx)
            => _committedTxs.Contains(tx) || (_txsWithPageImage.Contains(tx) && !_abortedTxs.Contains(tx));

        // 監査 #1 (recovery clobber, spec: 02_wal_recovery.md#two-phase-recovery): Pass 2b が再適用した presume-committed
        // キーの集合。FtLeafMutation は state-setting last-write-wins なので、これらのキーの権威ある値は
        // Pass 2b で確定している。Pass 3 の loser 論理 undo が同じキーへ逆操作を当てると、committed 値を
        // 旧値で上書き (clobber) してしまう (loser Delete の undo = UpsertRaw(key, 旧値) は無条件上書き)。
        // よって committed キーは undo 対象から除外する。キーは (tenant, keyBytes) で一意。
        var committedKeys = new HashSet<string>();
        static string KeyId(byte tenant, ReadOnlySpan<byte> key)
            => $"{tenant}:{Convert.ToHexString(key)}";

        // Pass 2b (論理 redo): presume-committed tx の FtLeafMutation を LSN 順に再実行する。
        using (var reader = _wal.OpenReader(_recoverCheckpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                if (record.Type != WalRecordType.FtLeafMutation) continue;
                if (!PresumeCommitted(record.TransactionId.Value)) continue;
                if (FtLeafMutationCodec.TryDecode(
                        record.Payload.Span, out var op, out byte tenant, out var key, out long value))
                {
                    committedKeys.Add(KeyId(tenant, key));
                    indexManager.ApplyFtLeafRedo(tenant, op == FtLeafMutationCodec.Op.Upsert, key, value);
                }
            }
        }

        // Pass 3 (論理 undo): presume-committed で無い真の loser tx (明示 Abort 済み + 宙ぶらりん) の
        // FtLeafMutation を逆操作で取り消す。逆順 (LIFO)。inverse も state-setting で冪等なので二重 undo 安全。
        // 監査 #1: ただし Pass 2b が確立した committed キーは skip する (committed 値の clobber 防止)。
        var losers = new List<(byte Tenant, bool IsUpsert, byte[] Key, long Value)>();
        using (var reader = _wal.OpenReader(_recoverCheckpointLsn))
        {
            while (reader.TryReadNext(out var record))
            {
                if (record.Type != WalRecordType.FtLeafMutation) continue;
                if (PresumeCommitted(record.TransactionId.Value)) continue;
                if (FtLeafMutationCodec.TryDecode(
                        record.Payload.Span, out var op, out byte tenant, out var key, out long value))
                {
                    if (committedKeys.Contains(KeyId(tenant, key))) continue; // 監査 #1: committed 値を保護
                    losers.Add((tenant, op == FtLeafMutationCodec.Op.Upsert, key.ToArray(), value));
                }
            }
        }
        for (int i = losers.Count - 1; i >= 0; i--)
            indexManager.ApplyFtLeafUndo(losers[i].Tenant, losers[i].IsUpsert, losers[i].Key, losers[i].Value);

        // 再実行/逆操作はバッファプール経由 (WAL OFF)。durable 化して次回 recovery の起点を確定する
        // (再 crash しても WAL 再生で冪等に再構築できるので必須ではないが、二度手間を避ける)。
        indexManager.FlushAll();
    }

    // FT-21: WAL を 1 回スキャンして、recovery の redo 起点となる LSN を求める。
    //
    // 新形式 (CheckpointBegin/End sentinel):
    //   - 最後の CheckpointEnd の LSN を起点に取る。
    //   - CheckpointBegin だけで対応する End が無い (= partial checkpoint で kill された)
    //     場合は無視し、起点は前回 End のまま (起点を Begin より前に保つことで、partial
    //     状態のページが WAL から redo されて整合に戻る)。
    //
    // 旧形式 (Checkpoint = 100) との互換:
    //   - 1 段で Begin/End を兼ねていた旧 Checkpoint レコードは「End と同等」とみなし
    //     起点を更新する。混在しても (新旧どちらか後発の End/Checkpoint まで進む) のが
    //     LSN 単調なので正しい挙動になる。
    private long FindLastCheckpointLsn()
    {
        long checkpointLsn = 0;
        using var reader = _wal.OpenReader(0);
        while (reader.TryReadNext(out var record))
        {
            if (record.Type == WalRecordType.CheckpointEnd ||
                record.Type == WalRecordType.Checkpoint)
            {
                checkpointLsn = record.Lsn;
            }
            // CheckpointBegin は単独では起点を進めない (End が来て初めて完了とみなす)。
        }
        return checkpointLsn;
    }

    /// <summary>
    /// PageImage (after-image, redo) または CompensationLogRecord (before-image, undo) の
    /// WAL レコードをデコードし、ページバイト列を所有ファイルへ直接書き込む。両者は
    /// <see cref="WalPageImageCodec"/> の同一フォーマットを共有する。
    /// </summary>
    private void ApplyPagePayload(in WalRecord record)
    {
        if (!WalPageImageCodec.TryDecode(
                record.Payload.Span, out byte fileKind, out long pageId, out var pageBytes))
            return;
        if (!_fileRegistry.TryGetValue(fileKind, out var file)) return;
        file.WritePageForRecovery(new PageId(pageId), pageBytes);
    }

    /// <summary>
    /// OP-5: FileTruncate レコードの redo。ペイロードは <c>[fileKind:1][newPageCount:8]</c>。
    /// fileKind が fileRegistry に存在しない / IPagedFile.Truncate が NotSupported を返した
    /// 場合は黙って skip (索引等の未対応 backend でも recovery が止まらないように)。
    /// </summary>
    private void ApplyFileTruncate(in WalRecord record)
    {
        if (record.Payload.Length < 9) return;
        var payload = record.Payload.Span;
        byte fileKind = payload[0];
        long newPageCount = BinaryPrimitives.ReadInt64LittleEndian(payload[1..]);
        if (newPageCount < 1) return;
        if (!_fileRegistry.TryGetValue(fileKind, out var file)) return;
        try
        {
            file.Truncate(newPageCount);
        }
        catch (NotSupportedException)
        {
            // backend が truncate 未対応 — 黙って skip。
        }
    }
}
