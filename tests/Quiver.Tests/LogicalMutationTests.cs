using FluentAssertions;
using Quiver.Core;
using Quiver.Logical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 論理変更ストリームが公開された変更操作を記録し、コミット時だけ通知されることを検証する。
/// また、新しいデータベースへ再生して元の状態を再現できることを確認する。
/// </summary>
public sealed class LogicalMutationTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
    }

    private string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "quiver_logical_" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(d);
        return d;
    }

    private static QuiverDatabase OpenWithSink(string dir, ILogicalMutationSink sink)
        => QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver"), new QuiverDatabaseOptions { LogicalMutationSink = sink });

    [Fact]
    public void Commit_publishes_buffered_mutations()
    {
        var sink = new InMemoryLogicalMutationSink();
        using var db = OpenWithSink(NewDir(), sink);

        using (var tx = db.BeginWriteTransaction())
        {
            var a = tx.CreateVertex("Person");
            var b = tx.CreateVertex("Person");
            tx.SetProperty(a, "name", PropertyValue.FromString("Alice"));
            tx.CreateEdge(a, b, "KNOWS");
            tx.Commit();
        }

        sink.Batches.Should().HaveCount(1);
        var kinds = sink.Mutations.Select(m => m.Kind).ToArray();
        kinds.Should().Equal(
            LogicalMutationKind.CreateVertex,
            LogicalMutationKind.CreateVertex,
            LogicalMutationKind.SetVertexProperty,
            LogicalMutationKind.CreateEdge);
    }

    [Fact]
    public void Rollback_does_not_publish_anything()
    {
        var sink = new InMemoryLogicalMutationSink();
        using var db = OpenWithSink(NewDir(), sink);

        using (var tx = db.BeginWriteTransaction())
        {
            tx.CreateVertex("Person");
            tx.Rollback();
        }

        sink.Batches.Should().BeEmpty();
    }

    [Fact]
    public void Read_only_transaction_does_not_register_sink()
    {
        var sink = new InMemoryLogicalMutationSink();
        using var db = OpenWithSink(NewDir(), sink);

        // Seed something first via a writing tx.
        using (var tx = db.BeginWriteTransaction())
        {
            tx.CreateVertex("Person");
            tx.Commit();
        }
        sink.Clear();

        using (db.BeginReadTransaction()) { }

        sink.Batches.Should().BeEmpty();
    }

    [Fact]
    public void Sink_is_silent_when_no_sink_configured()
    {
        // No sink → no logical buffering at all. We can't observe a missing
        // sink directly; instead exercise the full mutation surface and confirm
        // no exceptions and normal semantics.
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(NewDir(), "graph.quiver"));
        using var tx = db.BeginWriteTransaction();
        var a = tx.CreateVertex("X");
        tx.SetProperty(a, "k", PropertyValue.FromInt32(1));
        tx.RemoveProperty(a, "k");
        tx.DeleteVertex(a);
        tx.Commit();
    }

    [Fact]
    public void Replay_rebuilds_graph_state_from_logical_stream()
    {
        var sink = new InMemoryLogicalMutationSink();
        string srcDir = NewDir();

        using (var src = OpenWithSink(srcDir, sink))
        using (var tx = src.BeginWriteTransaction())
        {
            var alice = tx.CreateVertex("Person");
            var bob = tx.CreateVertex("Person");
            var carol = tx.CreateVertex("Person");
            tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
            tx.SetProperty(alice, "age", PropertyValue.FromInt32(30));
            tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));
            tx.SetProperty(carol, "name", PropertyValue.FromString("Carol"));
            var e1 = tx.CreateEdge(alice, bob, "KNOWS");
            var e2 = tx.CreateEdge(alice, carol, "KNOWS");
            tx.SetProperty(e1, "since", PropertyValue.FromInt32(2010));
            tx.RemoveProperty(alice, "age");
            // delete one edge to also exercise DeleteEdge in the stream
            tx.DeleteEdge(e2);
            tx.Commit();
        }

        // Open a brand new database and replay against it.
        string targetDir = NewDir();
        using var target = QuiverDatabase.Open(System.IO.Path.Combine(targetDir, "graph.quiver"));
        using (var tx = target.BeginWriteTransaction())
        {
            LogicalMutationReplay.Apply(tx, sink.Mutations);
            tx.Commit();
        }

        // Walk the rebuilt graph and check the surviving structure.
        using (var ro = target.BeginReadTransaction())
        {
            // Find Alice by scanning created vertices via the replay map is not
            // exposed — instead inspect each vertex and locate by name.
            VertexId? alice = null, bob = null, carol = null;
            for (long i = 0; i < 16 && (alice is null || bob is null || carol is null); i++)
            {
                var n = new VertexId(i);
                if (!ro.VertexExists(n)) continue;
                var name = ro.GetProperty(n, "name");
                if (name.Type != PropertyValueType.String) continue;
                var nameStr = System.Text.Encoding.UTF8.GetString(name.Utf8StringValue);
                if (nameStr == "Alice") alice = n;
                else if (nameStr == "Bob") bob = n;
                else if (nameStr == "Carol") carol = n;
            }

            alice.Should().NotBeNull();
            bob.Should().NotBeNull();
            carol.Should().NotBeNull();

            // RemoveProperty("age") replayed — Alice should not carry it.
            ro.HasProperty(alice!.Value, "age").Should().BeFalse();

            // Exactly one KNOWS edge from Alice should remain.
            int knowsCount = 0;
            var edges = ro.EnumerateEdges(alice.Value, Direction.Outgoing, "KNOWS");
            while (edges.MoveNext()) knowsCount++;
            knowsCount.Should().Be(1);
        }
    }

    [Fact]
    public void Nexus_mutations_are_captured_in_commit_order()
    {
        var sink = new InMemoryLogicalMutationSink();
        using var db = OpenWithSink(NewDir(), sink);

        db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));

        using (var tx = db.BeginWriteTransaction())
        {
            var a = tx.CreateVertex("Person");
            var b = tx.CreateVertex("Book");
            var he = tx.CreateNexus("Purchase", [new("Buyer", a), new("Item", b)]);
            tx.SetProperty(he, "price", PropertyValue.FromInt32(30));
            tx.AddPropertyValue(he, "tags", PropertyValue.FromString("gift"));
            tx.RemoveProperty(he, "price");
            tx.DeleteNexus(he);
            tx.Commit();
        }

        var kinds = sink.Mutations.Select(m => m.Kind).ToArray();
        kinds.Should().Equal(
            LogicalMutationKind.CreateVertex,
            LogicalMutationKind.CreateVertex,
            LogicalMutationKind.CreateNexus,
            LogicalMutationKind.SetNexusProperty,
            LogicalMutationKind.AddNexusPropertyValue,
            LogicalMutationKind.RemoveNexusProperty,
            LogicalMutationKind.DeleteNexus);
    }

    [Fact]
    public void Nexus_mutations_are_not_published_on_rollback()
    {
        var sink = new InMemoryLogicalMutationSink();
        using var db = OpenWithSink(NewDir(), sink);

        using (var tx = db.BeginWriteTransaction())
        {
            var a = tx.CreateVertex("A");
            var b = tx.CreateVertex("B");
            var he = tx.CreateNexus("T", [new("R1", a), new("R2", b)]);
            tx.SetProperty(he, "k", PropertyValue.FromInt32(1));
            tx.Rollback();
        }

        sink.Batches.Should().BeEmpty();
    }

    [Fact]
    public void Replay_rebuilds_nexus_with_members_and_properties()
    {
        var sink = new InMemoryLogicalMutationSink();
        string srcDir = NewDir();

        NexusId srcHe;
        VertexId srcA, srcB, srcC;
        using (var src = OpenWithSink(srcDir, sink))
        {
            src.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
            using var tx = src.BeginWriteTransaction();
            srcA = tx.CreateVertex("Person");
            srcB = tx.CreateVertex("Book");
            srcC = tx.CreateVertex("Store");
            srcHe = tx.CreateNexus("Purchase",
                [new("Buyer", srcA), new("Item", srcB), new("Seller", srcC)]);
            tx.SetProperty(srcHe, "price", PropertyValue.FromInt32(30));
            tx.AddPropertyValue(srcHe, "tags", PropertyValue.FromString("gift"));
            tx.AddPropertyValue(srcHe, "tags", PropertyValue.FromString("sale"));
            tx.Commit();
        }

        // 別 DB へ再生する。target 側は tags の cardinality を宣言しておく。
        string targetDir = NewDir();
        using var target = QuiverDatabase.Open(System.IO.Path.Combine(targetDir, "graph.quiver"));
        target.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
        var vertexMap = new Dictionary<long, VertexId>();
        var heMap = new Dictionary<long, NexusId>();
        using (var tx = target.BeginWriteTransaction())
        {
            LogicalMutationReplay.Apply(tx, sink.Mutations, vertexMap, nexusMap: heMap);
            tx.Commit();
        }

        heMap.Should().ContainKey(srcHe.Value);
        var targetHe = heMap[srcHe.Value];

        using (var ro = target.BeginReadTransaction())
        {
            // メンバーがターゲット側 ID へ再マッピングされて再構築されていること。
            var members = new List<NexusMember>();
            var me = ro.GetMembers(targetHe);
            while (me.MoveNext()) members.Add(me.Current);
            members.Should().HaveCount(3);
            members.Select(m => m.Role).Should().BeEquivalentTo(["Buyer", "Item", "Seller"]);
            members.Select(m => m.VertexId).Should().OnlyContain(n => vertexMap.ContainsValue(n));

            ro.GetProperty(targetHe, "price").Int32Value.Should().Be(30);

            var tags = new List<string>();
            var te = ro.GetPropertyValues(targetHe, "tags");
            while (te.MoveNext())
                tags.Add(System.Text.Encoding.UTF8.GetString(te.Current.Utf8StringValue));
            tags.Should().BeEquivalentTo(["gift", "sale"]);
        }
    }

    [Fact]
    public void Multiple_committed_transactions_produce_separate_batches()
    {
        var sink = new InMemoryLogicalMutationSink();
        using var db = OpenWithSink(NewDir(), sink);

        using (var tx = db.BeginWriteTransaction())
        {
            tx.CreateVertex("A");
            tx.Commit();
        }
        using (var tx = db.BeginWriteTransaction())
        {
            tx.CreateVertex("B");
            tx.CreateVertex("C");
            tx.Commit();
        }

        sink.Batches.Should().HaveCount(2);
        sink.Batches[0].Mutations.Should().HaveCount(1);
        sink.Batches[1].Mutations.Should().HaveCount(2);
    }

    [Fact]
    public void Replay_preserves_edge_removal_and_set_property_mutations()
    {
        var sink = new InMemoryLogicalMutationSink();
        using (var source = OpenWithSink(NewDir(), sink))
        {
            source.EditSchema(schema =>
                schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
            using var tx = source.BeginWriteTransaction();
            VertexId a = tx.CreateVertex("A");
            VertexId b = tx.CreateVertex("B");
            EdgeId edge = tx.CreateEdge(a, b, "LINK");
            tx.SetProperty(edge, "temporary", PropertyValue.FromInt32(1));
            tx.RemoveProperty(edge, "temporary");
            tx.AddPropertyValue(a, "tags", PropertyValue.FromString("keep"));
            tx.AddPropertyValue(a, "tags", PropertyValue.FromString("drop"));
            tx.RemovePropertyValue(a, "tags", PropertyValue.FromString("drop"));
            tx.AddPropertyValue(edge, "tags", PropertyValue.FromString("keep"));
            tx.AddPropertyValue(edge, "tags", PropertyValue.FromString("drop"));
            tx.RemovePropertyValue(edge, "tags", PropertyValue.FromString("drop"));
            tx.Commit();
        }

        using var target = QuiverDatabase.Open(
            System.IO.Path.Combine(NewDir(), "graph.quiver"));
        target.EditSchema(schema =>
            schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
        var vertexMap = new Dictionary<long, VertexId>();
        var edgeMap = new Dictionary<long, EdgeId>();
        using (var tx = target.BeginWriteTransaction())
        {
            LogicalMutationReplay.Apply(
                tx,
                sink.Mutations,
                vertexMap,
                edgeMap);
            tx.Commit();
        }

        EdgeId targetEdge = edgeMap[sink.Mutations
            .Single(m => m.Kind == LogicalMutationKind.CreateEdge)
            .EdgeId.Value];
        VertexId targetA = vertexMap[sink.Mutations
            .First(m => m.Kind == LogicalMutationKind.CreateVertex)
            .VertexId.Value];
        using var read = target.BeginReadTransaction();
        read.GetProperty(targetEdge, "temporary").Type.Should().Be(default);
        ReadStrings(read.GetPropertyValues(targetA, "tags")).Should().Equal("keep");
        ReadStrings(read.GetPropertyValues(targetEdge, "tags")).Should().Equal("keep");
    }

    [Fact]
    public void Replay_maps_reused_sequences_by_full_generation_identity()
    {
        VertexId firstSource = VertexId.Create(7, 1);
        VertexId secondSource = VertexId.Create(7, 2);
        LogicalMutation[] mutations =
        [
            LogicalMutation.CreateVertex(firstSource, "First"),
            LogicalMutation.CreateVertex(secondSource, "Second"),
            LogicalMutation.DeleteVertex(firstSource),
        ];

        using var target = QuiverDatabase.Open(
            System.IO.Path.Combine(NewDir(), "graph.quiver"));
        var vertexMap = new Dictionary<long, VertexId>();
        using (var tx = target.BeginWriteTransaction())
        {
            LogicalMutationReplay.Apply(tx, mutations, vertexMap);
            tx.Commit();
        }

        vertexMap.Keys.Should().Contain([firstSource.Value, secondSource.Value]);
        vertexMap[firstSource.Value].Should().NotBe(vertexMap[secondSource.Value]);
        using var read = target.BeginReadTransaction();
        read.VertexExists(vertexMap[firstSource.Value]).Should().BeFalse();
        read.VertexExists(vertexMap[secondSource.Value]).Should().BeTrue();
    }

    private static string[] ReadStrings(PropertyValuesEnumerator values)
    {
        var result = new List<string>();
        while (values.MoveNext())
            result.Add(System.Text.Encoding.UTF8.GetString(values.Current.Utf8StringValue));
        return result.ToArray();
    }
}
