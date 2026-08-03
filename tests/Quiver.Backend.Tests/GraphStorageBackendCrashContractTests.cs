using FluentAssertions;
using Quiver.Backend.Tests.Faults;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// クラッシュと永続性の契約テスト。
/// <see cref="GraphStorageBackendContractTests"/> が機能面を検証するのと同様に、
/// 各 backend のサブクラスで同じシナリオを実行する。
/// </summary>
public abstract class GraphStorageBackendCrashContractTests : IDisposable
{
    private readonly string _dir;
    private readonly IGraphStorageBackendFactory _factory;

    protected GraphStorageBackendCrashContractTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_crash_" + Guid.NewGuid().ToString("N"));
        _factory = CreateFactory();
    }

    public void Dispose()
    {
        // torn shutdown 後はハンドル解放を待ってから確実に削除し、%TEMP% のリークを抑える。
        Faults.TestTempCleanup.DeleteDirectoryRobust(_dir);
    }

    private protected abstract IGraphStorageBackendFactory CreateFactory();

    /// <summary>テスト対象 DB のディレクトリ。backend 固有のパス解決用にサブクラスへ公開する。</summary>
    protected string DatabaseDirectory => _dir;

    protected virtual string DatabasePath => _dir;

    private protected IGraphStorageBackend Open()
        => _factory.Open(DatabasePath, new QuiverDatabaseOptions());

    // ===== (a) Commit → kill → reopen でコミット済みデータを復旧 =====

    [Fact]
    public void Commit_then_kill_then_reopen_recovers_committed_data()
    {
        IGraphStorageBackend? backend = Open();
        VertexId persisted;
        using (var tx = backend.BeginWriteTransaction())
        {
            persisted = tx.CreateVertex("Survivor");
            tx.SetProperty(persisted, "marker", PropertyValue.FromInt64(7L));
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginReadTransaction();
        rtx.VertexExists(persisted).Should().BeTrue();
        rtx.GetProperty(persisted, "marker").Int64Value.Should().Be(7L);
    }

    // ===== (b) 書き込み中の kill 後も DB を再度開ける =====
    //
    // binary backend は commit 直前の PageImage と明示 Commit を記録する。
    // RecoveryManager は Commit のない transaction の PageImage を再生しない。
    // 書き込み途中の kill 後に reopen しても未コミット Vertex の痕跡が残ってはならない。

    [Fact]
    public void KillDuringWrite_database_reopens_cleanly()
    {
        IGraphStorageBackend? backend = Open();
        var tx = backend.BeginWriteTransaction();
        var doomed = tx.CreateVertex("Doomed");
        tx.SetProperty(doomed, "ephemeral", PropertyValue.FromInt64(999L));
        // 意図的に Commit しない。

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginReadTransaction();
        // 例外なく backend を再利用できなければならない。
        AssertUncommittedKillState(rtx, doomed);
    }

    /// <summary>
    /// 書き込み途中の未コミットデータが kill 後に残らないことを検証する。
    /// 将来の backend が strict rollback 契約を本質的に満たせない場合に限り override する。
    /// </summary>
    private protected virtual void AssertUncommittedKillState(
        IReadTransaction tx, VertexId uncommittedVertex)
        => tx.VertexExists(uncommittedVertex).Should().BeFalse(
            "uncommitted writes must not survive a kill (strict rollback contract)");

    // ===== (c) 混合ワークロード途中の kill でもコミット済み prefix を維持 =====

    [Fact]
    public void KillDuringMixedWorkload_committed_writes_survive()
    {
        IGraphStorageBackend? backend = Open();
        VertexId committed;
        using (var tx = backend.BeginWriteTransaction())
        {
            committed = tx.CreateVertex("Committed");
            tx.Commit();
        }

        // 2 本目のトランザクションを未コミットのまま模擬 kill する。
        var dirtyTx = backend.BeginWriteTransaction();
        var dirty = dirtyTx.CreateVertex("Dirty");

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginReadTransaction();
        rtx.VertexExists(committed).Should().BeTrue("committed work must persist");
        AssertUncommittedKillState(rtx, dirty);
    }

    // ===== (d) kill → recover を 100 回反復 =====

    [Fact]
    public void RepeatedKillRecover_100_iterations_no_corruption()
    {
        const int Iterations = 100;
        var ids = new List<VertexId>(Iterations);

        for (int i = 0; i < Iterations; i++)
        {
            IGraphStorageBackend? backend = Open();
            // それまでに書き込んだ全データが残っていることを確認する。
            using (var rtx = backend.BeginReadTransaction())
            {
                foreach (var id in ids)
                {
                    rtx.VertexExists(id).Should().BeTrue(
                        $"iteration {i}: previously committed vertex {id.Value} must still exist");
                }
            }

            VertexId next;
            using (var wtx = backend.BeginWriteTransaction())
            {
                next = wtx.CreateVertex("Iter");
                wtx.SetProperty(next, "i", PropertyValue.FromInt64(i));
                wtx.Commit();
            }
            ids.Add(next);

            KillProcessSimulator.SimulateKill(ref backend);
        }

        using var final = Open();
        using var ftx = final.BeginReadTransaction();
        for (int i = 0; i < Iterations; i++)
        {
            ftx.VertexExists(ids[i]).Should().BeTrue();
            ftx.GetProperty(ids[i], "i").Int64Value.Should().Be(i);
        }
    }

    // ===== (e) 最後の torn write をスキップまたは復旧 =====
    // ファイル配置が異なるため、注入処理は backend ごとに実装する。

    [Fact]
    public void TornLastWrite_skipped_or_recovered()
    {
        IGraphStorageBackend? backend = Open();
        VertexId pre;
        using (var tx = backend.BeginWriteTransaction())
        {
            pre = tx.CreateVertex("Pre");
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);
        InjectTornWriteAtTail();

        // torn tail は正常に replay されるか無視され、reopen は成功しなければならない。
        // 復旧不能なら、黙って破損させず識別可能な例外を送出する。
        IGraphStorageBackend? reopened = null;
        try
        {
            reopened = Open();
            using var rtx = reopened.BeginReadTransaction();
            // torn write より前のコミット済み状態は残っていなければならない。
            rtx.VertexExists(pre).Should().BeTrue("committed-before-tear data must survive");
        }
        catch (CorruptionException) { /* 破損を明示する挙動は許容する */ }
        catch (StorageException) { /* 許容する */ }
        finally
        {
            reopened?.Dispose();
        }
    }

    /// <summary>
    /// 対象 backend の永続化境界となるファイル末尾
    /// (例: 最新の WAL セグメント) に torn write を注入する。
    /// </summary>
    protected abstract void InjectTornWriteAtTail();

    // ===== (f) チェックサム破損を検出して報告 (backend 固有) =====
    // 各 backend が適切なファイルと例外型を選べるよう hook として提供する。

    [Fact]
    public void Checksum_mismatch_detected_and_reported()
    {
        IGraphStorageBackend? backend = Open();
        using (var tx = backend.BeginWriteTransaction())
        {
            tx.CreateVertex("Will_be_corrupted");
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);
        InjectChecksumCorruption();

        // reopen または最初の読み取りでは、(a) 破損を検出して例外を送出するか、
        // (b) 破損レコードをスキップする必要がある。binary WAL では切り詰めまたは
        // checksum 不一致の tail をログ終端として扱う。どちらも契約上許容するが、
        // 破損データを黙って信頼してはならない。
        try
        {
            using var reopened = Open();
            using var rtx = reopened.BeginReadTransaction();
            // ここに到達した場合、復旧処理は torn record を復旧不能としてスキップしている。
            // 少なくとも backend が新しい処理に利用できることを確認する。
        }
        catch (CorruptionException) { /* 許容する */ }
        catch (StorageException) { /* 許容する */ }
    }

    /// <summary>
    /// backend の主要なチェックサム対象構造
    /// (binary backend では WAL レコードヘッダー) の 1 ビットを反転する。
    /// </summary>
    protected abstract void InjectChecksumCorruption();

    // ===== (g) sidecar 削除後に再構築または安全に失敗 (backend 固有) =====

    [Fact]
    public void SidecarDeleted_backend_rebuilds_or_fails_safely()
    {
        IGraphStorageBackend? backend = Open();
        VertexId stable;
        using (var tx = backend.BeginWriteTransaction())
        {
            stable = tx.CreateVertex("Stable");
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);
        DeleteSidecarFiles();

        IGraphStorageBackend? reopened = null;
        try
        {
            reopened = Open();
            using var rtx = reopened.BeginReadTransaction();
            // sidecar が再構築可能な任意データなら、主Vertexは引き続き読み取れる必要がある。
            rtx.VertexExists(stable).Should().BeTrue();
        }
        catch (StorageException) { /* fail-safe なら許容する */ }
        catch (CorruptionException) { /* 許容する */ }
        finally
        {
            reopened?.Dispose();
        }
    }

    /// <summary>
    /// backend 固有の sidecar ファイル (binary では <c>adj.epoch</c>) を削除する。
    /// 実装は、消失から復旧できるか安全に検出できるファイルを選ぶ。
    /// </summary>
    protected abstract void DeleteSidecarFiles();
}
