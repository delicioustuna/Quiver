namespace Yatagarasu.Backend.Tests.Faults;

/// <summary>
/// crash/chaos テストの temp ディレクトリ後片付けを堅牢化するヘルパ。
///
/// kill シミュレーション後はバックエンドが Dispose されず、MMF / FileStream の OS ハンドルが
/// SafeHandle の finalizer 解放を待つ状態になることがある。この間に <c>Directory.Delete</c> を
/// 呼ぶと「使用中」で失敗し、1 DB あたり最低 64MB (PagedFile の初期確保単位) の残骸が %TEMP% に
/// 蓄積する。本ヘルパは GC + finalizer 待ち + 短いリトライで確実に削除し、リークを抑える。
/// </summary>
internal static class TestTempCleanup
{
    public static void DeleteDirectoryRobust(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // killed backend の MMF / FileStream SafeHandle を finalizer で解放させてから再試行。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(20 * (attempt + 1));
        }

        // 最終試行 (それでも失敗するなら諦める — テスト失敗にはしない)。
        try { Directory.Delete(dir, recursive: true); } catch { /* 失敗を許容する */ }
    }
}
