using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 明示ロール対の co-membership block と incidence chain フォールバックの
/// 結果一致、差分反映、再構築境界を検証する。
/// </summary>
public sealed class CoMembershipBlockTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public CoMembershipBlockTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_comembership_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Configured_role_pair_uses_block_and_matches_chain_result()
    {
        using var db = OpenConfigured(_path);
        VertexId subject;
        VertexId expected;
        using (var tx = db.BeginTransaction())
        {
            subject = tx.CreateVertex("Person");
            expected = tx.CreateVertex("Person");
            tx.CreateNexus("Fact", [
                new("Subject", subject),
                new("Object", expected),
            ]);

            // 未コミット create がある transaction は通常 chain へ縮退し、
            // read-your-writes を維持する。
            Query(tx, db, subject).Should().Equal(expected);
            tx.Commit();
        }

        using var read = db.BeginReadOnlyTransaction();
        Query(read, db, subject).Should().Equal(expected);
        var backend = (BinaryGraphStorageBackend)db.BackendInternal;
        backend.CoMembershipReadCountForTest.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Block_path_preserves_type_filter_and_carried_alias()
    {
        using var db = OpenConfigured(_path);
        VertexId subject;
        VertexId factTarget;
        using (var tx = db.BeginTransaction())
        {
            subject = tx.CreateVertex("Person");
            factTarget = tx.CreateVertex("Person");
            VertexId otherTarget = tx.CreateVertex("Person");
            tx.CreateNexus("Fact", [
                new("Subject", subject),
                new("Object", factTarget),
            ]);
            tx.CreateNexus("Other", [
                new("Subject", subject),
                new("Object", otherTarget),
            ]);
            tx.Commit();
        }

        using var read = db.BeginReadOnlyTransaction();
        var traversal = read.G(db.Schema)
            .Vertex(subject)
            .As("origin")
            .Nexuses("Fact", "Subject")
            .OtherMembers("Object");
        traversal.ToList().Should().Equal(factTarget);
        traversal.Select("origin").ToList().Should().Equal(subject);
    }

    [Fact]
    public void Missing_view_falls_back_to_incidence_chain()
    {
        using var db = QuiverDatabase.Open(_path);
        VertexId subject;
        VertexId expected;
        using (var tx = db.BeginTransaction())
        {
            subject = tx.CreateVertex("Person");
            expected = tx.CreateVertex("Person");
            tx.CreateNexus("Fact", [
                new("Subject", subject),
                new("Object", expected),
            ]);
            tx.Commit();
        }

        using var read = db.BeginReadOnlyTransaction();
        Query(read, db, subject).Should().Equal(expected);
        ((BinaryGraphStorageBackend)db.BackendInternal)
            .CoMembershipReadCountForTest.Should().Be(0);
    }

    [Fact]
    public void Savepoint_and_abort_do_not_publish_discarded_members()
    {
        using var db = OpenConfigured(_path);
        VertexId subject;
        VertexId discarded;
        VertexId committed;
        VertexId aborted;
        using (var setup = db.BeginTransaction())
        {
            subject = setup.CreateVertex("Person");
            discarded = setup.CreateVertex("Person");
            committed = setup.CreateVertex("Person");
            aborted = setup.CreateVertex("Person");
            setup.Commit();
        }

        using (var tx = db.BeginTransaction())
        {
            SavepointId savepoint = tx.Savepoint();
            tx.CreateNexus("Fact", [
                new("Subject", subject),
                new("Object", discarded),
            ]);
            tx.RollbackTo(savepoint);
            tx.CreateNexus("Fact", [
                new("Subject", subject),
                new("Object", committed),
            ]);
            tx.Commit();
        }

        using (var tx = db.BeginTransaction())
        {
            tx.CreateNexus("Fact", [
                new("Subject", subject),
                new("Object", aborted),
            ]);
            tx.Rollback();
        }

        using var read = db.BeginReadOnlyTransaction();
        Query(read, db, subject).Should().Equal(committed);
    }

    [Fact]
    public void Reopen_rebuilds_view_from_canonical_records()
    {
        VertexId subject;
        VertexId expected;
        using (var db = OpenConfigured(_path))
        using (var tx = db.BeginTransaction())
        {
            subject = tx.CreateVertex("Person");
            expected = tx.CreateVertex("Person");
            tx.CreateNexus("Fact", [
                new("Subject", subject),
                new("Object", expected),
            ]);
            tx.Commit();
        }

        using var reopened = OpenConfigured(_path);
        using var read = reopened.BeginReadOnlyTransaction();
        Query(read, reopened, subject).Should().Equal(expected);
        ((BinaryGraphStorageBackend)reopened.BackendInternal)
            .CoMembershipReadCountForTest.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Snapshot_recovery_rebuilds_view_from_canonical_records()
    {
        string snapshotPath = Path.Combine(_dir, "snapshot.quiver");
        VertexId subject;
        VertexId expected;
        using (var db = OpenConfigured(_path))
        {
            using (var tx = db.BeginTransaction())
            {
                subject = tx.CreateVertex("Person");
                expected = tx.CreateVertex("Person");
                tx.CreateNexus("Fact", [
                    new("Subject", subject),
                    new("Object", expected),
                ]);
                tx.Commit();
            }
            db.CreateSnapshot(snapshotPath);
        }

        using var recovered = OpenConfigured(snapshotPath);
        using var read = recovered.BeginReadOnlyTransaction();
        Query(read, recovered, subject).Should().Equal(expected);
    }

    [Fact]
    public void Vacuum_rebuild_removes_deleted_entries()
    {
        using var db = OpenConfigured(_path);
        VertexId subject;
        NexusId nexus;
        using (var tx = db.BeginTransaction())
        {
            subject = tx.CreateVertex("Person");
            VertexId target = tx.CreateVertex("Person");
            nexus = tx.CreateNexus("Fact", [
                new("Subject", subject),
                new("Object", target),
            ]);
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.DeleteNexus(nexus);
            tx.Commit();
        }

        db.Vacuum();

        using var read = db.BeginReadOnlyTransaction();
        Query(read, db, subject).Should().BeEmpty();
    }

    private static QuiverDatabase OpenConfigured(string path)
    {
        var options = new QuiverDatabaseOptions();
        options.CoMembershipRolePairs.Add(new("Subject", "Object"));
        return QuiverDatabase.Open(path, options);
    }

    private static List<VertexId> Query(
        IGraphTransaction tx,
        QuiverDatabase db,
        VertexId subject)
        => tx.G(db.Schema)
            .Vertex(subject)
            .Nexuses("Fact", "Subject")
            .OtherMembers("Object")
            .ToList();
}
