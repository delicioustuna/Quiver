using System.Reflection;

namespace Quiver.FuzzTests;

/// <summary>
/// corpus / regressions ディレクトリから fuzz 入力ファイル群を列挙するヘルパ。
/// 各 target ディレクトリ:
///   tests/Quiver.FuzzTests/corpus/&lt;target&gt;/*.bin   — fuzz で発掘された interesting input
///   tests/Quiver.FuzzTests/regressions/&lt;target&gt;/*.bin — 過去 crash の reproducer
/// regressions は必ず commit ごとに走り、corpus は CI nightly の延伸対象。
/// </summary>
internal static class FuzzCorpus
{
    private static readonly string BaseDir = Path.GetDirectoryName(
        typeof(FuzzCorpus).GetTypeInfo().Assembly.Location)!;

    public static IEnumerable<object[]> Enumerate(string targetName)
    {
        foreach (string root in new[] { "regressions", "corpus" })
        {
            string dir = Path.Combine(BaseDir, root, targetName);
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.EnumerateFiles(dir, "*.bin"))
            {
                yield return new object[]
                {
                    Path.Combine(root, targetName, Path.GetFileName(f)),
                    File.ReadAllBytes(f),
                };
            }
        }
    }
}
