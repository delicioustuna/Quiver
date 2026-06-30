using System.Collections.Concurrent;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 全文インデックスの並行性契約を、単一ライターと複数の並行リーダーという
/// エンジンの実際のモデルに沿って検証する。
/// 同じ Postings B+Tree を複数の書き込みトランザクションが同時に変更する操作は契約外なので、
/// 複数スレッドからの取り込みはアプリケーション側の書き込みゲートで直列化する。
/// リーダーはロックなしで並行実行する。
///
/// スレッドで観測した結果は、一時的な競合、コミット前の空または部分的な読み取り、
/// 予期しない障害の 3 種類に分類する。
/// 前二者は許容し、それ以外の例外、破損、null は失敗とする。
/// 取り込み完了後はコミット済み集合を過不足なく検索できることを確認し、
/// リーダーとの競合中も Postings の保守が整合していたことを保証する。
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
            // CI の全面並列実行では内部コミットロックの待機が既定の 5 秒を超えることがある。
            // スターベーションを正しさの障害と誤認しないよう待機時間を広げる。
            // 実際のタイムアウトは分類器が一時的な競合として扱う。
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
                    // 一度に 1 つの Postings インデックスだけを書き換える。
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
