using System.Reflection;
using FluentAssertions;
using PublicApiGenerator;
using Xunit;

namespace Yatagarasu.PublicApi.Tests;

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
        // 旧 Yatagarasu / Yatagarasu.Api / Yatagarasu.Core は単一 'Yatagarasu' アセンブリに統合されている。
        // 安定対象の add-on である Yatagarasu.Rag は独立 assembly として固定する。
        yield return new object[] { typeof(global::Yatagarasu.GraphStore).Assembly };
        yield return new object[] { typeof(global::Yatagarasu.Rag.RagStore).Assembly };
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

    [Theory]
    [MemberData(nameof(StableAssemblies))]
    public void PublicApi_does_not_export_storage_ref_struct_or_backend_spi(Assembly assembly)
    {
        Type[] exported = assembly.GetExportedTypes();

        Type[] storageTypes = exported
            .Where(type => type.Namespace is not null
                && type.Namespace.StartsWith("Yatagarasu.Storage", StringComparison.Ordinal))
            .ToArray();
        Type[] byRefLikeTypes = exported.Where(type => type.IsByRefLike).ToArray();
        Type[] backendTypes = exported
            .Where(type => type.Name == "IGraphStorageBackend"
                || type.Name == "IGraphStorageBackendFactory")
            .ToArray();
        string[] removedContractTypes =
        [
            "YatagarasuDatabase",
            "YatagarasuDatabaseOptions",
            "IReadTransaction",
            "IWriteTransaction",
            "ReadTransaction",
            "WriteTransaction",
            "GraphTraversalSource",
            "GraphMutationSource",
            "VertexId",
            "EdgeId",
            "NexusId",
            "EntityRef",
        ];
        Type[] legacyContractTypes = exported
            .Where(type => removedContractTypes.Contains(type.Name, StringComparer.Ordinal))
            .ToArray();

        storageTypes.Should().BeEmpty("storage record and cursor types are implementation details");
        byRefLikeTypes.Should().BeEmpty("the stable public contract must use owned managed values and cursors");
        backendTypes.Should().BeEmpty("backend implementations are selected by product options, not injected as a public SPI");
        legacyContractTypes.Should().BeEmpty("the adopted contract uses GraphStore, owned values, and opaque keys");
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").TrimEnd('\n');

    private static string BaselineDir()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Yatagarasu.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    "tests",
                    "Yatagarasu.PublicApi.Tests",
                    "PublicApi");
            }
        }

        throw new InvalidOperationException(
            $"Yatagarasu repository root containing 'Yatagarasu.slnx' was not found from '{AppContext.BaseDirectory}'.");
    }
}
