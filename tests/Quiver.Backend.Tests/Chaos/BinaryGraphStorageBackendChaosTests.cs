using FluentAssertions;
using Xunit;

namespace Quiver.Backend.Tests.Chaos;

/// <summary>
/// binary backend に対する 100 件以上の chaos scenario。
/// <c>[Trait("Category","Chaos")]</c> により、日常の <c>dotnet test</c> から除外可能
/// (<c>--filter "Category!=Chaos"</c>) で、CI nightly では含める運用。
///
/// 1 シナリオ = (seed, txCount, FaultKind)。failure 時は <see cref="ChaosTraceWriter"/>
/// が <c>chaos-trace.log</c> へ trace を吐き、同じ (seed, fault, txCount) で再現できる。
/// </summary>
[Trait("Category", "Chaos")]
public sealed class BinaryGraphStorageBackendChaosTests
{
    public static IEnumerable<object[]> Scenarios()
    {
        // 12 seed × 10 fault 種別 = 120 scenario。seed ごとに tx 数も変え、
        // 同じ fault 種別でも workload の組み合わせが一様にならないようにする。
        FaultKind[] faults =
        [
            FaultKind.KillOnly,
            FaultKind.KillThenTornWalTail,
            FaultKind.KillThenChecksumFlip,
            FaultKind.KillThenSidecarDelete,
            FaultKind.AbortThenKill,
            FaultKind.CheckpointKillAfterBegin,
            FaultKind.CheckpointKillAfterDataFlush,
            FaultKind.CheckpointKillAfterIndexFlush,
            FaultKind.CheckpointKillAfterEnd,
            FaultKind.CheckpointKillAfterTruncate,
        ];
        int[] seeds = [1, 2, 3, 7, 11, 13, 17, 19, 23, 29, 31, 37];
        foreach (var seed in seeds)
        {
            int txCount = 3 + (seed % 6); // 3..8
            foreach (var fault in faults)
            {
                yield return new object[] { seed, txCount, fault };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Chaos_scenario_recovers_consistently(int seed, int txCount, FaultKind fault)
    {
        var scenario = new ChaosScenario(seed, txCount, fault);
        var runner = new ChaosScenarioRunner(
            openDefault: dir => new BinaryGraphStorageBackendFactory()
                .Open(System.IO.Path.Combine(dir, "graph.quiver"), new QuiverDatabaseOptions()),
            openCheckpointSensitive: dir => new BinaryGraphStorageBackendFactory()
                .Open(System.IO.Path.Combine(dir, "graph.quiver"), new QuiverDatabaseOptions { CheckpointThresholdBytes = 0 }),
            injectorFactory: dir => new BinaryBackendFaultInjector(dir));

        var result = runner.Run(scenario);
        if (!result.Success)
        {
            ChaosTraceWriter.Append(result.Trace);
        }
        result.Success.Should().BeTrue(
            $"scenario {scenario} must satisfy the recovery contract. trace:{Environment.NewLine}{result.Trace}");
    }
}
