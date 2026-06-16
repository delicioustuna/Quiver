using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// Runs the BA-2 backend contract suite against the default binary backend.
/// </summary>
public sealed class BinaryGraphStorageBackendContractTests : GraphStorageBackendContractTests
{
    protected override IGraphStorageBackendFactory CreateFactory()
        => new BinaryGraphStorageBackendFactory();

    // ARCH-4 増分8: binary backend は単一ファイル <dir>/graph.quiver を開く。
    protected override string DatabasePath
        => System.IO.Path.Combine(DatabaseDirectory, "graph.quiver");

    /// <summary>
    /// FT-15 Tier1: rollback must roll back page-backed store metadata (the node
    /// store high-water mark), so the slot freed by the aborted CreateNode is
    /// reused by the next committed transaction. This is a binary-specific
    /// guarantee — id assignment is backend-defined — so it lives here rather
    /// than in the shared contract suite.
    /// </summary>
    [Fact]
    public void Rollback_rolls_back_store_high_water_mark()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_ft15_hwm_" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new BinaryGraphStorageBackendFactory();
            using var backend = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), new GraphDatabaseOptions());

            NodeId discarded;
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                discarded = tx.CreateNode("Discarded");
                tx.Rollback();
            }

            NodeId reused;
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                reused = tx.CreateNode("Fresh");
                tx.Commit();
            }

            reused.Should().Be(discarded,
                "rollback must restore the node store high-water mark so the id is reused");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// FT-17: an index entry inserted by a rolled-back transaction must not be
    /// visible to <c>SeekIndex</c> afterwards.
    /// </summary>
    [Fact]
    public void IndexInsert_rolled_back_is_not_visible()
    {
        RunInTempBackend(backend =>
        {
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                var n = tx.CreateNode("Item");
                tx.IndexInsert("idx_score", 42L, n);
                tx.Rollback();
            }

            using var rtx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            var en = rtx.SeekIndex("idx_score", PropertyValue.FromInt64(42L));
            en.MoveNext().Should().BeFalse(
                "a rolled-back IndexInsert must leave no index entry");
            en.Dispose();
            rtx.Rollback();
        });
    }

    /// <summary>
    /// FT-17 課題2 (最も危険): rollback restores the node store high-water mark so
    /// the freed slot is reused. If the rolled-back index entry survived, it would
    /// now silently alias a different, valid node. Verify the entry is gone.
    /// </summary>
    [Fact]
    public void Rollback_prevents_stale_index_entry_aliasing()
    {
        RunInTempBackend(backend =>
        {
            NodeId discarded;
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                discarded = tx.CreateNode("Person");
                tx.IndexInsert("idx_name", "alice", discarded);
                tx.Rollback();
            }

            NodeId reused;
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                // Reuses the slot freed by the rolled-back node (FT-15).
                reused = tx.CreateNode("Person");
                tx.Commit();
            }
            reused.Should().Be(discarded, "FT-15: the freed slot is reused");

            using var rtx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: true);
            var en = rtx.SeekIndex("idx_name", PropertyValue.FromString("alice"));
            var hits = new List<NodeId>();
            while (en.MoveNext()) hits.Add(en.Current);
            en.Dispose();
            hits.Should().BeEmpty(
                "the rolled-back index entry must not alias the node that reused its slot");
            rtx.Rollback();
        });
    }

    /// <summary>
    /// FT-17 課題3: with a registered index, <c>MergeNode</c> uses an index seek to
    /// decide create-vs-find. A rolled-back merge must leave no stale index entry,
    /// otherwise the next merge of the same key would wrongly find a ghost.
    /// </summary>
    [Fact]
    public void MergeNode_with_index_upsert_is_correct_across_rollback()
    {
        RunInTempBackend(backend =>
        {
            backend.Schema.CreateIndex(
                "idx_person_email", "Person", "email", IndexKind.StringEquality);

            // A merge that creates a node + index entry, then rolls back.
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                var (_, created) = tx.MergeNode(
                    "Person", "email", PropertyValue.FromString("a@x.com"));
                created.Should().BeTrue();
                tx.Rollback();
            }

            // The rolled-back merge left no stale index entry, so this must CREATE.
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                var (_, created) = tx.MergeNode(
                    "Person", "email", PropertyValue.FromString("a@x.com"));
                created.Should().BeTrue(
                    "a rolled-back MergeNode must not leave a stale index entry");
                tx.Commit();
            }

            // Now committed — a later merge of the same key must FIND it.
            using (var tx = backend.BeginGraphTransaction(
                IsolationLevel.SnapshotIsolation, readOnly: false))
            {
                var (_, created) = tx.MergeNode(
                    "Person", "email", PropertyValue.FromString("a@x.com"));
                created.Should().BeFalse(
                    "a committed MergeNode must be found by a later merge of the same key");
                tx.Commit();
            }
        });
    }

    // Opens a fresh binary backend in a unique temp directory, runs the body,
    // and cleans up — keeps each FT-17 test self-contained.
    private static void RunInTempBackend(Action<IGraphStorageBackend> body)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_ft17_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var backend = new BinaryGraphStorageBackendFactory()
                .Open(System.IO.Path.Combine(dir, "graph.quiver"), new GraphDatabaseOptions());
            body(backend);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
