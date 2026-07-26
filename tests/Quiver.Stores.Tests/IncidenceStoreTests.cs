using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// IncidenceStore と VertexIncidenceHeadStore の単体テスト。
/// 双方向 chain の物理構造 (作成順の保持、head insert、prev 更新、終端) と、
/// 不可視 nexus の読み飛ばし、列挙の allocation-free 契約を検証する。
/// </summary>
public sealed class IncidenceStoreTests : IDisposable
{
    private readonly string[] _paths = Enumerable.Range(0, 6)
        .Select(_ => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))
        .ToArray();

    private readonly List<PagedFile> _files = [];
    private readonly VersionedNexusStore _nexuses;
    private readonly IncidenceStore _incidences;
    private readonly VertexIncidenceHeadStore _heads;

    public IncidenceStoreTests()
    {
        for (int index = 0; index < _paths.Length; index++)
            _files.Add(new PagedFile(_paths[index]));

        _nexuses = new VersionedNexusStore(
            _files[0],
            new ItemPointerMap(_files[1]),
            new EntityVersionStore(_files[2]));
        _incidences = new IncidenceStore(_files[3]);
        _heads = new VertexIncidenceHeadStore(_files[5]);
    }

    /// <summary>
    /// nexus からのメンバー列挙が作成順を保ち、Invalid で終端することを検証する。
    /// メンバーには同一 vertex の別 role 参加 (vertex 4 が role 1 と 2) と、
    /// 同一 role への複数 vertex 参加 (role 2 に vertex 4・5・6) の両方を含める。
    /// </summary>
    [Fact]
    public void Nexus_chain_preserves_member_order_and_terminates()
    {
        IncidenceMember[] members =
        [
            new(new VertexId(4), new RoleId(1)),
            new(new VertexId(4), new RoleId(2)),
            new(new VertexId(5), new RoleId(2)),
            new(new VertexId(6), new RoleId(2)),
        ];
        NexusId id = _nexuses.Create(
            new NexusTypeId(1), members, _incidences, _heads);

        var actual = new List<IncidenceMember>();
        var iterator = _incidences.EnumerateByNexus(id, _nexuses);
        while (iterator.MoveNext())
            actual.Add(new IncidenceMember(
                iterator.Current.VertexId, iterator.Current.RoleId));

        actual.Should().Equal(members);
        iterator.Current.NextInNexus.Should().Be(IncidenceId.Invalid);
    }

    /// <summary>
    /// 同じ vertex が 2 つ目の nexus に参加したとき、新しい incidence が
    /// vertex chain の先頭に入り、その NextInVertex が旧 head を指すことを検証する。
    /// 逆リンクは持たないため旧 head は書き換わらず (NextInVertex は終端 Invalid のまま)、
    /// head insert が旧 head 側の page image を汚さないことを確かめる。
    /// </summary>
    [Fact]
    public void Vertex_chain_head_insert_points_forward_to_old_head()
    {
        VertexId shared = new(8);
        NexusId first = Create(shared, new VertexId(9), role: 1);
        IncidenceId oldHead = _heads.Get(shared);
        NexusId second = Create(shared, new VertexId(10), role: 2);
        IncidenceId newHead = _heads.Get(shared);

        using var newRecord = _incidences.Read(newHead);
        newRecord.NexusId.Sequence.Should().Be(second.Sequence);
        newRecord.NextInVertex.Should().Be(oldHead);

        using var oldRecord = _incidences.Read(oldHead);
        oldRecord.NexusId.Sequence.Should().Be(first.Sequence);
        oldRecord.NextInVertex.Should().Be(IncidenceId.Invalid);
    }

    /// <summary>
    /// vertex chain の途中に削除済み nexus の incidence があっても、
    /// 列挙がそこで途切れず後続の生存 nexus を返し続けることを検証する。
    /// incidence 自身は削除で書き換えないため、この skip が可視性の要になる。
    /// </summary>
    [Fact]
    public void Vertex_enumeration_skips_invisible_middle_header_without_losing_tail()
    {
        VertexId shared = new(12);
        NexusId oldest = Create(shared, new VertexId(13), role: 1);
        NexusId middle = Create(shared, new VertexId(14), role: 2);
        NexusId newest = Create(shared, new VertexId(15), role: 3);
        _nexuses.Delete(middle);

        var actual = new List<long>();
        var iterator = _incidences.EnumerateByVertex(shared, _heads, _nexuses);
        while (iterator.MoveNext())
            actual.Add(iterator.Current.NexusId.Sequence);

        actual.Should().Equal(newest.Sequence, oldest.Sequence);
    }

    /// <summary>
    /// head sidecar が新規確保した page の未書き込み slot を Invalid として返すことを
    /// 検証する (同一 page 内の隣接 slot と、未確保 page 域の vertex の両方)。
    /// 0 埋めのままだと未参加 vertex が sequence 0 の incidence を指してしまう。
    /// </summary>
    [Fact]
    public void Vertex_head_pages_initialize_unused_slots_to_invalid()
    {
        _heads.Set(new VertexId(0), new IncidenceId(42));

        _heads.Get(new VertexId(0)).Should().Be(new IncidenceId(42));
        _heads.Get(new VertexId(1)).Should().Be(IncidenceId.Invalid);
        _heads.Get(new VertexId(10_000)).Should().Be(IncidenceId.Invalid);
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
                new VertexId(index), new RoleId(index)))
            .ToArray();
        NexusId id = _nexuses.Create(
            new NexusTypeId(1), members, _incidences, _heads);

        var warmup = _incidences.EnumerateByNexus(id, _nexuses);
        while (warmup.MoveNext())
        {
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var iterator = _incidences.EnumerateByNexus(id, _nexuses);
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
            new NexusId(1), new VertexId(1), new RoleId(1),
            IncidenceId.Invalid, IncidenceId.Invalid);
        IncidenceId second = _incidences.Allocate(
            new NexusId(1), new VertexId(2), new RoleId(1),
            IncidenceId.Invalid, IncidenceId.Invalid);
        long hwmBeforeFree = _incidences.HighWaterMark;

        _incidences.Free(first);
        _incidences.InUseCount.Should().Be(1);
        _incidences.FreeHead.Should().Be(first.Sequence);

        IncidenceId reused = _incidences.Allocate(
            new NexusId(2), new VertexId(3), new RoleId(2),
            IncidenceId.Invalid, IncidenceId.Invalid);

        reused.Should().Be(first);
        _incidences.HighWaterMark.Should().Be(hwmBeforeFree);
        _incidences.FreeHead.Should().Be(IncidenceId.Invalid.Sequence);
        _incidences.InUseCount.Should().Be(2);

        using var record = _incidences.Read(reused);
        record.InUse.Should().BeTrue();
        record.NexusId.Should().Be(new NexusId(2));
        record.VertexId.Should().Be(new VertexId(3));
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
            new NexusId(1), new VertexId(1), new RoleId(1),
            IncidenceId.Invalid, IncidenceId.Invalid);
        IncidenceId freed = _incidences.Allocate(
            new NexusId(1), new VertexId(2), new RoleId(1),
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
            new NexusId(2), new VertexId(3), new RoleId(2),
            IncidenceId.Invalid, IncidenceId.Invalid);
        reused.Should().Be(freed);
        reopened.HighWaterMark.Should().Be(hwm);
    }

    private NexusId Create(VertexId first, VertexId second, int role)
    {
        IncidenceMember[] members =
        [
            new(first, new RoleId(role)),
            new(second, new RoleId(role)),
        ];
        return _nexuses.Create(
            new NexusTypeId(1), members, _incidences, _heads);
    }

    public void Dispose()
    {
        foreach (PagedFile file in _files)
            file.Dispose();
        foreach (string path in _paths)
            File.Delete(path);
    }
}
