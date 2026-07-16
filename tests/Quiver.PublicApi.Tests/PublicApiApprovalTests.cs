using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using PublicApiGenerator;
using Xunit;

namespace Quiver.PublicApi.Tests;

/// <summary>
/// 安定性保証の対象アセンブリ (docs/api-stability.md §2) の public API surface を
/// テキスト化し、checked-in の baseline (<c>PublicApi/&lt;Assembly&gt;.approved.txt</c>) と比較する
/// 承認テスト。
///
/// public API に差分が出ると test が fail し、<c>&lt;Assembly&gt;.received.txt</c> を出力する。
/// 意図した変更なら received を approved に上書きコミットすることで「明示承認」とする
/// (手順は docs/api-stability.md §6.1)。
/// </summary>
public sealed class PublicApiApprovalTests
{
    public static IEnumerable<object[]> StableAssemblies()
    {
        // 旧 Quiver / Quiver.Api / Quiver.Core は単一 'Quiver' アセンブリに
        // 統合されたため、安定性の対象は 1 アセンブリのみ (3 つの typeof はすべて同一 Assembly を指す)。
        yield return new object[] { typeof(global::Quiver.QuiverDatabase).Assembly };
    }

    [Theory]
    [MemberData(nameof(StableAssemblies))]
    public void PublicApi_matches_approved_baseline(Assembly assembly)
    {
        var options = new ApiGeneratorOptions
        {
            // コンパイラ生成属性のノイズを抑え、差分を意味のある API 変更に集中させる。
            ExcludeAttributes = new[]
            {
                "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
                "System.Diagnostics.DebuggerNonUserCodeAttribute",
                "System.Runtime.CompilerServices.NullableAttribute",
                "System.Runtime.CompilerServices.NullableContextAttribute",
                "System.Runtime.CompilerServices.RefSafetyRulesAttribute",
                "System.Reflection.AssemblyMetadataAttribute",
                "System.Runtime.Versioning.TargetFrameworkAttribute",
            },
        };

        var actual = assembly.GeneratePublicApi(options);
        var assemblyName = assembly.GetName().Name!;

        var approvedPath = Path.Combine(BaselineDir(), $"{assemblyName}.approved.txt");
        var receivedPath = Path.Combine(BaselineDir(), $"{assemblyName}.received.txt");

        var approved = File.Exists(approvedPath)
            ? Normalize(File.ReadAllText(approvedPath))
            : string.Empty;
        var actualNormalized = Normalize(actual);

        if (actualNormalized != approved)
        {
            // 差分があれば received を書き出してから fail させる (diff/承認用)。
            File.WriteAllText(receivedPath, actualNormalized);
            actualNormalized.Should().Be(
                approved,
                $"public API of '{assemblyName}' changed. これが意図した変更なら "
                + $"'{assemblyName}.received.txt' を '{assemblyName}.approved.txt' に上書きして "
                + "コミットすること (docs/api-stability.md §6.1)。");
        }
        else
        {
            // 一致したら古い received を掃除する。
            if (File.Exists(receivedPath))
            {
                File.Delete(receivedPath);
            }
        }
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").TrimEnd('\n');

    private static string BaselineDir([CallerFilePath] string? thisFilePath = null)
    {
        var dir = Path.Combine(Path.GetDirectoryName(thisFilePath)!, "PublicApi");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
