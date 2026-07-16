using System;
using System.IO;
using System.Threading;

namespace Quiver.Benchmarks;

/// <summary>
/// ベンチ用の一時 DB ディレクトリを一元管理するヘルパ。
///
/// 【なぜこのクラスが必要か — %TEMP% リークの構造的原因】
/// 各ベンチは %TEMP% にダミー DB を作るが、その削除は BenchmarkDotNet の
/// [GlobalCleanup] / [IterationCleanup] に依存している。これらの属性は
/// BDN が各ベンチごとに生成・起動する「子プロセス内」で実行されるため、
/// Ctrl+C・ベンチのクラッシュ・OOM・BDN のタイムアウト kill が起きると
/// 一切実行されない。ディレクトリ名は毎回ランダム(Guid)なので、漏れた
/// 残骸は次回 run に再利用も上書きもされず単調に積み上がる
/// (実例: %TEMP% に 231 GB 蓄積。とくに BulkLoadBenchmarks は 10M edge ×
///  3 DB を作るためサイズが GB 級になり被害が大きい)。
///
/// 対策として、全ベンチの一時 DB を <see cref="Root"/>(%TEMP%\quiver_bench\)
/// という単一ルート配下に集約する。単一ルートにすることで:
///   1. Program 起動時に <see cref="SweepRoot"/> でルートを丸ごと削除すれば、
///      前回 run で kill された残骸を毎回確実に回収できる。これが per-bench
///      cleanup を取りこぼした場合の最後の砦になる。
///   2. プロセス終了フック(ProcessExit / CancelKeyPress)からも 1 回の
///      削除で全ベンチ分をまとめて片付けられる。
/// </summary>
internal static class BenchTempDir
{
    /// <summary>
    /// 全ベンチ共通の一時 DB ルート(%TEMP%\quiver_bench\)。
    /// 個別のランダム名ディレクトリをすべてこの配下に置くことで、起動時 /
    /// 終了時に 1 回の削除で残骸を回収できるようにしている。
    /// </summary>
    public static readonly string Root =
        Path.Combine(Path.GetTempPath(), "quiver_bench");

    /// <summary>
    /// <paramref name="prefix"/> + ランダム suffix で <see cref="Root"/> 配下の
    /// パスを 1 つ返す。ディレクトリ実体は作らない(QuiverDatabase.Open など
    /// 呼び出し側が必要に応じて作る)。ルートだけは確実に存在させておく。
    /// </summary>
    public static string Create(string prefix)
    {
        // ルートは起動時 SweepRoot で消えている可能性があるため毎回作り直す。
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, prefix + "_" + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>
    /// 一時 DB ディレクトリを削除する(リトライ付き)。
    /// 各ベンチの [GlobalCleanup] / [IterationCleanup] から呼ぶ。
    ///
    /// 【なぜリトライと例外握り潰しが必要か】
    /// storage 層は MemoryMappedFile(PagedFile / WAL)を使う。Windows では
    /// mmap view のアンマップが対応 SafeHandle の GC finalize 完了まで遅延
    /// しうるため、DB を Dispose した直後の Directory.Delete が IOException
    /// (ファイル使用中)で失敗することがある。GC を促してから数回リトライ
    /// することで、この取りこぼしを防ぐ。
    /// また、cleanup 中に例外を投げると BDN の run 自体が中断し、かえって
    /// 他ベンチ分の残骸を増やすため、最終的には例外を握り潰す。消し残しは
    /// 次回起動時の <see cref="SweepRoot"/> が回収する。
    /// </summary>
    public static void Delete(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // mmap ハンドルの解放待ち: GC を促してから少し待って再試行する。
                // GC.Collect はベンチ計測区間の外(cleanup)なので測定値に影響しない。
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        // 最終試行。ここでも失敗したら諦める(次回起動時の SweepRoot が回収)。
        try { Directory.Delete(path, recursive: true); }
        catch { /* 起動時 SweepRoot に委ねる */ }
    }

    /// <summary>
    /// <see cref="Root"/> 配下の残骸をすべて削除する。Program 起動時と
    /// プロセス終了フックから呼ぶ。kill で per-bench cleanup を逃しても、
    /// 次回 run のこの呼び出しが前回分を確実に回収する。
    /// </summary>
    public static void SweepRoot()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // ルート一括削除が一部のロックで失敗しても、ロックされていない
            // サブディレクトリは個別なら消せる。残りは次回 run に委ねる。
            try
            {
                foreach (var dir in Directory.GetDirectories(Root))
                    Delete(dir);
            }
            catch { /* 次回起動時の SweepRoot に委ねる */ }
        }
    }
}
