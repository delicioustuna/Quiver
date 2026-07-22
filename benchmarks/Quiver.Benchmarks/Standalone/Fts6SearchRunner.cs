using System.Diagnostics;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// BenchmarkDotNet を経由しない短時間ランナーで、設計書 13 §9 の 3 つの
/// 目標値を実測する:
/// <list type="bullet">
///   <item><b>検索 p50/p90/p99</b>: N チャンク・2〜5 term クエリ (バッファプール常駐)。目標 p50 &lt; 10ms。</item>
///   <item><b>取込増幅</b>: 全文索引維持込みの WAL バイト数 ÷ 素の SetProperty の WAL バイト数。目標 5× 以内。</item>
///   <item><b>WAL bytes/chunk</b>: 索引維持込みでチャンク 1 件あたりの WAL バイト数 (回帰 sentinel 値)。</item>
/// </list>
/// checkpoint は閾値を最大化して WAL truncate を止め、取込で発生した物理ログを
/// 全量計測する。WAL は単一サイドカー <c>graph.quiver-wal</c> ()。
///
/// 起動方法: <c>dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --fts6 [chunkCount] [queryCount]</c>
/// </summary>
public static class Fts6SearchRunner
{
    private const string Index = "idx_body";
    private const int BatchSize = 200;     // realistic batch ingest (design §4: 1 tx = 複数チャンク)
    private const int AmpSampleChunks = 5_000;  // bounded sample for the WAL amplification ratio
    private const int MaintenanceIntervalChunks = 10_000;

    public static int Run(int searchChunks, int queryCount)
    {
        Console.WriteLine("=== Full-Text Search / Ingest Amplification ===");
        Console.WriteLine($"searchChunks={searchChunks}, queryCount={queryCount}, batchSize={BatchSize}");
        Console.WriteLine();

        // Realistic corpus model: a large Zipfian vocabulary (natural-language /
        // bigram chunks have thousands+ of distinct terms, not a tiny closed set).
        // A small uniform vocab would make every term df≈N — pathological for
        // term-at-a-time BM25 and unrepresentative of RAG chunks.
        var vocab = new ZipfVocabulary(Math.Clamp(searchChunks / 2, 4_000, 60_000));

        // ── Phase 1: WAL amplification on a bounded sample ───────────────────────
        // Measured with checkpointing OFF so the WAL retains every page image (the
        // total-bytes-written, not the post-truncation footprint). Bounded to
        // AmpSampleChunks so the WAL file stays in the tens-of-MB range — never
        // disable checkpointing for a 100k corpus (multi-GB WAL).
        int ampChunks = Math.Min(searchChunks, AmpSampleChunks);
        long walWithFt = MeasureWal(vocab, ampChunks, withIndex: true, out double ampMsFt);
        long walPlain = MeasureWal(vocab, ampChunks, withIndex: false, out double ampMsPlain);
        double amplification = walPlain > 0 ? walWithFt / (double)walPlain : double.NaN;

        Console.WriteLine($"--- ingest WAL amplification over {ampChunks:N0} chunks, checkpoint OFF (design 13 §9: target ≤ 5×) ---");
        Console.WriteLine($"WAL with FT:        {walWithFt,12:N0} bytes  ({walWithFt / (double)ampChunks,8:F1} bytes/chunk)  ingest {ampMsFt,7:F0} ms");
        Console.WriteLine($"WAL plain:          {walPlain,12:N0} bytes  ({walPlain / (double)ampChunks,8:F1} bytes/chunk)  ingest {ampMsPlain,7:F0} ms");
        Console.WriteLine($"amplification:      {amplification,12:F2}×");
        Console.WriteLine();

        // ── Phase 2: search latency at scale ─────────────────────────────────────
        // Default options → checkpointing keeps the WAL bounded while we build the
        // full search corpus. Search reads the materialized index, so checkpoint
        // policy doesn't affect latency.
        var dirFt = BenchTempDir.Create("fts6_search");
        try
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(dirFt, "graph.quiver"));
            db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(Index, new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));
            double ingestMs = IngestCorpus(db, vocab, searchChunks, seed: 11);
            Console.WriteLine($"built search corpus: {searchChunks:N0} chunks in {ingestMs,7:F0} ms ({searchChunks / (ingestMs / 1000.0),8:F0} chunks/s)");
            Console.WriteLine();

            var latenciesMs = MeasureSearchLatencies(db, vocab, queryCount, seed: 99);
            ReportSearch(latenciesMs, searchChunks);
        }
        finally
        {
            BenchTempDir.Delete(dirFt);
        }

        Console.WriteLine();
        Console.WriteLine("csv,ampChunks,walWithFt,walPlain,bytesPerChunkFt,amplification,searchChunks,queries");
        Console.WriteLine(
            $"csv,{ampChunks},{walWithFt},{walPlain}," +
            $"{walWithFt / (double)ampChunks:F1},{amplification:F2},{searchChunks},{queryCount}");
        return 0;
    }

    private static long MeasureWal(ZipfVocabulary vocab, int chunkCount, bool withIndex, out double ingestMs)
    {
        var dir = BenchTempDir.Create(withIndex ? "fts6_amp_ft" : "fts6_amp_plain");
        try
        {
            using var db = QuiverDatabase.Open(
                System.IO.Path.Combine(dir, "graph.quiver"),
                new QuiverDatabaseOptions { CheckpointThresholdBytes = long.MaxValue });
            if (withIndex) db.EditSchema(schema => schema.CreateIndex(new FullTextIndexDefinition(Index, new PropertyTarget(PropertyOwnerKind.Vertex, "body", "Doc"))));
            ingestMs = IngestCorpus(db, vocab, chunkCount, seed: 11);
            return WalBytes(dir);
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static double IngestCorpus(QuiverDatabase db, ZipfVocabulary vocab, int chunkCount, int seed)
    {
        var rng = new Random(seed);
        var sw = Stopwatch.StartNew();
        int written = 0;
        while (written < chunkCount)
        {
            int batch = Math.Min(BatchSize, chunkCount - written);
            using var tx = db.BeginWriteTransaction();
            for (int b = 0; b < batch; b++)
            {
                var n = tx.CreateVertex("Doc");
                tx.SetProperty(n, "body", PropertyValue.FromString(MakeChunk(vocab, rng)));
            }
            tx.Commit();
            written += batch;

            // Durable segment bodies are append-only until vacuum retires manifests that no
            // reader can observe. A scale benchmark must exercise that production maintenance
            // path, otherwise repeated merges retain O(N^2) historical artifacts and measure
            // temporary disk exhaustion instead of steady-state ingest/search behavior.
            if (chunkCount > AmpSampleChunks
                && written % MaintenanceIntervalChunks == 0)
            {
                var backend = (BinaryGraphStorageBackend)db.BackendInternal;
                backend.WaitForFullTextSegmentMergeForTest();
                if (backend.FullTextSegmentMergeErrorForTest is Exception mergeError)
                    throw new InvalidOperationException(
                        "Full-text segment merge failed during benchmark maintenance.",
                        mergeError);
                db.Vacuum();
            }
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static List<double> MeasureSearchLatencies(
        QuiverDatabase db, ZipfVocabulary vocab, int queryCount, int seed)
    {
        var rng = new Random(seed);
        // collect stats so the per-term (df, maxTf) snapshot is available and the
        // text-first operator takes the WAND pruning path (G(schema) without stats stays on
        // the full term-at-a-time scan — the  baseline).
        var stats = db.CollectStats();
        using var rtx = db.BeginReadTransaction();
        var g = rtx.Query.WithStats(stats);

        // Warmup so the buffer pool is resident before timing (design §9 premise).
        for (int i = 0; i < Math.Min(20, queryCount); i++)
            _ = g.Search(Index, MakeQuery(vocab, rng, 2, 5), k: 20).ToList();

        var latencies = new List<double>(queryCount);
        var sw = new Stopwatch();
        for (int i = 0; i < queryCount; i++)
        {
            string q = MakeQuery(vocab, rng, 2, 5);
            sw.Restart();
            var hits = g.Search(Index, q, k: 20).ToList();
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            GC.KeepAlive(hits);
        }
        latencies.Sort();
        return latencies;
    }

    private static void ReportSearch(List<double> sortedMs, int chunkCount)
    {
        Console.WriteLine($"--- search latency over {chunkCount:N0} chunks (design 13 §9: target p50 < 10ms) ---");
        Console.WriteLine($"queries:            {sortedMs.Count}");
        Console.WriteLine($"p50:                {Percentile(sortedMs, 0.50),9:F3} ms");
        Console.WriteLine($"p90:                {Percentile(sortedMs, 0.90),9:F3} ms");
        Console.WriteLine($"p99:                {Percentile(sortedMs, 0.99),9:F3} ms");
        Console.WriteLine($"max:                {sortedMs[^1],9:F3} ms");
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        int idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        idx = Math.Clamp(idx, 0, sorted.Count - 1);
        return sorted[idx];
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

    private static string MakeQuery(ZipfVocabulary vocab, Random rng, int minTerms, int maxTerms)
    {
        int terms = rng.Next(minTerms, maxTerms + 1);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < terms; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(vocab.Sample(rng));
        }
        return sb.ToString();
    }

    private static long WalBytes(string dir)
    {
        var wal = Path.Combine(dir, "graph.quiver-wal");
        try { return File.Exists(wal) ? new FileInfo(wal).Length : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// A Zipf-distributed closed vocabulary: term rank <c>r</c> (1-based) is drawn
    /// with probability ∝ 1/r, the classic model for natural-language term
    /// frequency. Both corpus and queries sample from it, so query terms span the
    /// realistic mix of common (high-df) and rare (low-df) terms — exactly what
    /// stresses term-at-a-time BM25, whose cost tracks postings-list length.
    /// </summary>
    private sealed class ZipfVocabulary
    {
        private readonly string[] _terms;
        private readonly double[] _cdf;   // normalized cumulative weights

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
