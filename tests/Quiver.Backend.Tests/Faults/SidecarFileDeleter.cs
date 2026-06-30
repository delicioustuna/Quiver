namespace Quiver.Backend.Tests.Faults;

/// <summary>
/// DB ディレクトリから sidecar / metadata ファイルを削除する fault injector。
/// backend は欠損ファイルを primary data から再構築するか、明確なエラーで fail-fast する。
/// stale または不完全な状態を使う暗黙の破損は契約違反となる。
/// </summary>
internal static class SidecarFileDeleter
{
    public static bool TryDelete(string path)
    {
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public static int DeleteByExtension(string directory, string extension)
    {
        if (!Directory.Exists(directory)) return 0;
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*" + extension))
        {
            File.Delete(file);
            n++;
        }
        return n;
    }
}
