using FluentAssertions;
using Quiver.Core;
using Quiver.Index;
using Xunit;

namespace Quiver.Index.Tests;

/// <summary>
/// FT-19 W1: <see cref="FileKindCatalog"/> の単体テスト。
/// load / allocate / free / persistence / corruption 検出をカバー。
/// </summary>
public class FileKindCatalogTests : IDisposable
{
    private readonly string _dir;

    public FileKindCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ft19_catalog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GetOrAllocate_assigns_starting_byte()
    {
        var catalog = new FileKindCatalog(_dir);
        byte b = catalog.GetOrAllocate("idx_a");
        b.Should().Be(FileKindCatalog.ReservedRangeStart);
    }

    [Fact]
    public void GetOrAllocate_is_idempotent_for_same_name()
    {
        var catalog = new FileKindCatalog(_dir);
        byte a1 = catalog.GetOrAllocate("idx_a");
        byte a2 = catalog.GetOrAllocate("idx_a");
        a1.Should().Be(a2);
    }

    [Fact]
    public void GetOrAllocate_assigns_distinct_bytes_to_distinct_names()
    {
        var catalog = new FileKindCatalog(_dir);
        byte a = catalog.GetOrAllocate("idx_a");
        byte b = catalog.GetOrAllocate("idx_b");
        byte c = catalog.GetOrAllocate("idx_c");
        new[] { a, b, c }.Distinct().Count().Should().Be(3);
        a.Should().Be(FileKindCatalog.ReservedRangeStart);
        b.Should().Be((byte)(FileKindCatalog.ReservedRangeStart + 1));
        c.Should().Be((byte)(FileKindCatalog.ReservedRangeStart + 2));
    }

    [Fact]
    public void Catalog_persists_across_reopen()
    {
        byte aBefore, bBefore;
        {
            var c = new FileKindCatalog(_dir);
            aBefore = c.GetOrAllocate("idx_a");
            bBefore = c.GetOrAllocate("idx_b");
        }
        {
            var c = new FileKindCatalog(_dir);
            c.TryGet("idx_a", out byte aAfter).Should().BeTrue();
            c.TryGet("idx_b", out byte bAfter).Should().BeTrue();
            aAfter.Should().Be(aBefore);
            bAfter.Should().Be(bBefore);
        }
    }

    [Fact]
    public void Remove_frees_the_byte_for_reuse()
    {
        var catalog = new FileKindCatalog(_dir);
        byte a = catalog.GetOrAllocate("idx_a");
        byte b = catalog.GetOrAllocate("idx_b");
        catalog.Remove("idx_a").Should().BeTrue();

        // 次の allocate は最小の未使用 byte (= a) に戻る
        byte cReused = catalog.GetOrAllocate("idx_c");
        cReused.Should().Be(a);

        catalog.TryGet("idx_a", out _).Should().BeFalse();
        catalog.TryGet("idx_b", out byte bStill).Should().BeTrue();
        bStill.Should().Be(b);
    }

    [Fact]
    public void Remove_unknown_name_returns_false()
    {
        var catalog = new FileKindCatalog(_dir);
        catalog.Remove("never_existed").Should().BeFalse();
    }

    [Fact]
    public void Entries_returns_snapshot_of_current_mappings()
    {
        var catalog = new FileKindCatalog(_dir);
        catalog.GetOrAllocate("idx_a");
        catalog.GetOrAllocate("idx_b");

        var entries = catalog.Entries.ToList();
        entries.Should().HaveCount(2);
        entries.Select(e => e.Name).Should().Contain(["idx_a", "idx_b"]);
    }

    [Fact]
    public void Corrupted_file_throws_on_load()
    {
        File.WriteAllText(Path.Combine(_dir, ".fileKinds"), "bad_no_tab_here\nidx_b\t65\n");
        Action open = () => _ = new FileKindCatalog(_dir);
        open.Should().Throw<CorruptionException>();
    }

    [Fact]
    public void OutOfRange_byte_in_file_throws_on_load()
    {
        // 0x10 = 16, 予約レンジ (0x40..0xFF) 外
        File.WriteAllText(Path.Combine(_dir, ".fileKinds"), "idx_a\t16\n");
        Action open = () => _ = new FileKindCatalog(_dir);
        open.Should().Throw<CorruptionException>();
    }

    [Fact]
    public void Duplicate_byte_in_file_throws_on_load()
    {
        File.WriteAllText(Path.Combine(_dir, ".fileKinds"), "idx_a\t64\nidx_b\t64\n");
        Action open = () => _ = new FileKindCatalog(_dir);
        open.Should().Throw<CorruptionException>();
    }

    [Fact]
    public void Allocate_until_range_exhausted_throws_constraint_exception()
    {
        var catalog = new FileKindCatalog(_dir);
        int max = FileKindCatalog.ReservedRangeEnd - FileKindCatalog.ReservedRangeStart + 1;
        for (int i = 0; i < max; i++) catalog.GetOrAllocate($"idx_{i}");

        Action overflow = () => catalog.GetOrAllocate("one_too_many");
        overflow.Should().Throw<ConstraintException>();
    }
}
