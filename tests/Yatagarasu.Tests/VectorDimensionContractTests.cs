using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class VectorDimensionContractTests
{
    public static IEnumerable<object[]> WriteCases()
    {
        foreach (var kind in new[] { PropertyOwnerKind.Vertex, PropertyOwnerKind.Edge, PropertyOwnerKind.Nexus })
        foreach (bool direct in new[] { false, true })
        foreach (int dimensions in new[] { 2, 4 }) yield return [kind, direct, dimensions];
    }

    [Theory]
    [MemberData(nameof(WriteCases))]
    public void Rejected_write_preserves_current_vector_and_transaction_can_commit(PropertyOwnerKind kind, bool direct, int dimensions)
    {
        WithDatabase(path =>
        {
            EntityRef owner;
            using (var db = YatagarasuDatabase.Open(path))
            {
                using var tx = db.BeginWriteTransaction();
                owner = Create(tx, kind);
                tx.EditSchema.CreateIndex(Definition("embedding", kind, "Owner", 3));
                tx.EditSchema.CreateIndex(Definition("same", kind, null, 3));
                tx.SetVectorProperty(owner, "value", new float[] { 1, 2, 3 });
                var core = Field<GraphTransaction>(tx, "_core");
                int staged = Field<System.Collections.IList>(core, "_vectorMutations").Count;
                var pending = PendingImages(core);
                Action invalid = () => Set(tx, owner, new float[dimensions], direct);
                invalid.Should().Throw<VectorException>();
                PendingImages(core).Should().BeEquivalentTo(pending);
                Field<System.Collections.IList>(core, "_vectorMutations").Count.Should().Be(staged);
                Field<PropertyVersionStore>(db.BackendInternal, "_propStore").ScanOrphanVectorPayloads().Should().BeEmpty();
                var vector = new float[3];
                tx.TryGetVectorProperty(owner, "value", vector).Should().BeTrue();
                vector.Should().Equal(1, 2, 3);
                tx.Commit();
            }
            using var reopened = YatagarasuDatabase.Open(path);
            using var reader = reopened.BeginReadTransaction();
            var restored = new float[3];
            reader.TryGetVectorProperty(owner, "value", restored).Should().BeTrue();
            restored.Should().Equal(1, 2, 3);
        });
    }

    [Theory]
    [InlineData(PropertyOwnerKind.Vertex, DistanceMetric.Cosine)]
    [InlineData(PropertyOwnerKind.Vertex, DistanceMetric.Dot)]
    [InlineData(PropertyOwnerKind.Vertex, DistanceMetric.Euclidean)]
    [InlineData(PropertyOwnerKind.Edge, DistanceMetric.Cosine)]
    [InlineData(PropertyOwnerKind.Edge, DistanceMetric.Dot)]
    [InlineData(PropertyOwnerKind.Edge, DistanceMetric.Euclidean)]
    [InlineData(PropertyOwnerKind.Nexus, DistanceMetric.Cosine)]
    [InlineData(PropertyOwnerKind.Nexus, DistanceMetric.Dot)]
    [InlineData(PropertyOwnerKind.Nexus, DistanceMetric.Euclidean)]
    public void Legacy_dimensions_are_skipped_consistently_by_single_and_batch(PropertyOwnerKind kind, DistanceMetric metric)
    {
        WithDatabase(path =>
        {
            EntityRef good;
            using (var db = YatagarasuDatabase.Open(path))
            using (var tx = db.BeginWriteTransaction())
            {
                tx.SetVectorProperty(Create(tx, kind), "value", new float[] { 1, 2 });
                tx.SetVectorProperty(Create(tx, kind), "value", new float[] { 1, 2, 3, 4 });
                good = Create(tx, kind);
                tx.SetVectorProperty(good, "value", new float[] { 1, 2, 3 });
                tx.EditSchema.CreateIndex(Definition("embedding", kind, "Owner", 3) with { Metric = metric });
                tx.Commit();
            }
            using var reopened = YatagarasuDatabase.Open(path);
            using var reader = reopened.BeginReadTransaction();
            using var single = reader.KnnSearch("embedding", new float[] { 1, 2, 3 }, 5);
            var core = Field<GraphTransaction>(reader, "_core");
            core.SkippedVectorDimensionMismatchCount.Should().Be(2);
            var batch = reader.KnnSearchBatch("embedding", new ReadOnlyMemory<float>[] { new float[] { 1, 2, 3 } }, 5);
            core.SkippedVectorDimensionMismatchCount.Should().Be(4);
            using var batched = batch[0];
            Results(single).Should().Equal(good);
            Results(batched).Should().Equal(good);
        });
    }

    [Theory]
    [InlineData(null, "Owner", true)]
    [InlineData("Owner", null, true)]
    [InlineData("Owner", "Owner", true)]
    [InlineData("Owner", "Other", false)]
    public void Different_dimensions_are_rejected_only_for_overlapping_scopes(string? first, string? second, bool overlaps)
    {
        WithDatabase(path =>
        {
            using var db = YatagarasuDatabase.Open(path);
            using var tx = db.BeginWriteTransaction();
            tx.EditSchema.CreateIndex(Definition("first", PropertyOwnerKind.Vertex, first, 3));
            Action create = () => tx.EditSchema.CreateIndex(Definition("second", PropertyOwnerKind.Vertex, second, 2));
            if (overlaps)
            {
                create.Should().Throw<ConstraintException>();
                tx.Schema.TryGetIndex("second", out _).Should().BeFalse();
            }
            else create.Should().NotThrow();
            tx.Commit();
        });
    }

    private static VectorIndexDefinition Definition(string name, PropertyOwnerKind kind, string? scope, int dimensions)
        => new(name, new(kind, "value", scope), dimensions);

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(target)!;

    private static Dictionary<object, byte[]> PendingImages(GraphTransaction core)
    {
        var pending = Field<System.Collections.IDictionary>(Field<object>(core.Inner, "_walWriteSet"), "_pending");
        var images = new Dictionary<object, byte[]>();
        foreach (System.Collections.DictionaryEntry entry in pending)
            images.Add(entry.Key, ((byte[])entry.Value!.GetType()
                .GetProperty("PageBytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(entry.Value)!).ToArray());
        return images;
    }

    [Theory]
    [InlineData(DistanceMetric.Cosine)]
    [InlineData(DistanceMetric.Dot)]
    [InlineData(DistanceMetric.Euclidean)]
    public void Repeated_updates_ties_and_non_finite_candidates_match_between_search_paths(DistanceMetric metric)
    {
        WithDatabase(path =>
        {
            using var db = YatagarasuDatabase.Open(path);
            var owners = new List<EntityRef>();
            using (var tx = db.BeginWriteTransaction())
            {
                tx.EditSchema.CreateIndex(Definition("embedding", PropertyOwnerKind.Vertex, "Owner", 3) with { Metric = metric });
                for (int i = 0; i < 3; i++) owners.Add(Create(tx, PropertyOwnerKind.Vertex));
                foreach (var owner in owners.AsEnumerable().Reverse())
                {
                    tx.SetVectorProperty(owner, "value", new float[] { 1, 2, 3 });
                    tx.SetVectorProperty(owner, "value", new float[] { 1, 2, 3 });
                }
                tx.SetVectorProperty(Create(tx, PropertyOwnerKind.Vertex), "value", new float[] { float.NaN, 2, 3 });
                tx.SetVectorProperty(Create(tx, PropertyOwnerKind.Vertex), "value", new float[] { float.PositiveInfinity, 2, 3 });
                tx.Commit();
            }
            using var reader = db.BeginReadTransaction();
            using var single = reader.KnnSearch("embedding", new float[] { 1, 2, 3 }, 2);
            var batch = reader.KnnSearchBatch("embedding", new ReadOnlyMemory<float>[] { new float[] { 1, 2, 3 } }, 2);
            using var batched = batch[0];
            Results(single).Should().Equal(owners.Take(2));
            Results(batched).Should().Equal(owners.Take(2));
        });
    }

    private static EntityRef Create(IWriteTransaction tx, PropertyOwnerKind kind)
    {
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        return kind switch
        {
            PropertyOwnerKind.Vertex => EntityRef.From(tx.CreateVertex("Owner")),
            PropertyOwnerKind.Edge => EntityRef.From(tx.CreateEdge(a, b, "Owner")),
            _ => EntityRef.From(tx.CreateNexus("Owner", [new("a", a), new("b", b)])),
        };
    }

    private static void Set(IWriteTransaction tx, EntityRef owner, float[] vector, bool direct)
    {
        if (!direct) { tx.SetVectorProperty(owner, "value", vector); return; }
        var value = PropertyValue.FromFloatArray(vector);
        switch (owner.Kind)
        {
            case EntityKind.Vertex: tx.SetProperty(new VertexId(owner.Value), "value", in value); break;
            case EntityKind.Edge: tx.SetProperty(new EdgeId(owner.Value), "value", in value); break;
            default: tx.SetProperty(new NexusId(owner.Value), "value", in value); break;
        }
    }

    private static EntityRef[] Results(VectorSearchCursor cursor)
    {
        var owners = new List<EntityRef>();
        while (cursor.MoveNext()) owners.Add(cursor.Current.Owner);
        return owners.ToArray();
    }

    private static void WithDatabase(Action<string> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_dimensions_" + Guid.NewGuid().ToString("N"));
        try { test(Path.Combine(directory, "graph.yata")); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
