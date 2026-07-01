using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 非同期 API が同期 API と同じトランザクション境界・キャンセル・rollback 契約を
/// 維持することを検証する。グラフ操作自体は同期のまま実行し、await は開始・commit・
/// 結果取得・破棄の境界に限定する。
/// </summary>
public sealed class AsyncApiTests
{
    /// <summary>
    /// 非同期トランザクションで書いたデータを commit 後に読み取り、
    /// 具体化・件数・先頭要素・非同期ストリームの各終端から同じ結果を取得できることを検証する。
    /// </summary>
    [Fact]
    public async Task Async_transaction_boundaries_and_traversal_terminals_work()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_async_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));

            await using (var tx = await db.BeginTransactionAsync())
            {
                // ThreadStatic のトランザクションコンテキストを維持するため、
                // グラフ操作の途中には await を挟まず、commit 境界だけを非同期にする。
                tx.CreateNode("Person");
                tx.CreateNode("Person");
                await tx.CommitAsync();
                tx.State.Should().Be(TransactionState.Committed);
            }

            await using var read = await db.BeginReadOnlyTransactionAsync();
            var traversal = read.G(db.Schema).Nodes().HasLabel("Person");

            (await traversal.ToListAsync()).Should().HaveCount(2);
            (await traversal.CountAsync()).Should().Be(2);
            (await traversal.NextAsync()).Should().NotBeNull();
            (await traversal.TryNextAsync()).Should().NotBeNull();

            var streamed = new List<NodeId>();
            // AsAsyncEnumerable は全件を List に具体化せず、カーソルを逐次消費する。
            await foreach (var nodeId in traversal.AsAsyncEnumerable())
                streamed.Add(nodeId);
            streamed.Should().HaveCount(2);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 処理開始前にキャンセル済みの token を渡した場合、トランザクション開始と
    /// traversal の具体化が処理を始めず <see cref="OperationCanceledException"/> を返すことを検証する。
    /// </summary>
    [Fact]
    public async Task Async_entry_points_respect_pre_canceled_tokens()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_async_cancel_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> begin = async () => await db.BeginTransactionAsync(cancellationToken: cts.Token);
            await begin.Should().ThrowAsync<OperationCanceledException>();

            await using var read = await db.BeginReadOnlyTransactionAsync();
            Func<Task> materialize = async () =>
                await read.G(db.Schema).Nodes().ToListAsync(cts.Token);
            await materialize.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// commit せずに <c>await using</c> スコープを抜けたトランザクションが同期版と同様に
    /// rollback され、後続の読み取りから未確定ノードが見えないことを検証する。
    /// </summary>
    [Fact]
    public async Task Async_dispose_rolls_back_uncommitted_transaction()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quiver_async_dispose_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));
            await using (var tx = await db.BeginTransactionAsync())
                tx.CreateNode("Transient");

            await using var read = await db.BeginReadOnlyTransactionAsync();
            (await read.G(db.Schema).Nodes().HasLabel("Transient").CountAsync()).Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
