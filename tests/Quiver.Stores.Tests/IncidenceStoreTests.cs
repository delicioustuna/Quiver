using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// IncidenceStore と NodeIncidenceHeadStore の単体テスト。
/// 双方向 chain の物理構造 (作成順の保持、head insert、prev 更新、終端) と、
/// 不可視 hyperedge の読み飛ばし、列挙の allocation-free 契約を検証する。
/// </summary>
public sealed class IncidenceStoreTests : IDisposable
{
    private readonly string[] _paths = Enumerable.Range(0, 6)
        .Select(_ => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
        .ToArray();

    private readonly List<PagedFile> _files = [];
    private readonly VersionedHyperedgeStore _hyperedges;
    private readonly IncidenceStore _incidences;
    private readonly NodeIncidenceHeadStore _heads;

    public IncidenceStoreTests()
    {
        for (int index = 0; index < _paths.Length; index++)
            _files.Add(new PagedFile(_paths[index]));

        _hyperedges = new VersionedHyperedgeStore(
            _files[0],
            new ItemPointerMap(_files[1]),
            new EntityVersionStore(_files[2]));
        _incidences = new IncidenceStore(_files[3]);
        _heads = new NodeIncidenceHeadStore(_files[5]);
    }

    /// <summary>
    /// hyperedge からのメンバー列挙が作成順を保ち、Invalid で終端することを検証する。
    /// メンバーには同一 node の別 role 参加 (node 4 が role 1 と 2) と、
    /// 同一 role への複数 node 参加 (role 2 に node 4・5・6) の両方を含める。
    /// </summary>
    [Fact]
    public void Hyperedge_chain_preserves_member_order_and_terminates()
    {
        IncidenceMember[] members =
        [
            new(new NodeId(4), new RoleId(1)),
            new(new NodeId(4), new RoleId(2)),
            new(new NodeId(5), new RoleId(2)),
            new(new NodeId(6), new RoleId(2)),
        ];
        HyperedgeId id = _hyperedges.Create(
            new HyperedgeTypeId(1), members, _incidences, _heads);

        var actual = new List<IncidenceMember>();
        var iterator = _incidences.EnumerateByHyperedge(id, _hyperedges);
        while (iterator.MoveNext())
            actual.Add(new IncidenceMember(
                iterator.Current.NodeId, iterator.Current.RoleId));

        actual.Should().Equal(members);
        iterator.Current.NextInHyperedge.Should().Be(IncidenceId.Invalid);
    }

    /// <summary>
    /// 同じ node が 2 つ目の hyperedge に参加したとき、新しい incidence が
    /// node chain の先頭に入り、その NextInNode が旧 head を指すことを検証する。
    /// 逆リンクは持たないため旧 head は書き換わらず (NextInNode は終端 Invalid のまま)、
    /// head insert が旧 head 側の page image を汚さないことを確かめる。
    /// </summary>
    [Fact]
    public void Node_chain_head_insert_points_forward_to_old_head()
    {
        NodeId shared = new(8);
        HyperedgeId first = Create(shared, new NodeId(9), role: 1);
        IncidenceId oldHead = _heads.Get(shared);
        HyperedgeId second = Create(shared, new NodeId(10), role: 2);
        IncidenceId newHead = _heads.Get(shared);

        using var newRecord = _incidences.Read(newHead);
        newRecord.HyperedgeId.Should().Be(second);
        newRecord.NextInNode.Should().Be(oldHead);

        using var oldRecord = _incidences.Read(oldHead);
        oldRecord.HyperedgeId.Should().Be(first);
        oldRecord.NextInNode.Should().Be(IncidenceId.Invalid);
    }

    /// <summary>
    /// node chain の途中に削除済み hyperedge の incidence があっても、
    /// 列挙がそこで途切れず後続の生存 hyperedge を返し続けることを検証する。
    /// incidence 自身は削除で書き換えないため、この skip が可視性の要になる。
    /// </summary>
    [Fact]
    public void Node_enumeration_skips_invisible_middle_header_without_losing_tail()
    {
        NodeId shared = new(12);
        HyperedgeId oldest = Create(shared, new NodeId(13), role: 1);
        HyperedgeId middle = Create(shared, new NodeId(14), role: 2);
        HyperedgeId newest = Create(shared, new NodeId(15), role: 3);
        _hyperedges.Delete(middle);

        var actual = new List<HyperedgeId>();
        var iterator = _incidences.EnumerateByNode(shared, _heads, _hyperedges);
        while (iterator.MoveNext())
            actual.Add(iterator.Current.HyperedgeId);

        actual.Should().Equal(newest, oldest);
    }

    /// <summary>
    /// head sidecar が新規確保した page の未書き込み slot を Invalid として返すことを
    /// 検証する (同一 page 内の隣接 slot と、未確保 page 域の node の両方)。
    /// 0 埋めのままだと未参加 node が sequence 0 の incidence を指してしまう。
    /// </summary>
    [Fact]
    public void Node_head_pages_initialize_unused_slots_to_invalid()
    {
        _heads.Set(new NodeId(0), new IncidenceId(42));

        _heads.Get(new NodeId(0)).Should().Be(new IncidenceId(42));
        _heads.Get(new NodeId(1)).Should().Be(IncidenceId.Invalid);
        _heads.Get(new NodeId(10_000)).Should().Be(IncidenceId.Invalid);
    }

    /// <summary>
    /// warm-up 後のメンバー列挙 (arity 16) が managed allocation を一切発生させない
    /// ことを検証する。ref struct 列挙子でホットパスに GC 圧を作らない契約。
    /// </summary>
    [Fact]
    public void Enumeration_does_not_allocate_per_incidence()
    {
        IncidenceMember[] members = Enumerable.Range(0, 16)
            .Select(index => new IncidenceMember(
                new NodeId(index), new RoleId(index)))
            .ToArray();
        HyperedgeId id = _hyperedges.Create(
            new HyperedgeTypeId(1), members, _incidences, _heads);

        var warmup = _incidences.EnumerateByHyperedge(id, _hyperedges);
        while (warmup.MoveNext())
        {
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var iterator = _incidences.EnumerateByHyperedge(id, _hyperedges);
        int count = 0;
        while (iterator.MoveNext())
            count++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        count.Should().Be(16);
        allocated.Should().Be(0);
    }

    /// <summary>
    /// Free で戻した slot の sequence が次の Allocate で LIFO に再利用され、
    /// 高水位を伸ばさずに新しいレコードを書けることを検証する。
    /// </summary>
    [Fact]
    public void Freed_slot_sequence_is_reused_by_next_allocate()
    {
        IncidenceId first = _incidences.Allocate(
            new HyperedgeId(1), new NodeId(1), new RoleId(1),
            IncidenceId.Invalid, IncidenceId.Invalid);
        IncidenceId second = _incidences.Allocate(
            new HyperedgeId(1), new NodeId(2), new RoleId(1),
            IncidenceId.Invalid, IncidenceId.Invalid);
        long hwmBeforeFree = _incidences.HighWaterMark;

        _incidences.Free(first);
        _incidences.InUseCount.Should().Be(1);
        _incidences.FreeHead.Should().Be(first.Sequence);

        IncidenceId reused = _incidences.Allocate(
            new HyperedgeId(2), new NodeId(3), new RoleId(2),
            IncidenceId.Invalid, IncidenceId.Invalid);

        reused.Should().Be(first);
        _incidences.HighWaterMark.Should().Be(hwmBeforeFree);
        _incidences.FreeHead.Should().Be(IncidenceId.Invalid.Sequence);
        _incidences.InUseCount.Should().Be(2);

        using var record = _incidences.Read(reused);
        record.InUse.Should().BeTrue();
        record.HyperedgeId.Should().Be(new HyperedgeId(2));
        record.NodeId.Should().Be(new NodeId(3));
        record.RoleId.Should().Be(new RoleId(2));
    }

    /// <summary>
    /// 高水位と free chain 先頭をヘッダページから復元することを検証する。
    /// 再オープン後も生存 slot が読め、free に積んだ sequence が再利用されることを確かめる。
    /// </summary>
    [Fact]
    public void Reopen_restores_high_water_mark_and_free_chain()
    {
        IncidenceId keep = _incidences.Allocate(
            new HyperedgeId(1), new NodeId(1), new RoleId(1),
            IncidenceId.Invalid, IncidenceId.Invalid);
        IncidenceId freed = _incidences.Allocate(
            new HyperedgeId(1), new NodeId(2), new RoleId(1),
            IncidenceId.Invalid, IncidenceId.Invalid);
        _incidences.Free(freed);
        long hwm = _incidences.HighWaterMark;

        _files[3].Dispose();
        var reopenedFile = new PagedFile(_paths[3]);
        _files[3] = reopenedFile;
        var reopened = new IncidenceStore(reopenedFile);

        reopened.HighWaterMark.Should().Be(hwm);
        reopened.FreeHead.Should().Be(freed.Sequence);
        reopened.InUseCount.Should().Be(1);
        using (var record = reopened.Read(keep))
            record.InUse.Should().BeTrue();

        IncidenceId reused = reopened.Allocate(
            new HyperedgeId(2), new NodeId(3), new RoleId(2),
            IncidenceId.Invalid, IncidenceId.Invalid);
        reused.Should().Be(freed);
        reopened.HighWaterMark.Should().Be(hwm);
    }

    private HyperedgeId Create(NodeId first, NodeId second, int role)
    {
        IncidenceMember[] members =
        [
            new(first, new RoleId(role)),
            new(second, new RoleId(role)),
        ];
        return _hyperedges.Create(
            new HyperedgeTypeId(1), members, _incidences, _heads);
    }

    public void Dispose()
    {
        foreach (PagedFile file in _files)
            file.Dispose();
        foreach (string path in _paths)
            File.Delete(path);
    }
}
