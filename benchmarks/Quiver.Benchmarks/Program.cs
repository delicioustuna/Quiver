using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using Quiver.Benchmarks;
using Quiver.Benchmarks.Standalone;

// ── ベンチ一時 DB の残骸を起動時に掃除する ──────────────────────────────
// 各ベンチは %TEMP%\quiver_bench\ 配下にダミー DB を作り、その削除を BDN の
// [GlobalCleanup] / [IterationCleanup](BDN がベンチごとに生成する子プロセス
// 内で実行)に委ねている。そのため Ctrl+C・クラッシュ・OOM・タイムアウト
// kill が起きると削除されず、ランダム名ゆえに残骸が次回 run に再利用も
// 上書きもされず単調に蓄積する(実例: %TEMP% に 231 GB)。
// 起動時にルートを丸ごと掃除しておけば、前回 run で kill された残骸を毎回
// 確実に回収できる ― これが per-bench cleanup の取りこぼしに対する最後の砦。
bool skipTempSweep = Environment.GetEnvironmentVariable("QUIVER_BENCH_SKIP_SWEEP") == "1";
if (!skipTempSweep)
{
    BenchTempDir.SweepRoot();
}

// ホストプロセスが Ctrl+C / 正常終了するときにも掃除する。子プロセスは
// BDN が終了時に kill するので、その後ホスト側で 1 回掃けば全ベンチ分が
// 片付く(なお取りこぼしても次回起動時の SweepRoot が回収する)。
if (!skipTempSweep)
{
    Console.CancelKeyPress += (_, _) => BenchTempDir.SweepRoot();
    AppDomain.CurrentDomain.ProcessExit += (_, _) => BenchTempDir.SweepRoot();
}

// full-text search p50 + ingest amplification + WAL bytes/chunk standalone runner.
// Usage: -- --fts6 [chunkCount] [queryCount]   (defaults: 100000 chunks, 500 queries)
if (args.Length >= 1 && args[0] == "--fts6")
{
    int chunkCount = args.Length >= 2 && int.TryParse(args[1], out var c) ? c : 100_000;
    int queryCount = args.Length >= 3 && int.TryParse(args[2], out var q) ? q : 500;
    return Fts6SearchRunner.Run(chunkCount, queryCount);
}

// MergeEdge degree cost standalone runner
if (args.Length >= 1 && args[0] == "--qp3-merge-cost")
{
    return MergeEdgeCostRunner.Run();
}

// 基本性能 (README 性能目標) standalone runner
if (args.Length >= 1 && args[0] == "--basic-perf")
{
    return BasicPerfRunner.Run();
}

// basic-perf に含まれる並行 read scaling の単独再測入口。
if (args.Length >= 1 && args[0] == "--read-scaling")
{
    return ReadScalingRunner.Run();
}

// Wave 4: reader なし writer と 32 snapshot readers 併走時の commit p50 比較。
if (args.Length >= 1 && args[0] == "--single-writer-perf")
{
    return SingleWriterPerfRunner.Run();
}

// payload page pin と slab cache の同一 DB 比較。
// Usage: -- --payload-cache [N] [dim] [queries]
if (args.Length >= 1 && args[0] == "--payload-cache")
{
    int count = args.Length >= 2 && int.TryParse(args[1], out var n) ? n : 10_000;
    int dimensions = args.Length >= 3 && int.TryParse(args[2], out var d) ? d : 768;
    int queryCount = args.Length >= 4 && int.TryParse(args[3], out var q) ? q : 50;
    return PayloadCacheRunner.Run(count, dimensions, queryCount);
}

if (args.Length >= 1 && args[0] == "--scorer-accumulator")
{
    return ScorerAccumulatorRunner.Run();
}

// clean-slate redesign baseline on the QUIVER-SW page-WAL kernel.
// Usage: -- --clean-slate-page-wal-baseline [degree] [traversalIters] [fullTextChunks] [fullTextQueries] [vectorCount] [vectorQueries]
if (args.Length >= 1 && args[0] == "--clean-slate-page-wal-baseline")
{
    return CleanSlatePageWalBaselineRunner.Run(args.Skip(1).ToArray());
}

// clean-slate CSR edge spike using adjacency payload lanes as a base segment approximation.
// Usage: -- --clean-slate-csr-edge-spike [degree] [iterations]
if (args.Length >= 1 && args[0] == "--clean-slate-csr-edge-spike")
{
    return CleanSlateCsrEdgeSpikeRunner.Run(args.Skip(1).ToArray());
}

// clean-slate CSR edge persistence spike for locator, delta, deletion, merge, and recovery contracts.
// Usage: -- --clean-slate-csr-persistence-spike [degree] [pointUpdates] [mergeDeltaCount]
if (args.Length >= 1 && args[0] == "--clean-slate-csr-persistence-spike")
{
    return CleanSlateCsrPersistenceSpikeRunner.Run(args.Skip(1).ToArray());
}

// clean-slate CSR edge product-path integration validation.
// Usage: -- --clean-slate-csr-product-integration [degree] [iterations] [mutationCount]
if (args.Length >= 1 && args[0] == "--clean-slate-csr-product-integration")
{
    return CleanSlateCsrProductIntegrationRunner.Run(args.Skip(1).ToArray());
}

// clean-slate CSR edge integrated merge gate.
// Usage: -- --clean-slate-csr-product-merge-gate [deltaCount] [batchSize] [targetPool]
if (args.Length >= 1 && args[0] == "--clean-slate-csr-product-merge-gate")
{
    return CleanSlateCsrProductIntegrationRunner.RunMergeGate(args.Skip(1).ToArray());
}

// clean-slate CSR edge compact process-kill recovery matrix.
// Usage: -- --clean-slate-csr-compact-recovery-matrix
if (args.Length >= 1 && args[0] == "--clean-slate-csr-compact-recovery-matrix")
{
    return CleanSlateCsrCompactRecoveryMatrixRunner.Run();
}

// child process entry used by the compact recovery matrix.
if (args.Length >= 1 && args[0] == "--clean-slate-csr-compact-recovery-child")
{
    return CleanSlateCsrCompactRecoveryMatrixRunner.RunChild(args.Skip(1).ToArray());
}

// clean-slate full-text/vector segment fan-out spike.
// Usage: -- --clean-slate-segment-spike [fullTextChunks] [fullTextQueries] [vectorCount] [vectorQueries] [segmentCount]
if (args.Length >= 1 && args[0] == "--clean-slate-segment-spike")
{
    return CleanSlateFullTextVectorSegmentSpikeRunner.Run(args.Skip(1).ToArray());
}

if (args.Length >= 1 && args[0] == "--incidence-traversal")
{
    IncidenceTraversalBenchmarks.Run();
    return 0;
}

if (args.Length >= 1 && args[0] == "--nexus-wal")
{
    return NexusWalAmplificationBenchmarks.Run();
}

// co-membership 走査 (製品 API) vs binary 1-hop の p50 gate
if (args.Length >= 1 && args[0] == "--nexus-traversal")
{
    return NexusTraversalBenchmarks.Run();
}

// arity 別 create/setProperty/delete 遅延 + create WAL 増幅 + 高次数 DeleteVertex カスケード
if (args.Length >= 1 && args[0] == "--nexus-write")
{
    return NexusWriteBenchmarks.Run();
}

// 星型Nexus Match vs reified graph pattern の p50 比較
if (args.Length >= 1 && args[0] == "--nexus-match")
{
    return NexusMatchBenchmarks.Run();
}

// JsonExporter.Full は <ResultsDir>/<Class>-report-full.json を出す。
// Quiver.Benchmarks.RegressionCheck はこの形式を読んで baselines/main.json と
// 比較する。default config の Markdown / CSV exporter は残したまま追加する。
var config = DefaultConfig.Instance.AddExporter(JsonExporter.Full);
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
return 0;
