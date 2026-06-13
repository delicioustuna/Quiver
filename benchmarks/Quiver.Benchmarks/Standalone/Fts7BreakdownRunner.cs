using System.Diagnostics;
using System.Text;
using Quiver;
using Quiver.Api;
using Quiver.Storage;            // PageHeader, PageKind
using Quiver.Storage.Records;    // PropertyValue
using Quiver.Storage.Wal;        // WalReader, WalRecordType, WalPageImageCodec
using Quiver.Text;               // MixedBigramTokenizer, ITokenSink

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// FTS-7 手順1: 取込 WAL 増幅の <b>内訳分解計測 (実測先行 / kill criteria 固定)</b>。
/// FTS-6 の実測 (~33×/chunk) の根因を「どの経路が支配的か」「ランダム挿入のページ touch 数」
/// として数値で確定し、案A (tx 内 postings バッファリング) / 案B (BulkLoader postings 経路) の
/// 選択根拠を与える。推論で結論しない (design 13 §4/§9.1)。
///
/// 計測方法: 全文索引付きで N チャンクを batch tx 取込 (checkpoint OFF → WAL に全 page-image を保持)
/// し、<c>graph.quiver-wal</c> を直接パースする。各 page-logging レコード
/// (<see cref="WalRecordType.PageImage"/> = after-image / <see cref="WalRecordType.CompensationLogRecord"/>
/// = before-image) を、復元ページの <see cref="PageKind"/> でバケット分類して WAL バイトを帰属させる。
/// ARCH-4 で全ストアは単一物理ファイル上の tenant なので WAL の fileKind では分離できない —
/// ページ種別 (PageKind) が postings/norms B+Tree (= BTreeLeaf/Internal) と node/property 本体を分ける。
/// この FT-only ベンチでは B+Tree tenant は postings と norms の 2 つだけ (norms は N 件で provably 極小)
/// なので BTree バイト ≈ postings の寄与。
///
/// 起動方法: <c>dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --fts7 [chunks] [batchSize]</c>
/// (defaults: 5000 chunks, batch 200 — FTS-6 増幅サンプルと同条件)
/// </summary>
public static class Fts7BreakdownRunner
{
    private const string Index = "idx_body";

    public static int Run(int chunks, int batchSize)
    {
        Console.WriteLine("=== FTS-7 手順1: ingest WAL amplification breakdown ===");
        Console.WriteLine($"chunks={chunks:N0}, batchSize={batchSize}, checkpoint=OFF (target ≤ 5×/chunk)");
        Console.WriteLine();

        var vocab = new ZipfVocabulary(Math.Clamp(chunks / 2, 4_000, 60_000));

        // ── plain baseline (no FT index): 本体のみの取込増幅基準 ───────────────────
        long walPlain = MeasureWalSimple(vocab, chunks, batchSize, withIndex: false);

        // ── FT run: WAL を取込後に開いたままパースして内訳分解 ─────────────────────
        var dir = BenchTempDir.Create("fts7_breakdown");
        try
        {
            using var db = GraphDatabase.Open(
                System.IO.Path.Combine(dir, "graph.quiver"),
                new GraphDatabaseOptions { CheckpointThresholdBytes = long.MaxValue });
            db.Schema.CreateFullTextIndex(Index, "Doc", "body");
            double ingestMs = Ingest(db, vocab, chunks, batchSize);

            string walPath = System.IO.Path.Combine(dir, "graph.quiver-wal");
            long walWithFt = WalBytes(walPath);
            var report = AnalyzeWal(walPath);

            Print(report, walWithFt, walPlain, chunks, ingestMs);
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
        return 0;
    }

    // ════════════════════════════════════════════════════════════════════════
    // FTS-7 手順2: spike ゲート (ARIES 変更なしで logical postings WAL の見込み増幅を算出)
    // ════════════════════════════════════════════════════════════════════════
    //
    // kill criteria (design 13 §9.2): batch=200 で ≤5×/chunk 見込み。超過なら term 辞書化を先に設計。
    //
    // 方法論:
    //  1. 現 WAL の leaf page-image を「その tx で初出のページ (= split/新規割当)」と
    //     「既存ページ更新」に分類する。logical postings WAL は **leaf 更新を論理レコード化し、
    //     SMO (split/merge) は page-WAL 維持** (§9.2) なので:
    //       - 既存ページ更新の page-image (before+after) → 論理レコードに置換 = WAL から消える (logical-eligible)
    //       - 初出ページ (= split で新規割当された leaf) の page-image → page-WAL 維持 = 残存 (residual SMO)
    //     (注: split で「あふれた既存 leaf」の書き直しは re-appearance 側に入るため、本分類は
    //      residual SMO をやや過小評価 = projection はやや楽観。境界判定では保守側に解釈する。)
    //  2. 実コーパスを索引と同一トークナイザで再トークナイズし、postings キー数 / term UTF-8 総バイト /
    //     norms 件数を **正確に** 集計 → 論理 payload を算出 (辞書なし / term→termId 辞書あり)。
    //  3. projected_WAL = (FT page-logging − logical-eligible leaf bytes) + logical payload
    //     → projected_amp = projected_WAL / walPlain。batch=10/200/1000 で算出し tx 形状非依存性を確認。

    public static int RunSpike(int chunks, int batchSize)
    {
        Console.WriteLine("=== FTS-7 手順2: logical postings WAL spike gate ===");
        Console.WriteLine($"chunks={chunks:N0}, batchSize={batchSize}, checkpoint=OFF");
        Console.WriteLine("kill criteria (design 13 §9.2): batch=200 で projected ≤ 5×/chunk");
        Console.WriteLine();

        foreach (int batch in new[] { 10, batchSize, 1000 }.Distinct().OrderBy(x => x))
            SpikeOne(chunks, batch);
        return 0;
    }

    private static void SpikeOne(int chunks, int batch)
    {
        var vocab = new ZipfVocabulary(Math.Clamp(chunks / 2, 4_000, 60_000));

        // ── 論理 payload (実コーパス再トークナイズで正確に集計) ───────────────────
        var tok = new MixedBigramTokenizer();
        long postingsKeys = 0, termUtf8Bytes = 0, normsEntries = 0;
        {
            var rng = new Random(11);
            var sink = new DistinctTermSink();
            int written = 0;
            while (written < chunks)
            {
                int b = Math.Min(batch, chunks - written);
                for (int i = 0; i < b; i++)
                {
                    sink.Reset();
                    tok.Tokenize(MakeChunk(vocab, rng), sink);
                    foreach (var t in sink.Terms)
                    {
                        postingsKeys++;
                        termUtf8Bytes += Encoding.UTF8.GetByteCount(t);
                    }
                    normsEntries++;
                }
                written += b;
            }
        }

        // 論理レコード payload (1 レコード = redo+undo 情報を兼ねる; FT-15 eager CLR の論理版は
        // insert の undo = delete なので旧値不要、1 レコード/キーで足りる):
        //   postings: op(1) + indexId(1) + termLen(1) + termBytes + entityId(8) + tf(2)
        //   norms:    op(1) + indexId(1) + entityId(8) + docLen(4)
        long logicalNoDict = postingsKeys * (1 + 1 + 1 + 8 + 2) + termUtf8Bytes
                           + normsEntries * (1 + 1 + 8 + 4);
        //   term→termId 辞書版: termLen+termBytes を termId(4) に置換
        long logicalDict = postingsKeys * (1 + 1 + 4 + 8 + 2)
                         + normsEntries * (1 + 1 + 8 + 4);

        // ── 現 WAL から walPlain / FT page-logging / leaf 分類を実測 ────────────────
        long walPlain = MeasureWalSimple(vocab, chunks, batch, withIndex: false);

        var dir = BenchTempDir.Create("fts7_spike");
        long ftPageLogging, logicalEligibleLeaf, residualSmoLeaf, internalBytes, bodyBytes, walWithFt;
        try
        {
            using var db = GraphDatabase.Open(
                System.IO.Path.Combine(dir, "graph.quiver"),
                new GraphDatabaseOptions { CheckpointThresholdBytes = long.MaxValue });
            db.Schema.CreateFullTextIndex(Index, "Doc", "body");
            Ingest(db, vocab, chunks, batch);
            string walPath = System.IO.Path.Combine(dir, "graph.quiver-wal");
            walWithFt = WalBytes(walPath);
            ClassifyLeaf(walPath, out ftPageLogging, out logicalEligibleLeaf,
                         out residualSmoLeaf, out internalBytes, out bodyBytes);
        }
        finally { BenchTempDir.Delete(dir); }

        // projected: logical-eligible leaf page-image を論理 payload に置換、それ以外は据え置き。
        long projNoDict = ftPageLogging - logicalEligibleLeaf + logicalNoDict;
        long projDict = ftPageLogging - logicalEligibleLeaf + logicalDict;
        double ampNow = walPlain > 0 ? walWithFt / (double)walPlain : double.NaN;
        double ampNoDict = walPlain > 0 ? projNoDict / (double)walPlain : double.NaN;
        double ampDict = walPlain > 0 ? projDict / (double)walPlain : double.NaN;

        Console.WriteLine($"── batch={batch} ─────────────────────────────────────────");
        Console.WriteLine($"postings keys={postingsKeys:N0} ({postingsKeys / (double)chunks:F1}/chunk)  " +
                          $"term UTF-8={termUtf8Bytes:N0}B  norms={normsEntries:N0}");
        Console.WriteLine($"FT page-logging={ftPageLogging:N0}B  of which leaf logical-eligible={logicalEligibleLeaf:N0}B  " +
                          $"residual SMO leaf={residualSmoLeaf:N0}B  internal={internalBytes:N0}B  body={bodyBytes:N0}B");
        Console.WriteLine($"logical payload  no-dict={logicalNoDict:N0}B ({logicalNoDict / (double)chunks:F0}/chunk)  " +
                          $"dict={logicalDict:N0}B ({logicalDict / (double)chunks:F0}/chunk)");
        Console.WriteLine($"amplification    now={ampNow:F2}×   →  projected no-dict={ampNoDict:F2}×   dict={ampDict:F2}×   (target ≤5×)");
        string verdict = ampNoDict <= 5.0 ? "PASS (no-dict)"
                       : ampDict <= 5.0 ? "PASS only with term→termId dict"
                       : "FAIL (>5× even with dict — residual SMO dominates)";
        Console.WriteLine($"verdict: {verdict}");
        Console.WriteLine($"csv-spike,{chunks},{batch},{walPlain},{walWithFt},{ampNow:F2}," +
                          $"{logicalEligibleLeaf},{residualSmoLeaf},{logicalNoDict},{logicalDict},{ampNoDict:F2},{ampDict:F2}");
        Console.WriteLine();
    }

    /// <summary>
    /// WAL を 1 パスし、leaf (BTreeLeaf) page-image を「初出ページ (split/新規割当 → residual SMO)」と
    /// 「既存ページ更新 (logical-eligible)」に分類する。internal / body は据え置き集計。
    /// </summary>
    private static void ClassifyLeaf(string walPath, out long ftPageLogging, out long logicalEligibleLeaf,
                                     out long residualSmoLeaf, out long internalBytes, out long bodyBytes)
    {
        ftPageLogging = logicalEligibleLeaf = residualSmoLeaf = internalBytes = bodyBytes = 0;
        var leafFirstTx = new Dictionary<long, long>();   // pageId -> 初出 txId

        using var reader = new WalReader(walPath, 0L);
        while (reader.TryReadNext(out var rec))
        {
            if (rec.Type != WalRecordType.PageImage && rec.Type != WalRecordType.CompensationLogRecord)
                continue;
            int len = rec.Payload.Length;
            ftPageLogging += len;
            if (!WalPageImageCodec.TryDecode(rec.Payload.Span, out _, out long pageId, out var pageBytes))
                continue;
            PageKind kind = PageHeader.ReadKind(pageBytes);
            long tx = rec.TransactionId.Value;

            if (kind == PageKind.BTreeLeaf)
            {
                if (!leafFirstTx.TryGetValue(pageId, out long firstTx))
                {
                    leafFirstTx[pageId] = tx;       // 初出 → split/新規割当 → residual SMO
                    residualSmoLeaf += len;
                }
                else if (firstTx == tx)
                {
                    residualSmoLeaf += len;         // 同一 tx 内の 2 本目 (CLR↔PageImage) も SMO 側
                }
                else
                {
                    logicalEligibleLeaf += len;     // 既存ページの後続 tx 更新 → 論理化対象
                }
            }
            else if (kind == PageKind.BTreeInternal)
            {
                internalBytes += len;               // SMO で page-WAL 維持
            }
            else
            {
                bodyBytes += len;                   // node/property/page-table/header 等
            }
        }
    }

    /// <summary>チャンク内の distinct term を集める軽量 sink (postings キー数・term バイト集計用)。</summary>
    private sealed class DistinctTermSink : ITokenSink
    {
        public HashSet<string> Terms { get; } = new(StringComparer.Ordinal);
        public void Reset() => Terms.Clear();
        public void Accept(ReadOnlySpan<char> token) => Terms.Add(token.ToString());
    }

    // ────────────────────────────────────────────────────────────────────────
    // WAL 内訳分解
    // ────────────────────────────────────────────────────────────────────────

    private sealed class KindStat
    {
        public long AfterBytes;     // PageImage (after-image) の WAL payload バイト
        public long BeforeBytes;    // CompensationLogRecord (before-image) の WAL payload バイト
        public long AfterRecords;
        public long BeforeRecords;
        public readonly HashSet<long> DistinctPages = new();
        public long TotalBytes => AfterBytes + BeforeBytes;
        public long TotalRecords => AfterRecords + BeforeRecords;
    }

    private sealed class WalReport
    {
        public readonly Dictionary<PageKind, KindStat> ByKind = new();
        public long PageLoggingBytes;       // PageImage + CLR の payload 合計
        public long OtherBytes;             // Begin/Commit/Abort 等の非ページレコード
        public long CommitCount;
        // BTree (postings+norms) ページの tx あたり touch 数 (= ランダム挿入の churn)。
        public readonly List<int> BtreePagesPerTx = new();
        public readonly List<int> BodyPagesPerTx = new();

        public KindStat For(PageKind k)
        {
            if (!ByKind.TryGetValue(k, out var s)) ByKind[k] = s = new KindStat();
            return s;
        }
    }

    private static WalReport AnalyzeWal(string walPath)
    {
        var r = new WalReport();
        long curTx = long.MinValue;
        var txBtreePages = new HashSet<long>();
        var txBodyPages = new HashSet<long>();

        void FlushTx()
        {
            if (curTx == long.MinValue) return;
            r.BtreePagesPerTx.Add(txBtreePages.Count);
            r.BodyPagesPerTx.Add(txBodyPages.Count);
            txBtreePages.Clear();
            txBodyPages.Clear();
        }

        using var reader = new WalReader(walPath, 0L);
        while (reader.TryReadNext(out var rec))
        {
            long tx = rec.TransactionId.Value;
            bool isPageLog = rec.Type == WalRecordType.PageImage
                          || rec.Type == WalRecordType.CompensationLogRecord;

            if (isPageLog)
            {
                if (tx != curTx) { FlushTx(); curTx = tx; }
                int payloadLen = rec.Payload.Length;
                r.PageLoggingBytes += payloadLen;

                if (WalPageImageCodec.TryDecode(rec.Payload.Span, out _, out long pageId, out var pageBytes))
                {
                    PageKind kind = PageHeader.ReadKind(pageBytes);
                    var stat = r.For(kind);
                    if (rec.Type == WalRecordType.PageImage) { stat.AfterBytes += payloadLen; stat.AfterRecords++; }
                    else { stat.BeforeBytes += payloadLen; stat.BeforeRecords++; }
                    stat.DistinctPages.Add(pageId);

                    if (kind is PageKind.BTreeLeaf or PageKind.BTreeInternal) txBtreePages.Add(pageId);
                    else txBodyPages.Add(pageId);
                }
            }
            else
            {
                r.OtherBytes += rec.Payload.Length;
                if (rec.Type == WalRecordType.Commit) r.CommitCount++;
            }
        }
        FlushTx();
        return r;
    }

    private static void Print(WalReport r, long walWithFt, long walPlain, int chunks, double ingestMs)
    {
        double amp = walPlain > 0 ? walWithFt / (double)walPlain : double.NaN;
        Console.WriteLine($"--- totals over {chunks:N0} chunks (ingest {ingestMs:F0} ms) ---");
        Console.WriteLine($"WAL with FT:        {walWithFt,14:N0} bytes  ({walWithFt / (double)chunks,9:F1} /chunk)");
        Console.WriteLine($"WAL plain (no FT):  {walPlain,14:N0} bytes  ({walPlain / (double)chunks,9:F1} /chunk)");
        Console.WriteLine($"amplification:      {amp,14:F2}×   (FTS-6 target ≤ 5×)");
        Console.WriteLine($"page-logging bytes: {r.PageLoggingBytes,14:N0}   non-page (begin/commit/...): {r.OtherBytes:N0}");
        Console.WriteLine($"commits (tx):       {r.CommitCount,14:N0}");
        Console.WriteLine();

        Console.WriteLine("--- WAL page-logging by PageKind (after = PageImage, before = CLR) ---");
        Console.WriteLine($"{"kind",-18}{"total B",13}{"%",7}{"after B",13}{"before B",13}{"recs",9}{"pages",9}{"B/page",9}");
        long totalPageBytes = r.PageLoggingBytes == 0 ? 1 : r.PageLoggingBytes;
        foreach (var kv in r.ByKind.OrderByDescending(k => k.Value.TotalBytes))
        {
            var s = kv.Value;
            double pct = 100.0 * s.TotalBytes / totalPageBytes;
            double bytesPerPage = s.DistinctPages.Count > 0 ? s.TotalBytes / (double)s.DistinctPages.Count : 0;
            Console.WriteLine(
                $"{kv.Key,-18}{s.TotalBytes,13:N0}{pct,6:F1}%{s.AfterBytes,13:N0}{s.BeforeBytes,13:N0}" +
                $"{s.TotalRecords,9:N0}{s.DistinctPages.Count,9:N0}{bytesPerPage,9:F0}");
        }
        Console.WriteLine();

        long btreeBytes = (r.ByKind.TryGetValue(PageKind.BTreeLeaf, out var l) ? l.TotalBytes : 0)
                        + (r.ByKind.TryGetValue(PageKind.BTreeInternal, out var i) ? i.TotalBytes : 0);
        double btreePerChunk = btreeBytes / (double)chunks;
        Console.WriteLine($"FT B+Tree (postings+norms) WAL: {btreeBytes:N0} bytes  ({btreePerChunk:F1} /chunk, " +
                          $"{100.0 * btreeBytes / totalPageBytes:F1}% of page-logging)");
        Console.WriteLine($"cross-check Δ(withFt−plain):     {walWithFt - walPlain:N0} bytes  " +
                          $"({(walWithFt - walPlain) / (double)chunks:F1} /chunk)");
        Console.WriteLine();

        Console.WriteLine("--- per-tx page touch (random-insert churn) ---");
        ReportTouch("BTree pages/tx", r.BtreePagesPerTx);
        ReportTouch("body  pages/tx", r.BodyPagesPerTx);
        Console.WriteLine();

        Console.WriteLine("csv,chunks,walWithFt,walPlain,amplification,btreeBytes,btreePerChunk,beforeBytes,afterBytes");
        long beforeAll = r.ByKind.Values.Sum(s => s.BeforeBytes);
        long afterAll = r.ByKind.Values.Sum(s => s.AfterBytes);
        Console.WriteLine($"csv,{chunks},{walWithFt},{walPlain},{amp:F2},{btreeBytes},{btreePerChunk:F1},{beforeAll},{afterAll}");
    }

    private static void ReportTouch(string label, List<int> samples)
    {
        if (samples.Count == 0) { Console.WriteLine($"{label}: (none)"); return; }
        samples.Sort();
        double avg = samples.Average();
        int p50 = samples[samples.Count / 2];
        int max = samples[^1];
        Console.WriteLine($"{label}: tx={samples.Count}  avg={avg:F1}  p50={p50}  max={max}");
    }

    // ────────────────────────────────────────────────────────────────────────
    // 取込ヘルパ
    // ────────────────────────────────────────────────────────────────────────

    private static long MeasureWalSimple(ZipfVocabulary vocab, int chunks, int batchSize, bool withIndex)
    {
        var dir = BenchTempDir.Create(withIndex ? "fts7_ft" : "fts7_plain");
        try
        {
            using var db = GraphDatabase.Open(
                System.IO.Path.Combine(dir, "graph.quiver"),
                new GraphDatabaseOptions { CheckpointThresholdBytes = long.MaxValue });
            if (withIndex) db.Schema.CreateFullTextIndex(Index, "Doc", "body");
            Ingest(db, vocab, chunks, batchSize);
            return WalBytes(System.IO.Path.Combine(dir, "graph.quiver-wal"));
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static double Ingest(GraphDatabase db, ZipfVocabulary vocab, int chunks, int batchSize)
    {
        var rng = new Random(11);
        var sw = Stopwatch.StartNew();
        int written = 0;
        while (written < chunks)
        {
            int batch = Math.Min(batchSize, chunks - written);
            using var tx = db.BeginTransaction();
            for (int b = 0; b < batch; b++)
            {
                var n = tx.CreateNode("Doc");
                tx.SetProperty(n, "body", PropertyValue.FromString(MakeChunk(vocab, rng)));
            }
            tx.Commit();
            written += batch;
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static long WalBytes(string walPath)
    {
        try { return File.Exists(walPath) ? new FileInfo(walPath).Length : 0; }
        catch { return 0; }
    }

    private static string MakeChunk(ZipfVocabulary vocab, Random rng)
    {
        int len = rng.Next(60, 120);
        var sb = new System.Text.StringBuilder(len * 8);
        for (int i = 0; i < len; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(vocab.Sample(rng));
        }
        return sb.ToString();
    }

    /// <summary>Zipf 分布の閉語彙 (Fts6SearchRunner と同モデル)。</summary>
    private sealed class ZipfVocabulary
    {
        private readonly string[] _terms;
        private readonly double[] _cdf;

        public ZipfVocabulary(int size)
        {
            _terms = new string[size];
            _cdf = new double[size];
            double sum = 0;
            for (int i = 0; i < size; i++)
            {
                _terms[i] = "term" + i.ToString("D5");
                sum += 1.0 / (i + 1);
                _cdf[i] = sum;
            }
            for (int i = 0; i < size; i++) _cdf[i] /= sum;
        }

        public string Sample(Random rng)
        {
            double u = rng.NextDouble();
            int lo = 0, hi = _cdf.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_cdf[mid] < u) lo = mid + 1; else hi = mid;
            }
            return _terms[lo];
        }
    }
}
