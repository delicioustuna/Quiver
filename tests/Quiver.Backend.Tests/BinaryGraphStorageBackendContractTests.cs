using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// 既定の binary backend に共通 backend 契約テストを適用する。
/// </summary>
public sealed class BinaryGraphStorageBackendContractTests : GraphStorageBackendContractTests
{
    private protected override IGraphStorageBackendFactory CreateFactory()
        => new BinaryGraphStorageBackendFactory();

    // binary backend は単一ファイル <dir>/graph.quiver を開く。
    protected override string DatabasePath
        => System.IO.Path.Combine(DatabaseDirectory, "graph.quiver");

    /// <summary>
    /// rollback がページベースストアのメタデータ (Vertexストアの high-water mark) も
    /// 巻き戻し、abort された CreateVertex の slot を次のコミット済みトランザクションが
    /// 再利用することを検証する。ID 割り当ては backend ごとの仕様なので、
    /// この保証は共通契約ではなく binary 固有テストに置く。
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
            using var backend = factory.Open(System.IO.Path.Combine(dir, "graph.quiver"), new QuiverDatabaseOptions());

            VertexId discarded;
            using (var tx = backend.BeginWriteTransaction())
            {
                discarded = tx.CreateVertex("Discarded");
                tx.Rollback();
            }

            VertexId reused;
            using (var tx = backend.BeginWriteTransaction())
            {
                reused = tx.CreateVertex("Fresh");
                tx.Commit();
            }

            reused.Should().Be(discarded,
                "rollback must restore the vertex store high-water mark so the id is reused");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// rollback されたトランザクションが挿入したインデックスエントリが、
    /// その後の <c>SeekIndex</c> から見えないことを検証する。
    /// </summary>
    [Fact]
    public void Indexed_property_rolled_back_is_not_visible()
    {
        RunInTempBackend(backend =>
        {
            using (var tx = backend.BeginWriteTransaction())
            {
                tx.EditSchema.CreateIndex(new ScalarIndexDefinition(
                    "idx_score",
                    new PropertyTarget(PropertyOwnerKind.Vertex, "score", "Item"),
                    IndexKind.Int64Equality));
                var n = tx.CreateVertex("Item");
                tx.SetProperty(n, "score", PropertyValue.FromInt64(42L));
                tx.Rollback();
            }

            using var rtx = backend.BeginReadTransaction();
            var en = rtx.SeekIndex("idx_score", PropertyValue.FromInt64(42L));
            en.MoveNext().Should().BeFalse(
                "a rolled-back IndexInsert must leave no index entry");
            en.Dispose();
        });
    }

    /// <summary>
    /// rollback でVertexストアの high-water mark が戻り、解放 slot が再利用されても、
    /// rollback 済みインデックスエントリが別の有効なVertexを誤って指さないことを検証する。
    /// </summary>
    [Fact]
    public void Rollback_prevents_stale_index_entry_aliasing()
    {
        RunInTempBackend(backend =>
        {
            VertexId discarded;
            using (var tx = backend.BeginWriteTransaction())
            {
                tx.EditSchema.CreateIndex(new ScalarIndexDefinition(
                    "idx_name",
                    new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"),
                    IndexKind.StringEquality));
                discarded = tx.CreateVertex("Person");
                tx.SetProperty(discarded, "name", PropertyValue.FromString("alice"));
                tx.Rollback();
            }

            VertexId reused;
            using (var tx = backend.BeginWriteTransaction())
            {
                // rollback されたVertexが解放した slot を再利用する。
                reused = tx.CreateVertex("Person");
                tx.Commit();
            }
            reused.Should().Be(discarded, ": the freed slot is reused");

            using var rtx = backend.BeginReadTransaction();
            var en = rtx.SeekIndex("idx_name", PropertyValue.FromString("alice"));
            var hits = new List<EntityRef>();
            while (en.MoveNext()) hits.Add(en.Current);
            en.Dispose();
            hits.Should().BeEmpty(
                "the rolled-back index entry must not alias the vertex that reused its slot");
        });
    }

    /// <summary>
    /// インデックス登録済みの <c>MergeVertex</c> は、インデックス検索で create と find を
    /// 判定する。rollback された merge が stale entry を残さず、同じキーの次の merge が
    /// ghost を誤検出しないことを検証する。
    /// </summary>
    [Fact]
    public void MergeVertex_with_index_upsert_is_correct_across_rollback()
    {
        RunInTempBackend(backend =>
        {
            using (var schemaTx = backend.BeginWriteTransaction())
            {
                schemaTx.EditSchema.CreateIndex(new ScalarIndexDefinition(
                    "idx_person_email",
                    new PropertyTarget(PropertyOwnerKind.Vertex, "email", "Person"),
                    IndexKind.StringEquality));
                schemaTx.Commit();
            }

            // Vertexとインデックスエントリを作成する merge を rollback する。
            using (var tx = backend.BeginWriteTransaction())
            {
                var (_, created) = tx.MergeVertex(
                    "Person", "email", PropertyValue.FromString("a@x.com"));
                created.Should().BeTrue();
                tx.Rollback();
            }

            // stale entry が残っていないため、ここでは新規作成される。
            using (var tx = backend.BeginWriteTransaction())
            {
                var (_, created) = tx.MergeVertex(
                    "Person", "email", PropertyValue.FromString("a@x.com"));
                created.Should().BeTrue(
                    "a rolled-back MergeVertex must not leave a stale index entry");
                tx.Commit();
            }

            // コミット後は、同じキーの merge が既存Vertexを見つける。
            using (var tx = backend.BeginWriteTransaction())
            {
                var (_, created) = tx.MergeVertex(
                    "Person", "email", PropertyValue.FromString("a@x.com"));
                created.Should().BeFalse(
                    "a committed MergeVertex must be found by a later merge of the same key");
                tx.Commit();
            }
        });
    }

    // 一意な一時ディレクトリで新しい binary backend を開いて処理を実行し、
    // 後片付けすることで各テストを独立させる。
    private static void RunInTempBackend(Action<IGraphStorageBackend> body)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_ft17_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var backend = new BinaryGraphStorageBackendFactory()
                .Open(System.IO.Path.Combine(dir, "graph.quiver"), new QuiverDatabaseOptions());
            body(backend);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
