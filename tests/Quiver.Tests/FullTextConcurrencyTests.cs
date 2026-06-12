using System.Collections.Concurrent;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FTS-6 concurrency contract for the full-text lane, held to the engine's actual
/// concurrency model: <b>single writer + many concurrent readers</b> (design
/// 07_transaction_recovery.md — Phase-1 single-writer; the FT-25 deadlock bench
/// records "single-writer / N-reader" as the supported shape). Two or more write
/// transactions mutating the <em>same</em> postings B+Tree at once is outside that
/// contract, so multi-threaded ingest is funnelled through an application-level
/// write gate — the documented-safe way to ingest from many threads against a
/// single-writer embedded engine. Readers run lock-free and concurrently.
///
/// The bar is the TS-7 three-bucket discipline: partition every observed thread
/// fault into (1) <b>transient</b> contention (lock timeout / serialization abort:
/// tolerated), (2) <b>empty/partial reads</b> (a search racing ahead of a commit:
/// tolerated, asserted only for well-formed ids), and (3) <b>unexpected</b>
/// (anything else — a missing-index ConstraintException, corruption, a null: these
/// fail). After ingest drains, the committed set must be fully and exactly
/// searchable, proving postings maintenance stayed coherent while readers raced it.
/// </summary>
[Collection("concurrency-stress")]
public sealed class FullTextConcurrencyTests : IDisposable
{
    private const string Index = "idx_body";
    private readonly string _dir;
    private readonly GraphDatabase _db;
    private readonly object _writeGate = new();

    public FullTextConcurrencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts6_conc_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), new GraphDatabaseOptions
        {
            // TS-7: under full-parallel CI the internal commit lock can exceed the
            // default 5s and surface as a spurious "Lock timeout" TransactionException.
            // Widen it so starvation doesn't masquerade as a correctness fault; the
            // classifier still tolerates a genuine timeout as transient.
            LockTimeout = TimeSpan.FromSeconds(30),
        });
        _db.Schema.CreateFullTextIndex(Index, "Doc", "body");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private enum Bucket { Transient, Unexpected }

    private static Bucket Classify(Exception ex) => ex switch
    {
        SerializabilityException => Bucket.Transient,
        TransactionException te when te.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || te.Message.Contains("lock", StringComparison.OrdinalIgnoreCase) => Bucket.Transient,
        _ => Bucket.Unexpected,
    };

    [Fact]
    public void Concurrent_ingest_and_search_stay_consistent()
    {
        const int Writers = 4;
        const int DocsPerWriter = 50;
        const int Readers = 3;

        var committed = new ConcurrentDictionary<string, NodeId>();   // marker -> node
        var faults = new ConcurrentBag<Exception>();
        using var done = new CountdownEvent(Writers);

        Thread MakeWriter(int w) => new(() =>
        {
            try
            {
                for (int i = 0; i < DocsPerWriter; i++)
                {
                    string marker = $"w{w}d{i:D3}";
                    // Single-writer contract: serialize the write transaction. Many
                    // threads may *submit* ingest, but only one mutates the shared
                    // postings index at a time (design 07, Phase-1 single-writer).
                    lock (_writeGate)
                    {
                        using var tx = _db.BeginTransaction();
                        var n = tx.CreateNode("Doc");
                        tx.SetProperty(n, "body",
                            PropertyValue.FromString($"shared token {marker} payload body"));
                        tx.Commit();
                        committed[marker] = n;
                    }
                }
            }
            catch (Exception ex)
            {
                faults.Add(ex);
            }
            finally
            {
                done.Signal();
            }
        });

        Thread MakeReader() => new(() =>
        {
            try
            {
                while (!done.IsSet)
                {
                    using var rtx = _db.BeginReadOnlyTransaction();
                    // Searching the shared term may legitimately return any prefix of
                    // the committed set (empty included) while a writer is in flight —
                    // the only contract here is "no fault, well-formed ids".
                    var hits = rtx.G(_db.Schema).Search(Index, "shared", k: 100).ToList();
                    foreach (var id in hits) id.Value.Should().BeGreaterThan(0);
                    Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                faults.Add(ex);
            }
        });

        var threads = new List<Thread>();
        for (int w = 0; w < Writers; w++) threads.Add(MakeWriter(w));
        for (int r = 0; r < Readers; r++) threads.Add(MakeReader());
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // Bucket 3: no unexpected faults are allowed.
        var unexpected = faults.Where(e => Classify(e) == Bucket.Unexpected).ToList();
        unexpected.Should().BeEmpty(
            "concurrent full-text ingest/search must not raise correctness faults; got: "
            + string.Join(" | ", unexpected.Select(e => $"{e.GetType().Name}: {e.Message}")));

        // Writes are gated, so every document commits.
        committed.Should().HaveCount(Writers * DocsPerWriter);

        // Final consistency: each committed marker resolves to exactly its node, and
        // the shared term returns the whole committed set (postings stayed coherent
        // under concurrent search).
        using (var rtx = _db.BeginReadOnlyTransaction())
        {
            var g = rtx.G(_db.Schema);
            foreach (var (marker, node) in committed)
                g.Search(Index, marker, k: 5).ToList().Should().ContainSingle().Which.Should().Be(node);
        }
        using (var rtx = _db.BeginReadOnlyTransaction())
        {
            rtx.G(_db.Schema).Search(Index, "shared", k: Writers * DocsPerWriter + 1)
                .ToList().Should().HaveCount(Writers * DocsPerWriter);
        }
    }
}
