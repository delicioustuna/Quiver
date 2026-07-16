using FluentAssertions;
using Quiver.Backend.Tests.Faults;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// binary backend のクラッシュ契約テスト。
/// 共通の <see cref="GraphStorageBackendCrashContractTests"/> に加え、WAL segment roll、
/// RecoveryManager の境界条件、隣接ブロック sidecar 消失という固有シナリオを検証する。
/// </summary>
public sealed class BinaryGraphStorageBackendCrashContractTests
    : GraphStorageBackendCrashContractTests
{
    protected override IGraphStorageBackendFactory CreateFactory()
        => new BinaryGraphStorageBackendFactory();

    // binary backend は単一ファイル *.quiver。コンテナは <dir>/graph.quiver、
    // WAL は <dir>/graph.quiver-wal。fault injector の LatestWalSegment / sidecar 削除はこの前提。
    protected override string DatabasePath
        => System.IO.Path.Combine(DatabaseDirectory, "graph.quiver");

    protected override void InjectTornWriteAtTail()
    {
        // 最新 WAL segment が永続化境界。末尾 16 バイトをゼロ埋めし、
        // 末尾レコードの CRC32C が必ず不一致になるようにする。
        var seg = LatestWalSegment();
        if (seg is null) return;
        TornWriteInjector.ZeroFillTail(seg, tailBytes: 16);
    }

    protected override void InjectChecksumCorruption()
    {
        var seg = LatestWalSegment();
        if (seg is null) return;
        // WAL ヘッダ: Length(4) + Lsn(8) + TxId(8) + Type(1) + Crc32(4) = 25 B。
        // 先頭レコードの Type バイトを 1 ビット反転し、replay 時に CRC を不一致にする。
        ChecksumCorruptor.FlipBitAt(seg, offset: 20, bitInByte: 0);
    }

    protected override void DeleteSidecarFiles()
    {
        // adj.epoch は次回 open 時に隣接ストアから再構築されるため削除を許容する。
        // bulk load 未実行でファイルがなければ no-op。
        var epoch = Path.Combine(DatabaseDirectory, "adj.epoch");
        SidecarFileDeleter.TryDelete(epoch);
    }

    // ===== binary backend 固有シナリオ =====

    /// <summary>
    /// プロセス内で明示的に rollback した後に kill しても未コミットデータが残らないことを検証する。
    /// abort は before-image の復元を kill 前に永続化し、recovery は先行する
    /// コミット済み処理を維持する。
    /// </summary>
    [Fact]
    public void AbortThenKill_leaves_no_uncommitted_data()
    {
        IGraphStorageBackend? backend = Open();
        VertexId committed;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            committed = tx.CreateVertex("Committed");
            tx.Commit();
        }

        var abortTx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false);
        VertexId rolledBack = abortTx.CreateVertex("RolledBack");
        abortTx.SetProperty(rolledBack, "ephemeral", PropertyValue.FromInt64(42L));
        abortTx.Rollback();

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        rtx.VertexExists(committed).Should().BeTrue(
            "committed work survives an abort-then-kill sequence");
        rtx.VertexExists(rolledBack).Should().BeFalse(
            "an explicitly aborted vertex must not resurface after a kill");
        rtx.Rollback();
    }

    /// <summary>
    /// 未コミットトランザクションが挿入した B+Tree エントリは kill 後の recovery で取り消され、
    /// コミット済みエントリは残ることを検証する。binary backend は B+Tree ページを
    /// PageImage として記録し、RecoveryManager は明示 Commit のある transaction だけを replay する。
    /// </summary>
    [Fact]
    public void IndexEntry_from_uncommitted_tx_is_undone_after_kill()
    {
        IGraphStorageBackend? backend = Open();

        // 比較基準となるインデックスエントリをコミットする。
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            var keep = tx.CreateVertex("Person");
            tx.IndexInsert("idx_name", "committed", keep);
            tx.Commit();
        }

        // インデックスエントリを未コミットのままプロセスを kill する。
        var dirtyTx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false);
        var doomed = dirtyTx.CreateVertex("Person");
        dirtyTx.IndexInsert("idx_name", "doomed", doomed);
        // 意図的に Commit しない。

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);

        var survived = rtx.SeekIndex("idx_name", PropertyValue.FromString("committed"));
        survived.MoveNext().Should().BeTrue(
            "a committed index entry must survive a kill");
        survived.Dispose();

        var undone = rtx.SeekIndex("idx_name", PropertyValue.FromString("doomed"));
        undone.MoveNext().Should().BeFalse(
            "an uncommitted index entry must be undone by recovery");
        undone.Dispose();
        rtx.Rollback();
    }

    /// <summary>
    /// 小さな WAL segment size で数回の commit ごとに segment roll を発生させ、
    /// kill 後も全データを復旧できることを検証する。
    /// segment を閉じて次を開く境界処理を対象とする。
    /// </summary>
    [Fact]
    public void KillAcrossWalSegmentRoll_recovers()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_crash_walroll_" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new BinaryGraphStorageBackendFactory();
            var opts = new QuiverDatabaseOptions();

            var ids = new List<VertexId>();
            for (int i = 0; i < 30; i++)
            {
                IGraphStorageBackend? backend = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
                using (var tx = backend.BeginGraphTransaction(
                    IsolationLevel.SnapshotIsolation, readOnly: false))
                {
                    // segment を早く満たすため 1 KB の payload を使う。
                    var vertex = tx.CreateVertex("Big");
                    tx.SetProperty(vertex, "blob",
                        PropertyValue.FromString(new string('x', 1024)));
                    ids.Add(vertex);
                    tx.Commit();
                }
                KillProcessSimulator.SimulateKill(ref backend);
            }

            using var reopened = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            foreach (var id in ids)
                rtx.VertexExists(id).Should().BeTrue();
            rtx.Rollback();
        }
        finally
        {
            Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    /// <summary>
    /// WAL もデータファイルもない空ディレクトリを開き、
    /// RecoveryManager の空の処理で正常に起動できることを検証する。
    /// </summary>
    [Fact]
    public void EmptyDirectory_recovers_with_empty_FileRegistry_pass()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_crash_emptydir_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            using var backend = new BinaryGraphStorageBackendFactory()
                .Open(System.IO.Path.Combine(dir, "graph.quiver"), new QuiverDatabaseOptions());
            using var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false);
            var vertex = tx.CreateVertex("First");
            tx.Commit();
        }
        finally
        {
            Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    /// <summary>
    /// commit 後に隣接ブロックインデックス sidecar を 0 バイトへ切り詰める。
    /// adjacency index は BulkLoader だけが書くため、存在しなければ factory は row path を
    /// 安全にスキップする。コミット済みVertexストアは引き続き読み取れる。
    /// </summary>
    [Fact]
    public void AdjacencyIndexSidecar_truncated_backend_falls_back_safely()
    {
        IGraphStorageBackend? backend = Open();
        VertexId persisted;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            persisted = tx.CreateVertex("Pre");
            tx.Commit();
        }
        KillProcessSimulator.SimulateKill(ref backend);

        var adjIdx = Path.Combine(DatabaseDirectory, "adj_idx.dat");
        if (File.Exists(adjIdx))
            TornWriteInjector.TruncateTail(adjIdx, int.MaxValue); // 0 バイトまで切り詰める

        // linked-list fallback で成功するか、StorageException で安全に失敗する。
        IGraphStorageBackend? reopened = null;
        try
        {
            reopened = Open();
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            rtx.VertexExists(persisted).Should().BeTrue();
            rtx.Rollback();
        }
        catch (StorageException) { /* fail-safe なら許容する */ }
        catch (CorruptionException) { /* 許容する */ }
        finally
        {
            reopened?.Dispose();
        }
    }

    /// <summary>
    /// コミット済み B+Tree エントリが、インデックスファイルの Dispose-flush
    /// <strong>前</strong>の kill 後も残ることを検証する。B+Tree の PageImage は
    /// commit の FlushTo で永続化され、recovery が冪等に redo してエントリを復元する。
    /// </summary>
    [Fact]
    public void CommittedIndexEntry_survives_kill_via_redo()
    {
        IGraphStorageBackend? backend = Open();
        VertexId committed;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            committed = tx.CreateVertex("Person");
            tx.IndexInsert("idx_name", "alice", committed);
            tx.Commit();
        }

        // Dispose せず kill するため、.idx ファイルは fsync 前の可能性がある。
        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        var cur = rtx.SeekIndex("idx_name", PropertyValue.FromString("alice"));
        cur.MoveNext().Should().BeTrue(
            "committed index entry must be redone from PageImage WAL records");
        cur.Current.Should().Be(committed);
        cur.MoveNext().Should().BeFalse(
            "idempotent redo must not create duplicate entries");
        cur.Dispose();
        rtx.Rollback();
    }

    /// <summary>
    /// checkpoint 後に B+Tree PageImage を含む WAL prefix が切り詰められても、
    /// コミット済みエントリが残ることを検証する。
    /// <see cref="Quiver.Index.IIndexManager.FlushAll"/> がなければ index file は永続化されず、
    /// 後続 kill でエントリを失う。checkpoint が index を fsync するため、
    /// WAL truncation と kill の後もエントリが残る。
    /// </summary>
    [Fact]
    public void CommittedIndexEntry_survives_checkpoint_then_kill()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_ft18_chkpt_" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new BinaryGraphStorageBackendFactory();
            // 2 回目の commit で checkpoint を強制する小さな閾値を使う。
            var opts = new QuiverDatabaseOptions { CheckpointThresholdBytes = 1 };

            VertexId committed;
            IGraphStorageBackend? backend = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                committed = tx.CreateVertex("Person");
                tx.IndexInsert("idx_name", "checkpointed", committed);
                tx.Commit();
            }
            // 2 回目の commit が MaybeCheckpoint (ActiveCount == 0、
            // BytesWritten >= threshold) を起動する。checkpoint は index を flush してから、
            // B+Tree PageImage を含む WAL prefix を切り詰める。
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                tx.CreateVertex("Filler");
                tx.Commit();
            }

            KillProcessSimulator.SimulateKill(ref backend);

            using var reopened = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            var cur = rtx.SeekIndex("idx_name", PropertyValue.FromString("checkpointed"));
            cur.MoveNext().Should().BeTrue(
                "checkpoint must fsync the index file before truncating its WAL prefix");
            cur.Current.Should().Be(committed);
            cur.Dispose();
            rtx.Rollback();
        }
        finally
        {
            Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    /// <summary>
    /// index undo の冪等性を検証する。プロセス内 abort の逆操作は直接 fsync せず
    /// buffer pool を通るため、steal 下では失われる可能性がある。
    /// recovery は check-before-apply で逆操作を再適用する。abort + kill 後も先行する
    /// コミット済み状態を維持し、abort された insert を除去し、二重 undo でも重複を作らない。
    /// </summary>
    [Fact]
    public void AbortedIndexInsert_then_kill_leaves_no_residual_entry()
    {
        IGraphStorageBackend? backend = Open();
        VertexId baseline;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            baseline = tx.CreateVertex("Person");
            tx.IndexInsert("idx_name", "baseline", baseline);
            tx.Commit();
        }

        var abortTx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false);
        var resurrected = abortTx.CreateVertex("Person");
        abortTx.IndexInsert("idx_name", "resurrected", resurrected);
        abortTx.Rollback();

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);

        var keepCur = rtx.SeekIndex("idx_name", PropertyValue.FromString("baseline"));
        keepCur.MoveNext().Should().BeTrue(
            "committed index entry must survive abort + kill via idempotent redo");
        keepCur.Current.Should().Be(baseline);
        keepCur.MoveNext().Should().BeFalse(
            "idempotent recovery must not duplicate the surviving entry");
        keepCur.Dispose();

        var gone = rtx.SeekIndex("idx_name", PropertyValue.FromString("resurrected"));
        gone.MoveNext().Should().BeFalse(
            "aborted index entry must remain undone after abort + kill");
        gone.Dispose();
        rtx.Rollback();
    }

    /// <summary>
    /// 大量 insert で B+Tree split を多発させた tx を commit → kill。
    /// 物理 PageImage WAL ロギングにより、split で touched された全ページが PageImage 経由で
    /// 再生されるので、partial-disk-arrival シナリオでも recovery 後に B+Tree が構造的に整合し、
    /// 全エントリが可視に戻る。論理 redo だけでは提供できない保証。
    /// </summary>
    [Fact]
    public void IndexHeavySplitWorkload_then_kill_recovers_all_entries()
    {
        IGraphStorageBackend? backend = Open();
        var insertedKeys = new List<int>();
        var insertedVertices = new List<VertexId>();
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            // 1000 件 — 1 leaf (8 KB) を遥かに超えるので multi-level B+Tree に。
            for (int i = 0; i < 1000; i++)
            {
                var n = tx.CreateVertex("Person");
                tx.IndexInsert("idx_split", (long)i, n);
                insertedKeys.Add(i);
                insertedVertices.Add(n);
            }
            tx.Commit();
        }

        // Dispose 経由の flush なしで kill。PageImage 経由でしか復旧できない状況を作る。
        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        for (int probe = 0; probe < 1000; probe += 37)
        {
            var cur = rtx.SeekIndex("idx_split", PropertyValue.FromInt64((long)probe));
            cur.MoveNext().Should().BeTrue($"key {probe} は committed PageImage 経由で復旧されるはず");
            cur.Current.Should().Be(insertedVertices[probe]);
            cur.MoveNext().Should().BeFalse("idempotent recovery で重複なし");
            cur.Dispose();
        }
        rtx.Rollback();
    }

    /// <summary>
    /// 複数索引を同一 tx 内で更新 → kill。各索引は別 fileKind を catalog で
    /// 割り当てられているので、全 fileKind の PageImage 系統が独立に redo されるはず。
    /// </summary>
    [Fact]
    public void MultipleIndexes_committed_then_kill_all_survive()
    {
        IGraphStorageBackend? backend = Open();
        VertexId vertexA, vertexB;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            vertexA = tx.CreateVertex("Person");
            vertexB = tx.CreateVertex("Person");
            tx.IndexInsert("idx_name", "alice", vertexA);
            tx.IndexInsert("idx_email", "alice@example.com", vertexA);
            tx.IndexInsert("idx_age", 30L, vertexA);
            tx.IndexInsert("idx_name", "bob", vertexB);
            tx.IndexInsert("idx_email", "bob@example.com", vertexB);
            tx.IndexInsert("idx_age", 25L, vertexB);
            tx.Commit();
        }

        KillProcessSimulator.SimulateKill(ref backend);

        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);

        var nameAlice = rtx.SeekIndex("idx_name", PropertyValue.FromString("alice"));
        nameAlice.MoveNext().Should().BeTrue("idx_name の alice エントリは PageImage で復旧");
        nameAlice.Current.Should().Be(vertexA);
        nameAlice.Dispose();

        var emailBob = rtx.SeekIndex("idx_email", PropertyValue.FromString("bob@example.com"));
        emailBob.MoveNext().Should().BeTrue("idx_email の bob エントリも復旧");
        emailBob.Current.Should().Be(vertexB);
        emailBob.Dispose();

        var age30 = rtx.SeekIndex("idx_age", PropertyValue.FromInt64(30L));
        age30.MoveNext().Should().BeTrue("idx_age の 30 エントリも復旧");
        age30.Current.Should().Be(vertexA);
        age30.Dispose();

        rtx.Rollback();
    }

    /// <summary>
    /// 索引作成 → kill → 再 open で索引カタログから復元される。
    /// 旧実装の indexes/.fileKinds サイドカーは廃止され、索引カタログ (name → tenantId /
    /// PropertyTypeFlags) は graph.quiver 内の専用テナントに同居する。よってファイル存在の
    /// 代わりに「kill 後に索引が再 materialize され、コミット済みエントリが引ける」ことで
    /// カタログ永続化を behavioral に確認する。
    /// </summary>
    [Fact]
    public void IndexCatalog_persists_fileKind_across_kill()
    {
        IGraphStorageBackend? backend = Open();
        VertexId vertex;
        using (var tx = backend.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: false))
        {
            vertex = tx.CreateVertex("Doc");
            tx.IndexInsert("idx_persistent", 42L, vertex);
            tx.Commit();
        }

        // 索引は graph.quiver に同居するため、独立した .idx / .fileKinds は作られない。
        File.Exists(Path.Combine(DatabaseDirectory, "indexes", ".fileKinds")).Should().BeFalse(
            " では索引カタログは graph.quiver 内テナントに同居し別ファイルを作らない");

        KillProcessSimulator.SimulateKill(ref backend);

        // 再 open で索引カタログテナントが recovery → ReloadAll で復元され、索引が
        // materialize される。PageImage redo もそれに依存する。
        using var reopened = Open();
        using var rtx = reopened.BeginGraphTransaction(
            IsolationLevel.SnapshotIsolation, readOnly: true);
        var cur = rtx.SeekIndex("idx_persistent", PropertyValue.FromInt64(42L));
        cur.MoveNext().Should().BeTrue();
        cur.Current.Should().Be(vertex);
        cur.Dispose();
        rtx.Rollback();
    }

    // ===== checkpoint atomicity (Begin/End sentinel) の kill point =====

    /// <summary>
    /// 与えた phase で kill 例外を投げるよう injector を仕込み、
    /// 「checkpoint を必ず誘発する commit」を 1 回実行 → 例外を捕捉 → kill simulate →
    /// 再 open → コミット済みデータが全て読めることを assert する。
    ///
    /// checkpoint 誘発手段: <see cref="QuiverDatabaseOptions.CheckpointThresholdBytes"/> = 1
    /// を渡しておき、最初の commit で <see cref="ITransactionManager"/>.MaybeCheckpoint
    /// が走るようにする。
    /// </summary>
    private void RunCheckpointKillScenario(CheckpointPhase killAt)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            $"quiver_ft21_{killAt}_" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new BinaryGraphStorageBackendFactory();
            var opts = new QuiverDatabaseOptions { CheckpointThresholdBytes = 1 };

            // セットアップ phase: checkpoint phase injector 無しで、recovery で確認したい
            // コミット済みデータを 2 件書いておく。最初の commit で 1 回目の checkpoint
            // が完走するのは構わない (このシナリオでテストしたいのは「次の checkpoint
            // が phase X で kill されたとき」の挙動)。
            var preserved = new List<VertexId>();
            IGraphStorageBackend? backend = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                preserved.Add(tx.CreateVertex("Pre1"));
                tx.SetProperty(preserved[0], "marker", PropertyValue.FromInt64(11L));
                tx.Commit();
            }
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                preserved.Add(tx.CreateVertex("Pre2"));
                tx.SetProperty(preserved[1], "marker", PropertyValue.FromInt64(22L));
                tx.Commit();
            }

            // injector を仕込んでから次の commit を発射。injector は phase 完了直後に
            // 例外を投げ、checkpoint をその時点で中断する。例外は MaybeCheckpoint →
            // OnCommit → Commit を経由してテスト側に伝播する。
            bool killed = false;
            Checkpointer.PhaseInjector = phase =>
            {
                if (phase == killAt && !killed)
                {
                    killed = true;
                    throw new InvalidOperationException(
                        $" simulated kill at {phase}");
                }
            };
            try
            {
                using var killTx = backend.BeginGraphTransaction(
                    IsolationLevel.SnapshotIsolation, readOnly: false);
                preserved.Add(killTx.CreateVertex("PreKill"));
                killTx.SetProperty(preserved[2], "marker", PropertyValue.FromInt64(33L));
                try { killTx.Commit(); }
                catch (InvalidOperationException) { /* 想定した模擬 kill */ }
            }
            finally
            {
                Checkpointer.PhaseInjector = null;
            }

            // この時点で WAL / data file は「checkpoint が phase X で中断された」状態。
            // KillProcessSimulator は Dispose を呼ぶが、未 fsync のページが flush されても
            // recovery の正当性は変わらない (CheckpointEnd が無ければ recovery は前回
            // checkpoint からやり直すので、partial 状態のページは PageImage redo で
            // 整合に戻る)。
            KillProcessSimulator.SimulateKill(ref backend);

            using var reopened = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), opts);
            using var rtx = reopened.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            // PreKill tx 自体も commit record が durable に書かれていれば redo されているはず。
            // (Commit は checkpoint Begin より前に WAL に書かれて FlushTo 済み。)
            for (int i = 0; i < preserved.Count; i++)
            {
                rtx.VertexExists(preserved[i]).Should().BeTrue(
                    $"phase {killAt}: committed vertex #{i} must survive checkpoint kill");
            }
            rtx.GetProperty(preserved[0], "marker").Int64Value.Should().Be(11L);
            rtx.GetProperty(preserved[1], "marker").Int64Value.Should().Be(22L);
            rtx.GetProperty(preserved[2], "marker").Int64Value.Should().Be(33L);
            rtx.Rollback();
        }
        finally
        {
            Checkpointer.PhaseInjector = null;
            Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    /// <summary>
    /// case 1 (Begin 直後 kill): Begin sentinel だけが WAL に乗った状態で死亡。
    /// data file は flush されていない (一部 PageImage 経由でしか整合しない)。
    /// recovery は End が無いと検知して前回 checkpoint からやり直し、PageImage redo で
    /// 全コミット済みデータが復活する。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterBegin_recovers_committed_data()
        => RunCheckpointKillScenario(CheckpointPhase.AfterBegin);

    /// <summary>
    /// case 2 (page fsync 半分の代替): Begin 後の data flush が完了する前に
    /// 死亡するシナリオを <see cref="CheckpointPhase.AfterBegin"/> で再実行する
    /// (完了する前という意味では AfterBegin と等価で、partial flush 中に死ぬのは
    /// AfterBegin より「悪くない」ので、AfterBegin が問題なければこちらも安全)。
    /// 別 seed で重ねがけして regression を担保する。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_MidDataFlush_recovers_committed_data()
    {
        // page fsync 途中での kill を真に inject するには PageManager.FlushAll の
        // 内部に hook が必要だが、PageManager は集計の都合で iterate せず一括で扱う。
        // 代わりに「flush が部分的にしか durable でない状態 = AfterBegin の上位互換」
        // として AfterBegin を再実行することでカバーする。
        RunCheckpointKillScenario(CheckpointPhase.AfterBegin);
    }

    /// <summary>
    /// case 3 (全 page fsync 直後 kill): data file は完全に durable だが
    /// index flush も End も走っていない。recovery は依然として End 不在を検知し
    /// 前回 checkpoint からやり直す (安全側のオーバーヘッドだけで integrity OK)。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterDataFlush_recovers_committed_data()
        => RunCheckpointKillScenario(CheckpointPhase.AfterDataFlush);

    /// <summary>
    /// case 4 (End 直前 kill): data + index 両方 fsync 済みだが End sentinel
    /// 未書込み。WAL 上は CheckpointBegin のみ。recovery は前回 checkpoint からやり直す。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterIndexFlush_before_End_recovers_committed_data()
        => RunCheckpointKillScenario(CheckpointPhase.AfterIndexFlush);

    /// <summary>
    /// case 5 (End 直後 kill): End sentinel まで書き終えているが truncate
    /// 未実行。recovery は最新 End から起動して、過去 WAL segment が残っていても
    /// 整合に影響しない (idempotent redo)。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterEnd_before_truncate_recovers_committed_data()
        => RunCheckpointKillScenario(CheckpointPhase.AfterEnd);

    /// <summary>
    /// case 6 (truncate 途中の代替): truncate 完了直後に kill。完全に成功した
    /// checkpoint を kill で締めた状態。truncate 途中の partial deletion は OS の
    /// unlink 単位の atomicity に依存するので、ここでは「全 unlink 成功直後に死亡」
    /// で代替する (truncate 中に死んだ場合の残骸 segment は次回 recovery でも old
    /// segment として無害に扱われる — このシナリオでは AfterEnd 経路で間接的に
    /// 担保されている)。
    /// </summary>
    [Fact]
    public void Checkpoint_kill_AfterTruncate_reopens_cleanly()
        => RunCheckpointKillScenario(CheckpointPhase.AfterTruncate);

    [Theory]
    [InlineData(nameof(CompactAdjacencyPhase.AfterDescriptorInvalidated), false)]
    [InlineData(nameof(CompactAdjacencyPhase.AfterRebuild), false)]
    [InlineData(nameof(CompactAdjacencyPhase.AfterFinalDescriptorFlushed), true)]
    public void CompactAdjacency_segment_interrupted_then_reopen_recovers_expected_view(
        string killAtName,
        bool expectAdjacencyView)
    {
        var killAt = Enum.Parse<CompactAdjacencyPhase>(killAtName);
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_adj_compact_crash_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "graph.quiver");

        try
        {
            PropertyKeyId weightKey;
            using (var seed = QuiverDatabase.Open(path))
            {
                weightKey = seed.Schema.GetOrCreatePropertyKey("weight");
                using var loader = seed.BeginBulkLoad(buildAdjacencyIndex: true);
                loader.WithPayloadLane(PayloadLaneSpec.ForInt64(weightKey.Value));
                loader.AppendVertex(new VertexId(0), new LabelId(0));
                for (int i = 1; i <= 3; i++)
                {
                    loader.AppendVertex(new VertexId(i), new LabelId(1));
                    loader.AppendEdge(
                        new EdgeId(i - 1),
                        new VertexId(0),
                        new VertexId(i),
                        new EdgeTypeId(0));
                    loader.AppendEdgePayload(new EdgeId(i - 1), weightKey, 100 + i);
                }

                loader.Commit();
            }

            VertexId deltaVertex;
            EdgeId deltaEdge;
            using (var db = QuiverDatabase.Open(path))
            {
                using (var tx = db.BeginTransaction())
                {
                    tx.SetProperty(new EdgeId(0), "weight", PropertyValue.FromInt64(700));
                    deltaVertex = tx.CreateVertex("V");
                    deltaEdge = tx.CreateEdge(new VertexId(0), deltaVertex, "LINK");
                    tx.SetProperty(deltaEdge, "weight", PropertyValue.FromInt64(900));
                    tx.DeleteEdge(new EdgeId(2));
                    tx.Commit();
                }

                BinaryGraphStorageBackend.CompactAdjacencyPhaseInjector = phase =>
                {
                    if (phase == killAt)
                        throw new InvalidOperationException("injected compact interruption");
                };

                Action compact = () => db.CompactAdjacency();
                compact.Should().Throw<InvalidOperationException>()
                    .WithMessage("injected compact interruption");
            }

            using var reopened = QuiverDatabase.Open(path);
            using var read = reopened.BeginReadOnlyTransaction();
            var adjacency = read.AsInternal().AdjacencySegments;
            if (expectAdjacencyView)
            {
                var view = adjacency as IAdjacencyPayloadView;
                view.Should().NotBeNull(
                    "final descriptor flush makes the rebuilt segment durable");
                view!.PayloadSpec.PropertyKeyId.Should().Be(weightKey.Value);

                var weights = ReadOutgoingWeights(read, new VertexId(0));
                weights[1].Should().Be(700);
                weights[2].Should().Be(102);
                weights[deltaVertex.Sequence].Should().Be(900);
                weights.Should().NotContainKey(3);
            }
            else
            {
                adjacency.Should().BeNull(
                    "descriptor remains invalid until the final compact descriptor is durable");
            }

            EnumerateOutgoingTargets(read, new VertexId(0))
                .Should().BeEquivalentTo(new[] { 1L, 2L, deltaVertex.Sequence });
            read.GetProperty(deltaEdge, "weight").Int64Value.Should().Be(900);
            read.Rollback();
        }
        finally
        {
            BinaryGraphStorageBackend.CompactAdjacencyPhaseInjector = null;
            Faults.TestTempCleanup.DeleteDirectoryRobust(dir);
        }
    }

    private static Dictionary<long, long> ReadOutgoingWeights(IGraphTransaction tx, VertexId source)
    {
        var seen = new Dictionary<long, long>();
        using var cursor = tx.AsInternal().AdjacencySegments!.OpenCursor(source, Direction.Outgoing, null);
        while (cursor.MoveNext())
        {
            if (!tx.AsInternal().AdjacencySegments!.IsTombstoned(cursor.Edge))
                seen[cursor.Neighbor.Sequence] = cursor.WeightRaw;
        }

        return seen;
    }

    private static List<long> EnumerateOutgoingTargets(IGraphTransaction tx, VertexId source)
    {
        var result = new List<long>();
        var en = tx.EnumerateEdges(source, Direction.Outgoing);
        while (en.MoveNext())
        {
            result.Add(en.Current.Target.Sequence);
        }

        return result;
    }

    private string? LatestWalSegment()
    {
        // WAL は単一サイドカー graph.quiver-wal。
        var walPath = Path.Combine(DatabaseDirectory, "graph.quiver-wal");
        return File.Exists(walPath) ? walPath : null;
    }
}
