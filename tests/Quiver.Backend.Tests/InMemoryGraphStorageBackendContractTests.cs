using FluentAssertions;
using Quiver.Core;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// インメモリバックエンドに共通 backend 契約テストを適用する。
/// </summary>
public sealed class InMemoryGraphStorageBackendContractTests
    : GraphStorageBackendContractTests
{
    protected override IGraphStorageBackendFactory CreateFactory()
        => new InMemoryGraphStorageBackendFactory();

    protected override string DatabasePath => ":memory:";
    protected override bool SupportsPersistence => false;

    [Fact]
    public void CreateInMemory_でノードを作成して読み戻せる()
    {
        using var database = GraphDatabase.CreateInMemory();

        NodeId nodeId;
        using (var tx = database.BeginTransaction())
        {
            nodeId = tx.CreateNode("Person");
            tx.Commit();
        }

        using var readTx = database.BeginReadOnlyTransaction();
        readTx.NodeExists(nodeId).Should().BeTrue();
        readTx.Rollback();
    }

    [Fact]
    public void Memoryセンチネルはインメモリバックエンドを選択する()
    {
        using var database = GraphDatabase.Open(":memory:");

        using var tx = database.BeginTransaction();
        tx.CreateNode("Temporary");
        tx.Commit();

        database.Path.Should().Be(":memory:");
    }

    [Fact]
    public void スナップショットはサポートしない()
    {
        using var database = GraphDatabase.CreateInMemory();

        var action = () => database.CreateSnapshot("snapshot.quiver");

        action.Should().Throw<NotSupportedException>();
    }
}
