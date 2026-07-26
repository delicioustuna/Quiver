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
    public void CreateInMemory_でVertexを作成して読み戻せる()
    {
        using var database = QuiverDatabase.CreateInMemory();

        VertexId vertexId;
        using (var tx = database.BeginWriteTransaction())
        {
            vertexId = tx.CreateVertex("Person");
            tx.Commit();
        }

        using var readTx = database.BeginReadTransaction();
        readTx.VertexExists(vertexId).Should().BeTrue();
    }

    [Fact]
    public void Memoryセンチネルはインメモリバックエンドを選択する()
    {
        using var database = QuiverDatabase.Open(":memory:");

        using var tx = database.BeginWriteTransaction();
        tx.CreateVertex("Temporary");
        tx.Commit();

        database.Path.Should().Be(":memory:");
    }

    [Fact]
    public void スナップショットはサポートしない()
    {
        using var database = QuiverDatabase.CreateInMemory();

        var action = () => database.CreateSnapshot("snapshot.quiver");

        action.Should().Throw<NotSupportedException>();
    }
}
