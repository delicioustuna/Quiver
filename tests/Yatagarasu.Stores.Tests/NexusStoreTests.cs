using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Storage.Records.Tests;

/// <summary>
/// VersionedNexusStore の単体テスト。
/// nexus header + incidence + vertex head sidecar の 3 ストアを永続ファイル上に組み、
/// 作成・検証・snapshot 可視性・再オープンの契約を検証する。
/// MVCC の検証以外は Bootstrap 相当の明示的な可視性で実行する。
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
        record.FirstPropertyRef.Should().Be(PropertyVersionRef.Invalid);
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
        var store = (ITransactionNexusStore)_nexuses;
        var creator = new TransactionId(10);
        SnapshotState createSnapshot = new(9, new HashSet<long>());
        NexusId id = CreateTwoMemberNexus(store, creator);

        using var ownWrite = store.Read(id, VisibleAt(createSnapshot, creator));
        ownWrite.InUse.Should().BeTrue();

        SnapshotState oldSnapshotState = new(9, new HashSet<long>(), creator);
        using var oldSnapshot = store.Read(
            id,
            VisibleAt(oldSnapshotState, new TransactionId(11)));
        oldSnapshot.InUse.Should().BeFalse();

        var deleter = new TransactionId(20);
        SnapshotState beforeDeleteState = new(10, new HashSet<long>());
        using var beforeDelete = store.Read(id, VisibleAt(beforeDeleteState, deleter));
        beforeDelete.InUse.Should().BeTrue();
        store.Delete(id, deleter);

        SnapshotState concurrentSnapshotState = new(15, new HashSet<long>(), deleter);
        using var concurrentSnapshot = store.Read(
            id,
            VisibleAt(concurrentSnapshotState, new TransactionId(21)));
        concurrentSnapshot.InUse.Should().BeTrue();

        SnapshotState afterDeleteState = new(20, new HashSet<long>());
        using var afterDelete = store.Read(
            id,
            VisibleAt(afterDeleteState, new TransactionId(22)));
        afterDelete.InUse.Should().BeFalse();
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

    [Fact]
    public void Stale_generation_cannot_delete_or_write_current_nexus()
    {
        NexusId current = CreateTwoMemberNexus();
        NexusId stale = NexusId.Create(current.Sequence, current.Generation + 1);

        _nexuses.Delete(stale);
        using (NexusReadHandle live = _nexuses.Read(current))
            live.InUse.Should().BeTrue();

        Action write = () =>
        {
            NexusWriteHandle handle = _nexuses.Write(stale);
            handle.Dispose();
        };
        write.Should().Throw<CorruptionException>();
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

    private NexusId CreateTwoMemberNexus(
        ITransactionNexusStore store,
        TransactionId transactionId)
    {
        IncidenceMember[] members =
        [
            new(new VertexId(1), new RoleId(10)),
            new(new VertexId(2), new RoleId(20)),
        ];
        return store.Create(
            new NexusTypeId(3), members, _incidences, _heads, transactionId);
    }

    private static VersionVisible VisibleAt(SnapshotState snapshot, TransactionId self)
        => (xmin, xmax) => Visibility.IsVisible(xmin, xmax, in snapshot, self);

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

}
