namespace Quiver.Backend.Tests.Faults;

/// <summary>
/// host process の OS レベル kill をモデル化する fault injector。
///
/// Windows の xUnit runner 内で実際に process kill すると、テストプロセスに引き継がれた
/// file handle が解放されるまで同じプロセスから排他的 FileStream lock を再取得できない。
/// そこで次の手順で kill を近似する:
///   1. <c>Dispose</c> を呼んで OS file handle を解放する。これは正常終了と同じ shutdown 経路だが、
///      Dispose 時の flush に永続性を依存する backend はそもそも契約違反となる。
///   2. 呼び出し側がディレクトリを reopen する前に、GC を 2 周実行して
///      finalizer 管理の handle を解放する。
///
/// この呼び出し前に永続化されたデータ、すなわち
/// <see cref="IGraphTransaction.Commit"/> が成功したデータは reopen 後も復旧できなければならない。
/// 未コミットデータは reopen 後に見えてはならない。
/// </summary>
internal static class KillProcessSimulator
{
    public static void SimulateKill(ref IGraphStorageBackend? backend)
    {
        var b = backend;
        backend = null;
        try { b?.Dispose(); } catch { /* 処理中の close を失う状況も kill の再現対象 */ }

        for (int i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
