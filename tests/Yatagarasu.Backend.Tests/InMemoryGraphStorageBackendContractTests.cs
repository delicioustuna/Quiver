using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Backend.Tests;

/// <summary>
/// インメモリバックエンドに共通 backend 契約テストを適用する。
/// </summary>
public sealed class InMemoryGraphStorageBackendContractTests
    : GraphStorageBackendContractTests
{
    private protected override IGraphStorageBackendFactory CreateFactory()
        => new InMemoryGraphStorageBackendFactory();

    protected override string DatabasePath => ":memory:";
    protected override bool SupportsPersistence => false;

    [Fact]
    public void CreateInMemory_でVertexを作成して読み戻せる()
    {
        using var database = YatagarasuDatabase.CreateInMemory();

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
        using var database = YatagarasuDatabase.Open(":memory:");

        using var tx = database.BeginWriteTransaction();
        tx.CreateVertex("Temporary");
        tx.Commit();

        database.Path.Should().Be(":memory:");
    }

    [Fact]
    public void スナップショットはサポートしない()
    {
        using var database = YatagarasuDatabase.CreateInMemory();

        var action = () => database.CreateSnapshot("snapshot.yata");

        action.Should().Throw<NotSupportedException>();
    }
}
