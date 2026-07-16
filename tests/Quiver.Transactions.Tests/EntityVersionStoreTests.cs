using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Transactions.Tests;

/// <summary>
/// <see cref="EntityVersionStore"/> の最小契約テスト。
///
/// <para>sidecar を単体で Open し、Read/Write round-trip と境界ケースを検証する。
/// MVCC visibility 経由の integration test が主検証になる。</para>
/// </summary>
public sealed class EntityVersionStoreTests : IDisposable
{
    private readonly string _tempPath;

    public EntityVersionStoreTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".verstore");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
        {
            try { File.Delete(_tempPath); }
            catch { /* 並列実行で他プロセスが掴んでいる場合は無視 */ }
        }
    }

    private EntityVersionStore CreateStore()
    {
        var file = new PagedFile(_tempPath);
        return new EntityVersionStore(file);
    }

    [Fact]
    public void Read_returns_Unset_for_unwritten_slot()
    {
        using var store = CreateStore();

        store.Read(0).Should().Be(EntityVersionMeta.Unset);
        store.Read(100).Should().Be(EntityVersionMeta.Unset);
        store.Read(long.MaxValue).Should().Be(EntityVersionMeta.Unset);
    }

    [Fact]
    public void Write_then_Read_round_trips_all_fields()
    {
        using var store = CreateStore();

        var meta = new EntityVersionMeta(Xmin: 42, Xmax: 99, Pstamp: 1234, Sstamp: 5678);
        store.Write(7, meta);

        store.Read(7).Should().Be(meta);
    }

    [Fact]
    public void UpdateXmax_only_changes_Xmax_field()
    {
        using var store = CreateStore();

        var initial = new EntityVersionMeta(Xmin: 10, Xmax: 0, Pstamp: 0, Sstamp: long.MaxValue);
        store.Write(3, initial);

        store.UpdateXmax(3, 77);

        store.Read(3).Should().Be(new EntityVersionMeta(Xmin: 10, Xmax: 77, Pstamp: 0, Sstamp: long.MaxValue));
    }

    [Fact]
    public void UpdatePstamp_and_UpdateSstamp_only_change_their_field()
    {
        using var store = CreateStore();

        var initial = new EntityVersionMeta(Xmin: 1, Xmax: 2, Pstamp: 3, Sstamp: 4);
        store.Write(5, initial);

        store.UpdatePstamp(5, 33);
        store.Read(5).Should().Be(new EntityVersionMeta(1, 2, 33, 4));

        store.UpdateSstamp(5, 44);
        store.Read(5).Should().Be(new EntityVersionMeta(1, 2, 33, 44));
    }

    [Fact]
    public void Crosses_page_boundary_correctly()
    {
        using var store = CreateStore();

        // QUIVER-SW header 40B、entry 40B なので RecordsPerPage = 8152/40 = 203。
        int rpp = EntityVersionStore.RecordsPerPage;
        rpp.Should().Be(203);

        var lastOnPage1 = new EntityVersionMeta(Xmin: 100, Xmax: 0, Pstamp: 0, Sstamp: long.MaxValue);
        var firstOnPage2 = new EntityVersionMeta(Xmin: 200, Xmax: 0, Pstamp: 0, Sstamp: long.MaxValue);

        store.Write(rpp - 1, lastOnPage1);  // page 2, last slot
        store.Write(rpp, firstOnPage2);      // page 3, first slot

        store.Read(rpp - 1).Should().Be(lastOnPage1);
        store.Read(rpp).Should().Be(firstOnPage2);
    }

    [Fact]
    public void Reopen_preserves_written_entries()
    {
        // 1 回目の open: エントリを書き込む
        using (var store = CreateStore())
        {
            store.Write(0, new EntityVersionMeta(1, 0, 0, long.MaxValue));
            store.Write(500, new EntityVersionMeta(2, 3, 4, 5));
        }

        // 2 回目の open: 読み戻す
        using var reopened = CreateStore();
        reopened.Read(0).Should().Be(new EntityVersionMeta(1, 0, 0, long.MaxValue));
        reopened.Read(500).Should().Be(new EntityVersionMeta(2, 3, 4, 5));
        reopened.Read(999).Should().Be(EntityVersionMeta.Unset);
    }

    [Fact]
    public void Negative_localId_returns_Unset_on_Read()
    {
        using var store = CreateStore();
        store.Read(-1).Should().Be(EntityVersionMeta.Unset);
        store.Read(-100).Should().Be(EntityVersionMeta.Unset);
    }

    [Fact]
    public void Negative_localId_throws_on_Write()
    {
        using var store = CreateStore();
        var act = () => store.Write(-1, new EntityVersionMeta(1, 0, 0, long.MaxValue));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
