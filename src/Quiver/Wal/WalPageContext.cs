using Quiver.Core;

namespace Quiver.Storage.Wal;

/// <summary>
/// 書き込みトランザクション中のページイメージロギング用に使われる、スレッドローカルな
/// WAL コンテキスト。書き込みトランザクション開始時にセットし、Commit / Abort 時にクリアする。
/// </summary>
public static class WalPageContext
{
    [ThreadStatic]
    internal static WriteTransactionContext? Current;

    /// <summary>このスレッドで書き込みトランザクションを開始する。</summary>
    public static void Begin(IWriteAheadLog wal, TransactionId txId)
        => Current = new WriteTransactionContext(wal, txId);

    /// <summary>現在の書き込みコンテキストを破棄する。</summary>
    public static void End() => Current = null;

    /// <summary>
    /// このスレッドで書き込みトランザクションがアクティブな場合、PageImage を
    /// トランザクションごとのバッファに記録する (案C: コアレス)。同じ (fileKind, pageId)
    /// を複数回触っても、保持されるのは最新のページ内容 1 件のみ。WAL への実際の追記は
    /// <see cref="FlushPending"/> (コミット時) まで遅延される。
    /// </summary>
    /// <returns>遅延ロギングのため LSN はまだ確定しない。常に -1 を返す。</returns>
    public static long LogPageImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
        => Current is { } ctx ? ctx.LogPageImage(fileKind, pageId, pageBytes) : -1L;

    /// <summary>
    /// FT-15: あるページが本トランザクション内で初めて書き込み用に pin された時点の
    /// 内容 (before-image) を記録する。書き込みトランザクション未アクティブ時は no-op。
    /// 同一ページの 2 回目以降の pin は無視される (各 savepoint バケットの初回タッチのみ保持)。
    /// </summary>
    public static void CaptureBeforeImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
        => Current?.CaptureBeforeImage(fileKind, pageId, pageBytes);

    /// <summary>
    /// FT-15: 現在の書き込みトランザクションがキャプチャした before-image (CLR ペイロード)
    /// を列挙する。インプロセス abort (= 全 savepoint バケットの巻き戻し) で使う。
    /// 同一ページが複数バケットに現れる場合、最も古い (= tx 開始前 = pre-tx) 状態を 1 件だけ返す。
    /// コンテキスト未設定時は空。
    /// </summary>
    public static IReadOnlyCollection<byte[]> CurrentBeforeImagePayloads
        => Current?.GetAllBeforeImagesOldestWins() ?? Array.Empty<byte[]>();

    /// <summary>
    /// 現在の書き込みトランザクションがバッファした PageImage をすべて WAL へ追記する。
    /// コミット時に <c>Commit</c> レコードを書く直前に呼ぶこと。コンテキスト未設定時は no-op。
    /// </summary>
    public static void FlushPending() => Current?.FlushPending();

    // ──────────────────────────────────────────────────────────────────────
    // FT-23: Savepoint / nested undo
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// FT-23: 新規 savepoint バケットをスタックに push する。返り値はスタック深度
    /// (0-based; 最初の savepoint なら 1)。後続の before-image はこのバケットに
    /// 蓄積され、<see cref="RollbackToSavepoint"/> で巻き戻し対象になる。
    /// アクティブな書き込みコンテキストが無いときは -1 を返す。
    /// </summary>
    public static int PushSavepoint()
        => Current?.PushSavepoint() ?? -1;

    /// <summary>
    /// FT-23: 指定 savepoint レベル以降 (そのレベル自体を含む) のバケットを 1 つに集約して
    /// 返し、スタックから除去する。同一ページが複数バケットに現れる場合は最も古い
    /// (= savepoint 直前の状態) を採用 — RollbackTo はその状態へ page を戻すため。
    /// 集約後に、同レベルに新規空バケットを push し直して savepoint を「有効なまま」維持する
    /// (SQL 標準: ROLLBACK TO は savepoint を消費しない)。
    /// </summary>
    public static IReadOnlyCollection<byte[]> RollbackToSavepoint(int level)
        => Current?.RollbackToSavepoint(level) ?? Array.Empty<byte[]>();

    /// <summary>
    /// FT-23: 指定 savepoint バケットを親バケットへマージし、スタックから除去する。
    /// 親バケットが既に同一ページの before-image を持っている場合は親側 (= 古い) を維持。
    /// </summary>
    public static void ReleaseSavepoint(int level)
        => Current?.ReleaseSavepoint(level);

    /// <summary>
    /// FT-23: 巻き戻し後に <c>_pending</c> (after-image バッファ) を保守する補助。
    /// 与えられたページについて _pending エントリを「巻き戻した内容」で上書きする。
    /// 後続コミット時の WAL PageImage が正しい (rollback 後の) ページ状態を反映する。
    /// </summary>
    public static void OverwritePendingFromBeforeImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
        => Current?.OverwritePendingFromBeforeImage(fileKind, pageId, pageBytes);

    /// <summary>FT-23 (テスト用): 現在の savepoint スタック深度。バケット数を返す。</summary>
    internal static int CurrentDepth => Current?.Depth ?? 0;
}

internal sealed class WriteTransactionContext(IWriteAheadLog wal, TransactionId txId)
{
    private readonly IWriteAheadLog _wal = wal;
    private readonly TransactionId _txId = txId;

    // (fileKind, pageId) → エンコード済み PageImage ペイロード。
    // 案C: 1 トランザクション中に同一ページを何度触っても、コミット時に最新版 1 件だけを
    // WAL に書く。これにより FlushMeta() 等によるホットページの再ログ増幅を解消する。
    // FT-29: FlushPending では Append ではなく WAL の coalesce バッファへ投入することで、
    // 並行 tx 間でも latest-wins de-dup が効くようにする (同一ページに対する書き込みは
    // PagedFile のフレーム X-lock で直列化されるが、FlushPending と Append(Commit) の間に
    // 別 tx が同一ページを上書きするケースに対応)。
    private readonly Dictionary<(byte FileKind, long PageId), byte[]> _pending = new();

    // FT-15 / FT-23: (fileKind, pageId) → エンコード済み before-image (CLR ペイロード)。
    // ページが「そのバケットで最初に」ダーティ化される直前の内容を 1 枚だけ保持する。
    // 値は CompensationLogRecord ペイロードそのものなので、WAL 追記とインプロセス
    // abort/rollback 巻き戻しで同じバッファを共有できる。
    //
    // FT-23: 単一 dict ではなく「バケットのスタック」になった。
    //   - stack[0]                : tx 開始時に push される root バケット (Savepoint なしの tx と同等)。
    //   - stack[0..n]             : Savepoint() ごとに新規バケットが追加される。
    //   - RollbackTo(SP_n)        : 上から SP_n 自身までを巻き戻し、SP_n 位置に空バケットを push し直す。
    //   - ReleaseSavepoint(SP_n)  : SP_n バケットを親へマージし pop する。
    //   - Abort()                 : 全バケットを oldest-wins で巻き戻し (= pre-tx 状態へ)。
    //
    // WAL CLR の重複回避: 同一ページに対する CLR は tx 内で 1 件しか WAL に書かない。
    // (recovery の undo パスは「最終 LSN の CLR が page に適用される」順序で走るため、複数 CLR
    // を書くと最新の CLR (= savepoint 直前) に巻き戻ってしまい、tx 開始前まで戻らない。)
    private readonly List<Dictionary<(byte FileKind, long PageId), byte[]>> _beforeImageStack
        = new() { new Dictionary<(byte FileKind, long PageId), byte[]>() };

    /// <summary>FT-23: 現在の savepoint スタック深度 (root バケット込みのバケット数)。</summary>
    public int Depth => _beforeImageStack.Count;

    /// <summary>
    /// PageImage をトランザクションバッファに記録 (または上書き) する。
    /// FT-29: ペイロードは <see cref="WalPageImageCodec.Encode"/> 経由で v2 形式 (末尾ゼロ trim) に。
    /// 同一ページの 2 回目以降の書き込みは新しい trimmed payload で置き換える
    /// (intra-tx coalesce: latest-wins)。
    /// </summary>
    public long LogPageImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var key = (fileKind, pageId);
        // FT-29: trim 後の payload はページ毎に長さが変わるので、buffer の再利用ではなく
        // 毎回 Encode → 新規 byte[] を割り当てる。sparse page では allocation も小さい (gen0 で安価)。
        _pending[key] = WalPageImageCodec.Encode(fileKind, pageId, pageBytes);
        return -1L;
    }

    /// <summary>
    /// FT-15 / FT-23: ページの before-image を「現在のバケット (= スタック top)」で初回タッチ
    /// 時に 1 度だけ捕捉する。捕捉した内容は
    /// (1) インプロセス abort / RollbackTo の巻き戻し用にバケットへバッファされ、
    /// (2) <see cref="WalRecordType.CompensationLogRecord"/> として、本 tx 内で初めて当該ページが
    ///     触られる場合のみ即時 WAL 追記される (savepoint 越しの 2 度目以降は CLR を出さない —
    ///     recovery の undo は単一 CLR から pre-tx 状態へ戻ることを前提とするため)。
    ///
    /// 案C の after-image バッファ (<see cref="_pending"/>) と異なり、CLR は遅延せず
    /// 即時追記する: コミットも abort もせずクラッシュしたトランザクションでも、
    /// before-image が WAL に残っていなければ巻き戻せないため。
    /// </summary>
    public void CaptureBeforeImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var key = (fileKind, pageId);
        var top = _beforeImageStack[^1];
        if (top.ContainsKey(key)) return; // このバケットでは捕捉済み

        byte[] payload = WalPageImageCodec.Encode(fileKind, pageId, pageBytes);
        top[key] = payload;

        // 下層バケットに既に同ページの before-image がある場合は、CLR は既に WAL へ
        // 書かれている (tx 全体で 1 件のみが正しい)。新規 CLR の WAL 追記は抑止する。
        for (int i = 0; i < _beforeImageStack.Count - 1; i++)
        {
            if (_beforeImageStack[i].ContainsKey(key)) return;
        }
        _wal.Append(WalRecordType.CompensationLogRecord, _txId, payload);
    }

    /// <summary>
    /// バッファした PageImage をすべて WAL の共有 coalesce バッファへ投入し、ローカルの
    /// per-tx バッファをクリアする。FT-29: 実際の WAL 追記は次の Commit / CheckpointBegin /
    /// CheckpointEnd / Abort 出力時にまとめて行われる。同一 (fileKind, pageId) は WAL レベルで
    /// latest-wins de-dup される。
    /// アボート時は呼ばれず、バッファは <see cref="WalPageContext.End"/> による
    /// コンテキスト破棄とともに破棄される (アボートしたトランザクションのページは WAL に残らない)。
    /// </summary>
    public void FlushPending()
    {
        if (_pending.Count == 0) return;
        foreach (var kv in _pending)
            _wal.BufferPageImage(_txId, kv.Key.FileKind, kv.Key.PageId, kv.Value);
        _pending.Clear();
    }

    /// <summary>
    /// FT-23: tx 全体の before-image を「同一ページに対しては最も古いバケットの値が勝つ」
    /// 集約で返す。<c>Abort</c> はこの集約を使うことで、tx 開始前の状態へ正しく戻す。
    /// </summary>
    public IReadOnlyCollection<byte[]> GetAllBeforeImagesOldestWins()
    {
        if (_beforeImageStack.Count == 1) return _beforeImageStack[0].Values;
        var merged = new Dictionary<(byte FileKind, long PageId), byte[]>();
        // bottom-up に走査し、まだ無いキーだけ追加 (= 最も古いバケットの値を採用)。
        for (int i = 0; i < _beforeImageStack.Count; i++)
        {
            foreach (var kv in _beforeImageStack[i])
            {
                if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
            }
        }
        return merged.Values;
    }

    public int PushSavepoint()
    {
        _beforeImageStack.Add(new Dictionary<(byte FileKind, long PageId), byte[]>());
        return _beforeImageStack.Count - 1;
    }

    /// <summary>
    /// FT-23: <paramref name="level"/> 以降 (両端含む) のバケットを集約して返し、スタックから除去。
    /// 同一ページが複数バケットに現れる場合は最も古いバケットの値 (= savepoint 直前の状態) を採用。
    /// 集約後、SQL 標準準拠で同レベルに新規空バケットを push し直す
    /// (ROLLBACK TO は savepoint を消費しないため、同じ id で再度 RollbackTo できる)。
    /// </summary>
    public IReadOnlyCollection<byte[]> RollbackToSavepoint(int level)
    {
        if (level <= 0 || level >= _beforeImageStack.Count)
            return Array.Empty<byte[]>();

        var merged = new Dictionary<(byte FileKind, long PageId), byte[]>();
        for (int i = level; i < _beforeImageStack.Count; i++)
        {
            foreach (var kv in _beforeImageStack[i])
            {
                if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
            }
        }
        _beforeImageStack.RemoveRange(level, _beforeImageStack.Count - level);
        // savepoint は消費しないので同レベルに空バケットを再 push する。
        _beforeImageStack.Add(new Dictionary<(byte FileKind, long PageId), byte[]>());
        return merged.Values;
    }

    /// <summary>
    /// FT-23: 指定 savepoint バケットを親バケットへマージし、スタックから除去する。
    /// 親に同キーが既にあれば古い親側を維持 (oldest-wins)。
    /// </summary>
    public void ReleaseSavepoint(int level)
    {
        if (level <= 0 || level >= _beforeImageStack.Count) return;
        var releasing = _beforeImageStack[level];
        var parent = _beforeImageStack[level - 1];
        foreach (var kv in releasing)
        {
            if (!parent.ContainsKey(kv.Key)) parent[kv.Key] = kv.Value;
        }
        _beforeImageStack.RemoveAt(level);
        // RollbackTo と異なり、Release した savepoint は消費される。
    }

    /// <summary>
    /// FT-23: <see cref="LogPageImage"/> を経由せず _pending を直接更新する。
    /// rollback で <c>WritePageForRecovery</c> によりページが復元された後、後続コミット時の
    /// PageImage が「rollback 後の状態」を WAL に永続化するために呼ぶ。
    /// </summary>
    public void OverwritePendingFromBeforeImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
        => LogPageImage(fileKind, pageId, pageBytes);
}
