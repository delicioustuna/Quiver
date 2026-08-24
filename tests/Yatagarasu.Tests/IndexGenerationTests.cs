using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// インデックス値に世代を保持する仕組みを検証する。
/// Vacuum のフリーリストによるスロット再利用後も古いインデックスエントリが
/// 別の Vertex を返さないこと、孤立 entry 回収が generation 不一致を除去できること、
/// QUIVER-SW family version が一致しない store を拒否することを確認する。
/// </summary>
public sealed class IndexGenerationTests : IDisposable
{
    private readonly string _dir;

    public IndexGenerationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_arch3_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- EntityRef のパックと展開 ----

    [Theory]
    [InlineData((byte)EntityKind.Vertex, 0L, 0)]
    [InlineData((byte)EntityKind.Vertex, 1L, 1)]
    [InlineData((byte)EntityKind.Edge, 42L, 7)]
    [InlineData((byte)EntityKind.Nexus, 99L, 3)]
    [InlineData((byte)EntityKind.Vertex, EntityRef.SequenceMask, EntityRef.MaxGeneration)]
    public void EntityRef_roundtrips(byte rawKind, long seq, int gen)
    {
        var kind = (EntityKind)rawKind;
        long packed = EntityRef.Pack(kind, seq, gen);
        EntityRef.UnpackKind(packed).Should().Be(kind);
        EntityRef.UnpackSequence(packed).Should().Be(seq);
        EntityRef.UnpackGeneration(packed).Should().Be(gen);
    }

    [Fact]
    public void EntityRef_rejects_out_of_range()
    {
        Action seqOverflow = () => EntityRef.Pack(EntityKind.Vertex, EntityRef.SequenceMask + 1, 0);
        seqOverflow.Should().Throw<ArgumentOutOfRangeException>();

        Action genOverflow = () => EntityRef.Pack(EntityKind.Vertex, 0, EntityRef.MaxGeneration + 1);
        genOverflow.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EntityRef_factories_accept_only_canonical_identity_values()
    {
        EntityRef.From(VertexId.Invalid).Should().Be(default(EntityRef));
        EntityRef.From(EdgeId.Invalid).Should().Be(default(EntityRef));
        EntityRef.From(NexusId.Invalid).Should().Be(default(EntityRef));
        default(EntityRef).IsValid.Should().BeFalse();

        var vertex = EntityRef.From(VertexId.Create(42, 7));
        vertex.IsValid.Should().BeTrue();
        vertex.Kind.Should().Be(EntityKind.Vertex);
        vertex.Sequence.Should().Be(42);
        vertex.Generation.Should().Be(7);

        Action reserved = () => EntityRef.Create((EntityKind)3, 1, 0);
        Action unknown = () => EntityRef.Create((EntityKind)5, 1, 0);
        Action packedReserved = () => EntityRef.Pack((EntityKind)15, 1, 0);
        reserved.Should().Throw<ArgumentOutOfRangeException>();
        unknown.Should().Throw<ArgumentOutOfRangeException>();
        packedReserved.Should().Throw<ArgumentOutOfRangeException>();
        EntityRef.UnpackKind(3L << EntityRef.KindShift).Should().Be((EntityKind)3);
    }

    // ---- ABA: slot reuse must not resurrect a stale index entry ----

    [Fact]
    public void Stale_index_entry_is_skipped_after_slot_reuse()
    {
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));

        // vertexA を作って "alice" で索引登録。
        VertexId vertexA;
        using (var tx = db.BeginWriteTransaction())
        {
            vertexA = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "alice", vertexA);
            tx.Commit();
        }

        // vertexA を削除 → vacuum で slot を物理回収。
        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(vertexA);
            tx.Commit();
        }
        db.Vacuum().ReclaimedVertices.Should().Be(1);

        // 同じ slot を再利用して vertexB を作り "bob" で索引登録。
        VertexId vertexB;
        using (var tx = db.BeginWriteTransaction())
        {
            vertexB = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "bob", vertexB);
            tx.Commit();
        }
        // ABA の前提として、同じスロットが再利用されていることを Sequence で確認する。
        // Value は世代を含むため reincarnation では vertexA と vertexB で異なる)。
        vertexB.Sequence.Should().Be(vertexA.Sequence);
        vertexB.Generation.Should().NotBe(vertexA.Generation);

        using var rtx = db.BeginReadTransaction();

        // 旧キー "alice" は世代不一致で stale 検出 → 空 (vertexB を誤って返さない)。
        var stale = rtx.SeekIndex("idx_name", PropertyValue.FromString("alice"));
        stale.MoveNext().Should().BeFalse("再利用された slot の旧索引エントリは世代不一致で弾かれる");
        stale.Dispose();

        // 新キー "bob" は現世代と一致 → vertexB を返す。
        var fresh = rtx.SeekIndex("idx_name", PropertyValue.FromString("bob"));
        fresh.MoveNext().Should().BeTrue();
        fresh.Current.Should().Be(EntityRef.From(vertexB));
        fresh.MoveNext().Should().BeFalse();
        fresh.Dispose();

    }

    [Fact]
    public void RangeIndex_skips_stale_entry_after_slot_reuse()
    {
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_age", new PropertyTarget(PropertyOwnerKind.Vertex, "age", "Person"), IndexKind.Int64Equality)));

        VertexId vertexA;
        using (var tx = db.BeginWriteTransaction())
        {
            vertexA = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_age", 30L, vertexA);
            tx.Commit();
        }
        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(vertexA);
            tx.Commit();
        }
        db.Vacuum().ReclaimedVertices.Should().Be(1);

        VertexId vertexB;
        using (var tx = db.BeginWriteTransaction())
        {
            vertexB = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_age", 99L, vertexB);
            tx.Commit();
        }
        vertexB.Sequence.Should().Be(vertexA.Sequence); // slot 同一性は Sequence

        using var rtx = db.BeginReadTransaction();

        // 旧範囲 [30,30] は stale → 空。
        var stale = rtx.RangeIndex(
            "idx_age", PropertyValue.FromInt64(30), true, PropertyValue.FromInt64(30), true);
        stale.MoveNext().Should().BeFalse();
        stale.Dispose();

        // 新範囲 [99,99] は vertexB を返す。
        var fresh = rtx.RangeIndex(
            "idx_age", PropertyValue.FromInt64(99), true, PropertyValue.FromInt64(99), true);
        fresh.MoveNext().Should().BeTrue();
        fresh.Current.Should().Be(EntityRef.From(vertexB));
        fresh.Dispose();

    }

    // ---- 世代付き VertexId の外部往復検証 (TryResolve の不一致は not-found) ----

    [Fact]
    public void Stale_vertex_handle_resolves_to_not_found_after_slot_reuse()
    {
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"));

        // vertexA を作って外部に往復した想定のハンドルとして保持する。
        VertexId vertexA;
        using (var tx = db.BeginWriteTransaction())
        {
            vertexA = tx.CreateVertex("Person");
            tx.Commit();
        }

        // 削除 → vacuum で slot を物理回収 → 同一 slot を vertexB が再利用 (世代 +1)。
        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(vertexA);
            tx.Commit();
        }
        db.Vacuum().ReclaimedVertices.Should().Be(1);
        VertexId vertexB;
        using (var tx = db.BeginWriteTransaction())
        {
            vertexB = tx.CreateVertex("Person");
            tx.Commit();
        }

        // 同一 slot・別世代であること (= ABA の前提)。
        vertexB.Sequence.Should().Be(vertexA.Sequence);
        vertexB.Generation.Should().NotBe(vertexA.Generation);

        using var rtx = db.BeginReadTransaction();
        // 旧ハンドル vertexA は世代不一致で not-found (別Vertex vertexB を誤って指さない)。
        rtx.VertexExists(vertexA).Should().BeFalse("stale generation handle must not resolve to the reused slot");
        // 現ハンドル vertexB は現世代と一致 → 存在する。
        rtx.VertexExists(vertexB).Should().BeTrue();
        // 世代を持たない (= 内部/旧来) ハンドルは照合をスキップし、生存 slot を素直に解決する。
        rtx.VertexExists(new VertexId(vertexA.Sequence)).Should().BeTrue();
    }

    // ---- Orphan GC: generation-mismatch entry is collected & repaired ----

    [Fact]
    public void OrphanGc_collects_and_repairs_generation_mismatch_after_reuse()
    {
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_name", new PropertyTarget(PropertyOwnerKind.Vertex, "name", "Person"), IndexKind.StringEquality)));

        VertexId vertexA;
        using (var tx = db.BeginWriteTransaction())
        {
            vertexA = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "alice", vertexA);
            tx.Commit();
        }
        using (var tx = db.BeginWriteTransaction())
        {
            tx.DeleteVertex(vertexA);
            tx.Commit();
        }
        db.Vacuum().ReclaimedVertices.Should().Be(1);

        VertexId vertexB;
        using (var tx = db.BeginWriteTransaction())
        {
            vertexB = tx.CreateVertex("Person");
            tx.SetIndexedProperty("idx_name", "bob", vertexB);
            tx.Commit();
        }
        vertexB.Sequence.Should().Be(vertexA.Sequence); // slot 同一性は Sequence

        // slot は in-use (vertexB) だが "alice" は世代違いなので orphan。
        var report = db.Diagnostics.CheckIndexConsistency();
        report.EntryCount.Should().Be(2);
        report.OrphanCount.Should().Be(1);
        report.Orphans[0].IndexName.Should().Be("idx_name");
        report.Orphans[0].EntityId.Should().Be(vertexB.Sequence); // OrphanIndexEntry.EntityId は unpacked seq

        // 修復で stale エントリのみ消える。
        db.Diagnostics.RepairIndexes(IndexRepairMode.Apply).RemovedCount.Should().Be(1);

        var after = db.Diagnostics.CheckIndexConsistency();
        after.OrphanCount.Should().Be(0);
        after.EntryCount.Should().Be(1);

        using var rtx = db.BeginReadTransaction();
        var fresh = rtx.SeekIndex("idx_name", PropertyValue.FromString("bob"));
        fresh.MoveNext().Should().BeTrue();
        fresh.Current.Should().Be(EntityRef.From(vertexB));
        fresh.Dispose();
    }

    // ---- storage family gate ----

    [Fact]
    public void Storage_format_family_has_a_single_current_version()
    {
        StorageFormatVersion.Current.Should().Be(2);
    }

    // 旧 family version を持つ store は open 時に reject する。
    private const byte RejectedFamilyVersion = 5;

    [Fact]
    public void Opening_store_with_old_family_version_throws_StorageFormatMismatch()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "graph.yata");

        // 現行 family の実 DB を作る。
        using (YatagarasuDatabase.Open(path))
        {
        }

        // Vertex heap の family version byte を不一致値へ変更する。
        using (var container = new SingleFileContainer(path))
        {
            var vertices = container.OpenTenant(
                BinaryGraphStorageBackendFactory.TenantVertices, PageKind.Header);
            using (var ph = vertices.PinForWrite(new PageId(1)))
            {
                ph.Data[31] = RejectedFamilyVersion;
            }
            container.Flush();
        }

        // 実 DB の current vertex heap 実装による再 open は拒否される。
        using (var container = new SingleFileContainer(path))
        {
            var vertices = container.OpenTenant(
                BinaryGraphStorageBackendFactory.TenantVertices, PageKind.Header);
            var vertexMapFile = container.OpenTenant(
                BinaryGraphStorageBackendFactory.TenantVertexMap, PageKind.Header);
            var vertexMap = new ItemPointerMap(vertexMapFile);
            Action reopen = () => new VersionedRecordHeap(vertices, vertexMap);
            reopen.Should().Throw<StorageFormatMismatchException>()
                .Which.Should().Match<StorageFormatMismatchException>(
                    ex => ex.FileKind == "versionedheap"
                          && ex.Found == RejectedFamilyVersion
                          && ex.Expected == StorageFormatVersion.Current);
        }
    }
}
