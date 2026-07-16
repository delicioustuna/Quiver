using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// VersionedNexusStore の単体テスト。
/// nexus header + incidence + vertex head sidecar の 3 ストアを永続ファイル上に組み、
/// 作成・検証・snapshot 可視性・再オープン・SSN read 記録の契約を検証する。
/// MVCC の検証以外は ambient トランザクション無し (Bootstrap 相当) で実行する。
/// </summary>
public sealed class NexusStoreTests : IDisposable
{
    private readonly string[] _paths = Enumerable.Range(0, 6)
        .Select(_ => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
        .ToArray();

    private PagedFile _nexusHeap = null!;
    private PagedFile _nexusMap = null!;
    private PagedFile _nexusVersions = null!;
    private PagedFile _incidenceHeap = null!;
    private PagedFile _incidenceMap = null!;
    private PagedFile _vertexHeads = null!;
    private VersionedNexusStore _nexuses = null!;
    private IncidenceStore _incidences = null!;
    private VertexIncidenceHeadStore _heads = null!;

    public NexusStoreTests() => Open();

    /// <summary>
    /// 最小 arity (2)・中間 (4)・複数 page にまたがりうる大きめの arity (16) で、
    /// 作成した header が読み戻せ、Scan と生存カウンタに反映されることを検証する。
    /// arity 4 以上では同一 role への複数 vertex 参加も含む (role は 3 種を巡回する)。
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    public void Create_and_scan_round_trip_supported_arities(int arity)
    {
        var members = Enumerable.Range(0, arity)
            .Select(index => new IncidenceMember(
                new VertexId(index), new RoleId(index % 3)))
            .ToArray();

        NexusId id = _nexuses.Create(
            new NexusTypeId(7), members, _incidences, _heads);

        using var record = _nexuses.Read(id);
        record.InUse.Should().BeTrue();
        record.Type.Should().Be(new NexusTypeId(7));
        record.FirstIncidenceId.IsValid.Should().BeTrue();
        record.FirstPropertyId.Should().Be(PropertyId.Invalid);
        _nexuses.Scan().Should().Equal(id);
        _nexuses.InUseCount.Should().Be(1);
        _incidences.InUseCount.Should().Be(arity);
    }

    /// <summary>
    /// 同じ role と vertex の組の重複が拒否され、失敗した Create が
    /// header・incidence・vertex head のどこにも書き込みを残さないことを検証する。
    /// </summary>
    [Fact]
    public void Validation_happens_before_any_record_is_written()
    {
        var duplicate = new[]
        {
            new IncidenceMember(new VertexId(1), new RoleId(2)),
            new IncidenceMember(new VertexId(1), new RoleId(2)),
        };

        Action act = () => _nexuses.Create(
            new NexusTypeId(1), duplicate, _incidences, _heads);

        act.Should().Throw<ArgumentException>();
        _nexuses.InUseCount.Should().Be(0);
        _incidences.InUseCount.Should().Be(0);
        _heads.Get(new VertexId(1)).Should().Be(IncidenceId.Invalid);
    }

    /// <summary>
    /// header を正本とする snapshot 可視性を 4 つの視点で検証する。
    /// 作成した本人には即座に可視 (read-your-own-writes)、作成 commit 前の snapshot には
    /// 不可視、削除 commit をまたぐ古い snapshot には可視のまま、削除 commit 後の
    /// snapshot には不可視。
    /// </summary>
    [Fact]
    public void Snapshot_visibility_handles_uncommitted_create_and_committed_delete()
    {
        var committed = new CommittedTxRegistry();
        NexusId id;

        try
        {
            MvccContext.Begin(
                new TransactionId(10),
                new SnapshotState(new TransactionId(9), new HashSet<long>()),
                committed);
            id = CreateTwoMemberNexus();

            using var ownWrite = _nexuses.Read(id);
            ownWrite.InUse.Should().BeTrue();

            MvccContext.End();
            committed.MarkCommitted(new TransactionId(10));
            MvccContext.Begin(
                new TransactionId(11),
                new SnapshotState(new TransactionId(9), new HashSet<long>()),
                committed);
            using var oldSnapshot = _nexuses.Read(id);
            oldSnapshot.InUse.Should().BeFalse();

            MvccContext.End();
            MvccContext.Begin(
                new TransactionId(20),
                new SnapshotState(new TransactionId(10), new HashSet<long>()),
                committed);
            using var beforeDelete = _nexuses.Read(id);
            beforeDelete.InUse.Should().BeTrue();
            _nexuses.Delete(id);

            MvccContext.End();
            committed.MarkCommitted(new TransactionId(20));
            MvccContext.Begin(
                new TransactionId(21),
                new SnapshotState(new TransactionId(15), new HashSet<long>()),
                committed);
            using var concurrentSnapshot = _nexuses.Read(id);
            concurrentSnapshot.InUse.Should().BeTrue();

            MvccContext.End();
            MvccContext.Begin(
                new TransactionId(22),
                new SnapshotState(new TransactionId(20), new HashSet<long>()),
                committed);
            using var afterDelete = _nexuses.Read(id);
            afterDelete.InUse.Should().BeFalse();
        }
        finally
        {
            MvccContext.End();
        }
    }

    /// <summary>
    /// 全ファイルを閉じて開き直した後も、header の可視性とメンバー chain
    /// (vertex・role・順序) が永続化されていることを検証する。
    /// </summary>
    [Fact]
    public void State_persists_across_reopen()
    {
        NexusId id = CreateTwoMemberNexus();
        Reopen();

        using var record = _nexuses.Read(id);
        record.InUse.Should().BeTrue();

        var members = new List<(VertexId Vertex, RoleId Role)>();
        var iterator = _incidences.EnumerateByNexus(id, _nexuses);
        while (iterator.MoveNext())
            members.Add((iterator.Current.VertexId, iterator.Current.RoleId));

        members.Should().Equal(
            (new VertexId(1), new RoleId(10)),
            (new VertexId(2), new RoleId(20)));
    }

    /// <summary>
    /// Read・Scan・vertex からの incidence 列挙のいずれで可視な header を観測しても、
    /// SSN の read set に nexus の読み取りとして記録されることを検証する
    /// (Serializable 分離の read-write 依存検出の前提)。
    /// </summary>
    [Fact]
    public void Read_scan_and_incidence_enumeration_record_nexus_reads()
    {
        NexusId id = CreateTwoMemberNexus();
        var committed = new CommittedTxRegistry();
        var sink = new ReadSink();

        try
        {
            MvccContext.Begin(
                new TransactionId(2),
                SnapshotState.Empty,
                committed,
                sink);

            using var record = _nexuses.Read(id);
            _ = _nexuses.Scan().Single();
            var iterator = _incidences.EnumerateByVertex(
                new VertexId(1), _heads, _nexuses);
            iterator.MoveNext().Should().BeTrue();
        }
        finally
        {
            MvccContext.End();
        }

        sink.Reads.Should().OnlyContain(
            read => read.Kind == EntityKind.Nexus
                && read.LocalId == id.Sequence);
        sink.Reads.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    private NexusId CreateTwoMemberNexus()
    {
        IncidenceMember[] members =
        [
            new(new VertexId(1), new RoleId(10)),
            new(new VertexId(2), new RoleId(20)),
        ];
        return _nexuses.Create(
            new NexusTypeId(3), members, _incidences, _heads);
    }

    private void Open()
    {
        _nexusHeap = new PagedFile(_paths[0]);
        _nexusMap = new PagedFile(_paths[1]);
        _nexusVersions = new PagedFile(_paths[2]);
        _incidenceHeap = new PagedFile(_paths[3]);
        _incidenceMap = new PagedFile(_paths[4]);
        _vertexHeads = new PagedFile(_paths[5]);
        _nexuses = new VersionedNexusStore(
            _nexusHeap,
            new ItemPointerMap(_nexusMap),
            new EntityVersionStore(_nexusVersions));
        _incidences = new IncidenceStore(_incidenceHeap);
        _heads = new VertexIncidenceHeadStore(_vertexHeads);
    }

    private void Reopen()
    {
        DisposeFiles();
        Open();
    }

    public void Dispose()
    {
        DisposeFiles();
        foreach (string path in _paths)
            File.Delete(path);
    }

    private void DisposeFiles()
    {
        _nexusHeap?.Dispose();
        _nexusMap?.Dispose();
        _nexusVersions?.Dispose();
        _incidenceHeap?.Dispose();
        _incidenceMap?.Dispose();
        _vertexHeads?.Dispose();
    }

    private sealed class ReadSink : ISsnReadSink
    {
        public List<(EntityKind Kind, long LocalId)> Reads { get; } = [];

        public void OnVisibleRead(EntityKind kind, long localId)
            => Reads.Add((kind, localId));
    }
}
