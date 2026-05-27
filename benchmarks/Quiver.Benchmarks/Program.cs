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
BenchTempDir.SweepRoot();

// ホストプロセスが Ctrl+C / 正常終了するときにも掃除する。子プロセスは
// BDN が終了時に kill するので、その後ホスト側で 1 回掃けば全ベンチ分が
// 片付く(なお取りこぼしても次回起動時の SweepRoot が回収する)。
Console.CancelKeyPress         += (_, _) => BenchTempDir.SweepRoot();
AppDomain.CurrentDomain.ProcessExit += (_, _) => BenchTempDir.SweepRoot();

// FT-20: standalone runner (BDN を経由せず短時間で参考値計測)
if (args.Length >= 1 && args[0] == "--ft20-wal")
{
    return FT20WalAmplificationRunner.Run();
}

// FT-24: lock contention (shared vs exclusive) standalone runner
if (args.Length >= 1 && args[0] == "--ft24-lock")
{
    return LockContentionRunner.Run();
}

// FT-25: deadlock detection latency / CPU overhead standalone runner
if (args.Length >= 1 && args[0] == "--ft25-deadlock")
{
    return DeadlockDetectionRunner.Run();
}

// FT-26: MVCC single-tx write throughput standalone runner
if (args.Length >= 1 && args[0] == "--ft26-mvcc")
{
    return Ft26MvccThroughputRunner.Run();
}

// FT-27: WAL group commit throughput standalone runner
if (args.Length >= 1 && args[0] == "--ft27-groupcommit")
{
    return Ft27GroupCommitRunner.Run();
}

// FT-29: Per-tx PageImage coalescing standalone runner
if (args.Length >= 1 && args[0] == "--ft29-coalesce")
{
    return Ft29PageImageCoalesceRunner.Run();
}

// TS-6: JsonExporter.Full は <ResultsDir>/<Class>-report-full.json を出す。
// Quiver.Benchmarks.RegressionCheck はこの形式を読んで baselines/main.json と
// 比較する。default config の Markdown / CSV exporter は残したまま追加する。
var config = DefaultConfig.Instance.AddExporter(JsonExporter.Full);
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
return 0;
