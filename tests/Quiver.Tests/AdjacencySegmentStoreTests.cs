using Quiver;
using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

[CollectionDefinition("binary-backend-maintenance", DisableParallelization = true)]
public sealed class BinaryBackendMaintenanceCollection;

/// <summary>
/// インラインペイロードレーンを持つ隣接ブロックが、一括読み込みを経ても重みを保持し、
/// 展開カーソルと <c>ExpandOperator</c> の <c>NeighborAndWeight</c> 射影から取得できることを検証する。
/// 往復変換に加え、1 ページ約 370 エントリを超える多段ページチェーンも対象とする。
/// </summary>
[Collection("binary-backend-maintenance")]
public sealed class AdjacencySegmentStoreTests : IDisposable
{
    private readonly string _dir;
    private QuiverDatabase? _db;

    public AdjacencySegmentStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_adj_segment_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _db?.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Int64_payload_round_trips_through_segment_cursor()
    {
        const int degree = 10;
        var weightKey = BuildWithInt64Weights(degree);

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var tx = _db.BeginWriteTransaction();
        var adj = tx.AsInternal().AdjacencySegments;
        adj.Should().NotBeNull();

        var view = adj as IAdjacencyPayloadView;
        view.Should().NotBeNull("the adjacency segment was built with a payload lane");
        view!.PayloadSpec.Kind.Should().Be(PayloadKind.Int64);
        view.PayloadSpec.PropertyKeyId.Should().Be(weightKey.Value);

        using var cursor = adj!.OpenCursor(new VertexId(0), Direction.Outgoing, null);
        long sum = 0;
        int count = 0;
        while (cursor.MoveNext())
        {
            // i 番目のEdgeの重みは 100 + i。
            cursor.WeightRaw.Should().Be(100 + cursor.Neighbor.Value);
            sum += cursor.WeightRaw;
            count++;
        }
        count.Should().Be(degree);
        // 100*deg + sum(1..deg)
        sum.Should().Be(100L * degree + degree * (degree + 1) / 2);
    }

    [Fact]
    public void Double_payload_round_trips_via_bitcast()
    {
        // Double 型のキーを使う。
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            var key = db.EditSchema(schema => schema.GetOrCreatePropertyKey("score"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            loader.WithPayloadLane(PayloadLaneSpec.ForDouble(key.Value, defaultValue: double.NaN));
            loader.AppendVertex(new VertexId(0), new LabelId(0));
            loader.AppendVertex(new VertexId(1), new LabelId(0));
            loader.AppendVertex(new VertexId(2), new LabelId(0));
            loader.AppendEdge(new EdgeId(0), new VertexId(0), new VertexId(1), new EdgeTypeId(0));
            loader.AppendEdge(new EdgeId(1), new VertexId(0), new VertexId(2), new EdgeTypeId(0));
            loader.AppendEdgePayload(new EdgeId(0), key, BitConverter.DoubleToInt64Bits(1.5));
            loader.AppendEdgePayload(new EdgeId(1), key, BitConverter.DoubleToInt64Bits(2.75));
            loader.Commit();
        }

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var tx = _db.BeginWriteTransaction();
        var seen = new Dictionary<long, double>();
        using var cursor = tx.AsInternal().AdjacencySegments!.OpenCursor(new VertexId(0), Direction.Outgoing, null);
        while (cursor.MoveNext())
            seen[cursor.Neighbor.Value] = BitConverter.Int64BitsToDouble(cursor.WeightRaw);

        seen.Should().HaveCount(2);
        seen[1].Should().Be(1.5);
        seen[2].Should().Be(2.75);
    }

    [Fact]
    public void Default_raw_applied_when_edge_has_no_payload()
    {
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            var key = db.EditSchema(schema => schema.GetOrCreatePropertyKey("weight"));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            loader.WithPayloadLane(PayloadLaneSpec.ForInt64(key.Value, defaultValue: -42));
            loader.AppendVertex(new VertexId(0), new LabelId(0));
            loader.AppendVertex(new VertexId(1), new LabelId(0));
            loader.AppendVertex(new VertexId(2), new LabelId(0));
            loader.AppendEdge(new EdgeId(0), new VertexId(0), new VertexId(1), new EdgeTypeId(0));
            loader.AppendEdge(new EdgeId(1), new VertexId(0), new VertexId(2), new EdgeTypeId(0));
            // 先頭だけに明示的なペイロードを設定し、2 件目は既定値を使う。
            loader.AppendEdgePayload(new EdgeId(0), key, 7);
            loader.Commit();
        }

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var tx = _db.BeginWriteTransaction();
        var seen = new Dictionary<long, long>();
        using var cursor = tx.AsInternal().AdjacencySegments!.OpenCursor(new VertexId(0), Direction.Outgoing, null);
        while (cursor.MoveNext())
            seen[cursor.Neighbor.Value] = cursor.WeightRaw;

        seen[1].Should().Be(7);
        seen[2].Should().Be(-42);
    }

    [Theory]
    [InlineData(50)]      // single page
    [InlineData(400)]     // just over one segment page (~370 per page)
    [InlineData(2_000)]   // many segment pages
    public void Weights_preserved_across_multi_page_chain(int degree)
    {
        BuildWithInt64Weights(degree);
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginWriteTransaction();
        var seen = new Dictionary<long, long>();
        using var cursor = tx.AsInternal().AdjacencySegments!.OpenCursor(new VertexId(0), Direction.Outgoing, null);
        while (cursor.MoveNext())
            seen[cursor.Neighbor.Value] = cursor.WeightRaw;

        seen.Should().HaveCount(degree);
        for (int i = 1; i <= degree; i++)
            seen[i].Should().Be(100 + i);
    }

    [Fact]
    public void ExpandOperator_NeighborAndWeight_emits_weight_column()
    {
        const int degree = 5;
        BuildWithInt64Weights(degree);
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginWriteTransaction();
        var op = new ExpandOperator(
            new SingleVertexSource(new VertexId(0)),
            sourceVertexColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborAndWeight);

        var result = tx.Execute(op);
        result.Schema.Columns.Should().HaveCount(3);
        result.Schema.Columns[2].Name.Should().Be("weight");
        result.Schema.Columns[2].Type.Should().Be(TupleSlotType.Int64);

        var rows = result.Rows().ToList();
        rows.Should().HaveCount(degree);
        foreach (var row in rows)
        {
            long n = row.GetVertexId(1).Sequence; // 重みは slot 番号基準で設定したので Sequence で照合
            long w = row.GetInt64(2);
            w.Should().Be(100 + n);
        }
    }

    [Fact]
    public void Reload_after_build_preserves_payload_spec()
    {
        BuildWithInt64Weights(3);

        // 閉じて再オープンし、メタデータの永続化を確認する。
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        _db.Dispose();
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginWriteTransaction();
        var view = tx.AsInternal().AdjacencySegments as IAdjacencyPayloadView;
        view.Should().NotBeNull();
        view!.PayloadSpec.Kind.Should().Be(PayloadKind.Int64);
        view.PayloadSpec.DefaultRaw.Should().Be(0);
    }

    [Fact]
    public void Compact_preserves_and_refreshes_segment_payloads_through_property_path()
    {
        var weightKey = BuildWithInt64Weights(3);
        EdgeId deltaEdge;
        VertexId deltaVertex;

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = _db.BeginWriteTransaction())
        {
            var updated = PropertyValue.FromInt64(700);
            tx.SetProperty(EdgeId.Create(0, 1), "weight", in updated);

            deltaVertex = tx.CreateVertex("V");
            deltaEdge = tx.CreateEdge(new VertexId(0), deltaVertex, "LINK");
            var deltaWeight = PropertyValue.FromInt64(900);
            tx.SetProperty(deltaEdge, "weight", in deltaWeight);

            tx.DeleteEdge(EdgeId.Create(2, 1));
            tx.Commit();
        }

        _db.CompactAdjacency();

        using (var tx = _db.BeginWriteTransaction())
        {
            var view = tx.AsInternal().AdjacencySegments as IAdjacencyPayloadView;
            view.Should().NotBeNull();
            view!.PayloadSpec.PropertyKeyId.Should().Be(weightKey.Value);
            tx.AsInternal().AdjacencySegments!.IsTombstoned(new EdgeId(2)).Should().BeFalse();

            var seen = ReadOutgoingWeights(tx, new VertexId(0));
            seen.Should().ContainKey(1);
            seen[1].Should().Be(700);
            seen.Should().ContainKey(2);
            seen[2].Should().Be(102, "unchanged bulk payloads must survive compact");
            seen.Should().ContainKey(deltaVertex.Sequence);
            seen[deltaVertex.Sequence].Should().Be(900);
            seen.Should().NotContainKey(3);
        }

        _db.Dispose();
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = _db.BeginWriteTransaction())
        {
            var seen = ReadOutgoingWeights(tx, new VertexId(0));
            seen[1].Should().Be(700);
            seen[2].Should().Be(102);
            seen[deltaVertex.Sequence].Should().Be(900);
            seen.Should().NotContainKey(3);
        }
    }

    [Fact]
    public void Compact_interruption_after_descriptor_invalidation_reopens_on_row_path()
    {
        BuildWithInt64Weights(3);

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using (var tx = _db.BeginWriteTransaction())
        {
            var updated = PropertyValue.FromInt64(700);
            tx.SetProperty(EdgeId.Create(0, 1), "weight", in updated);
            tx.Commit();
        }

        BinaryGraphStorageBackend.CompactAdjacencyPhaseInjector = phase =>
        {
            if (phase == CompactAdjacencyPhase.AfterDescriptorInvalidated)
                throw new InvalidOperationException("injected compact interruption");
        };

        try
        {
            Action compact = () => _db.CompactAdjacency();
            compact.Should().Throw<InvalidOperationException>()
                .WithMessage("injected compact interruption");
        }
        finally
        {
            BinaryGraphStorageBackend.CompactAdjacencyPhaseInjector = null;
        }

        _db.Dispose();
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        using var read = _db.BeginReadTransaction();
        read.AsInternal().AdjacencySegments.Should().BeNull(
            "an interrupted compact must not reopen a partial adjacency view");

        ExpandOut(read, new VertexId(0)).Should().BeEquivalentTo(new[] { 1L, 2L, 3L });
        read.GetProperty(EdgeId.Create(0, 1), "weight").Int64Value.Should().Be(700);
    }

    [Fact]
    public void Compact_interruption_after_rebuild_reopens_on_row_path_without_duplicate_delta()
    {
        BuildWithInt64Weights(3);

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        VertexId deltaVertex;
        EdgeId deltaEdge;
        using (var tx = _db.BeginWriteTransaction())
        {
            deltaVertex = tx.CreateVertex("V");
            deltaEdge = tx.CreateEdge(new VertexId(0), deltaVertex, "LINK");
            tx.SetProperty(deltaEdge, "weight", PropertyValue.FromInt64(900));
            tx.DeleteEdge(EdgeId.Create(2, 1));
            tx.Commit();
        }

        BinaryGraphStorageBackend.CompactAdjacencyPhaseInjector = phase =>
        {
            if (phase == CompactAdjacencyPhase.AfterRebuild)
                throw new InvalidOperationException("injected compact interruption");
        };

        try
        {
            Action compact = () => _db.CompactAdjacency();
            compact.Should().Throw<InvalidOperationException>()
                .WithMessage("injected compact interruption");
        }
        finally
        {
            BinaryGraphStorageBackend.CompactAdjacencyPhaseInjector = null;
        }

        _db.Dispose();
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        using var read = _db.BeginReadTransaction();
        read.AsInternal().AdjacencySegments.Should().BeNull(
            "descriptor must remain invalid until compact epoch metadata is ready");

        ExpandOut(read, new VertexId(0))
            .Should().BeEquivalentTo(new[] { 1L, 2L, deltaVertex.Sequence });
        read.GetProperty(deltaEdge, "weight").Int64Value.Should().Be(900);
    }

    [Fact]
    public void Compact_interruption_after_final_descriptor_flush_reopens_segment_view_without_duplicate_delta()
    {
        var weightKey = BuildWithInt64Weights(3);

        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        VertexId deltaVertex;
        EdgeId deltaEdge;
        using (var tx = _db.BeginWriteTransaction())
        {
            tx.SetProperty(EdgeId.Create(0, 1), "weight", PropertyValue.FromInt64(700));
            deltaVertex = tx.CreateVertex("V");
            deltaEdge = tx.CreateEdge(new VertexId(0), deltaVertex, "LINK");
            tx.SetProperty(deltaEdge, "weight", PropertyValue.FromInt64(900));
            tx.DeleteEdge(EdgeId.Create(2, 1));
            tx.Commit();
        }

        BinaryGraphStorageBackend.CompactAdjacencyPhaseInjector = phase =>
        {
            if (phase == CompactAdjacencyPhase.AfterFinalDescriptorFlushed)
                throw new InvalidOperationException("injected compact interruption");
        };

        try
        {
            Action compact = () => _db.CompactAdjacency();
            compact.Should().Throw<InvalidOperationException>()
                .WithMessage("injected compact interruption");
        }
        finally
        {
            BinaryGraphStorageBackend.CompactAdjacencyPhaseInjector = null;
        }

        _db.Dispose();
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        using var read = _db.BeginReadTransaction();
        var view = read.AsInternal().AdjacencySegments as IAdjacencyPayloadView;
        view.Should().NotBeNull("the final descriptor was durably flushed before interruption");
        view!.PayloadSpec.PropertyKeyId.Should().Be(weightKey.Value);

        ExpandOut(read, new VertexId(0))
            .Should().BeEquivalentTo(new[] { 1L, 2L, deltaVertex.Sequence });

        var seen = ReadOutgoingWeights(read, new VertexId(0));
        seen[1].Should().Be(700);
        seen[2].Should().Be(102);
        seen[deltaVertex.Sequence].Should().Be(900);
        seen.Should().NotContainKey(3);
    }

    private PropertyKeyId BuildWithInt64Weights(int degree)
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var key = db.EditSchema(schema => schema.GetOrCreatePropertyKey("weight"));
        using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
        loader.WithPayloadLane(PayloadLaneSpec.ForInt64(key.Value));
        loader.AppendVertex(new VertexId(0), new LabelId(0));
        for (int i = 1; i <= degree; i++)
        {
            loader.AppendVertex(new VertexId(i), new LabelId(1));
            loader.AppendEdge(new EdgeId(i - 1),
                new VertexId(0), new VertexId(i), new EdgeTypeId(0));
            loader.AppendEdgePayload(new EdgeId(i - 1), key, 100 + i);
        }
        loader.Commit();
        return key;
    }

    private static Dictionary<long, long> ReadOutgoingWeights(IReadTransaction tx, VertexId source)
    {
        var seen = new Dictionary<long, long>();
        using var cursor = tx.AsInternal().AdjacencySegments!.OpenCursor(source, Direction.Outgoing, null);
        while (cursor.MoveNext())
        {
            if (!tx.AsInternal().AdjacencySegments!.IsTombstoned(cursor.Edge))
                seen[cursor.Neighbor.Sequence] = cursor.WeightRaw;
        }
        return seen;
    }

    private static List<long> ExpandOut(IReadTransaction tx, VertexId source)
    {
        var op = new ExpandOperator(
            new SingleVertexSource(source),
            sourceVertexColumn: 0,
            Direction.Outgoing,
            typeFilter: null,
            ExpandOutputMode.NeighborOnly);

        return tx.Execute(op).Rows().Select(r => r.GetVertexId(0).Sequence).ToList();
    }

    /// <summary>
    /// 他の演算子スタックを起動せずに ExpandOperator を単体テストするための最小入力。
    /// VertexId を 1 行だけ出力する。
    /// </summary>
    private sealed class SingleVertexSource : IPhysicalOperator
    {
        private readonly TupleSlot[] _buf = new TupleSlot[1];
        private readonly VertexId _vertex;
        private bool _emitted;

        public SingleVertexSource(VertexId vertex) { _vertex = vertex; }
        public TupleSchema Schema { get; } = new([new ColumnDefinition("n", TupleSlotType.VertexId)]);
        public OperatorStatistics Statistics { get; private set; }
        public TupleRef Current => new(_buf);

        public void Open(Quiver.Transactions.ITransaction tx) { _emitted = false; }

        public bool MoveNext()
        {
            if (_emitted) return false;
            _buf[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _vertex.Value };
            _emitted = true;
            return true;
        }

        public void Dispose() { }
    }
}
