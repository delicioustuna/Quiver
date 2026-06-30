using FluentAssertions;
using Quiver.Query.Physical;
using Xunit;

namespace Quiver.Query.Physical.Tests;

/// <summary>
/// <see cref="PageSelectionBitmap"/> の単体テスト。
/// ビット操作の正確性、境界条件、列挙子の網羅をカバーする。
/// </summary>
public sealed class PageSelectionBitmapTests
{
    // ── Constructor validation ───────────────────────────────────────

    [Fact]
    public void Constructor_rejects_negative_count()
    {
        Span<ulong> words = stackalloc ulong[1];
        try
        {
            _ = new PageSelectionBitmap(words, -1);
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    [Fact]
    public void Constructor_rejects_undersized_words()
    {
        Span<ulong> words = stackalloc ulong[1];
        try
        {
            _ = new PageSelectionBitmap(words, 65);
            Assert.Fail("Expected ArgumentException");
        }
        catch (ArgumentException) { }
    }

    [Fact]
    public void Constructor_accepts_zero_count()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 0);
        bm.Capacity.Should().Be(0);
        bm.PopCount().Should().Be(0);
    }

    // ── Capacity ────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(200)]
    public void Capacity_returns_count(int count)
    {
        int wordsNeeded = (count + 63) >> 6;
        Span<ulong> words = stackalloc ulong[wordsNeeded];
        var bm = new PageSelectionBitmap(words, count);
        bm.Capacity.Should().Be(count);
    }

    // ── SetAll / Clear ──────────────────────────────────────────────

    [Fact]
    public void SetAll_sets_all_bits_up_to_count()
    {
        Span<ulong> words = stackalloc ulong[2];
        var bm = new PageSelectionBitmap(words, 100);
        bm.SetAll();
        for (int i = 0; i < 100; i++)
            bm.IsSet(i).Should().BeTrue($"bit {i}");
        bm.PopCount().Should().Be(100);
    }

    [Fact]
    public void SetAll_does_not_set_bits_beyond_count()
    {
        Span<ulong> words = stackalloc ulong[2];
        var bm = new PageSelectionBitmap(words, 70);
        bm.SetAll();
        bm.IsSet(70).Should().BeFalse();
        bm.IsSet(71).Should().BeFalse();
        bm.PopCount().Should().Be(70);
    }

    [Fact]
    public void Clear_resets_all_bits()
    {
        Span<ulong> words = stackalloc ulong[2];
        var bm = new PageSelectionBitmap(words, 100);
        bm.SetAll();
        bm.Clear();
        bm.PopCount().Should().Be(0);
        for (int i = 0; i < 100; i++)
            bm.IsSet(i).Should().BeFalse($"bit {i}");
    }

    [Fact]
    public void SetAll_after_Clear_restores_all_bits()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 64);
        bm.SetAll();
        bm.Clear();
        bm.SetAll();
        bm.PopCount().Should().Be(64);
    }

    // ── AndEquals ───────────────────────────────────────────────────

    [Fact]
    public void AndEquals_false_clears_specified_bit()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 64);
        bm.SetAll();
        bm.AndEquals(0, false);
        bm.AndEquals(32, false);
        bm.AndEquals(63, false);
        bm.IsSet(0).Should().BeFalse();
        bm.IsSet(32).Should().BeFalse();
        bm.IsSet(63).Should().BeFalse();
        bm.IsSet(1).Should().BeTrue();
        bm.PopCount().Should().Be(61);
    }

    [Fact]
    public void AndEquals_true_does_not_change_state()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 10);
        bm.SetAll();
        bm.AndEquals(5, true);
        bm.PopCount().Should().Be(10);
    }

    [Fact]
    public void AndEquals_false_on_already_cleared_bit_is_idempotent()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 8);
        bm.Clear();
        bm.AndEquals(3, false);
        bm.PopCount().Should().Be(0);
    }

    [Fact]
    public void AndEquals_out_of_range_throws()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 10);
        bm.SetAll();
        try
        {
            bm.AndEquals(10, false);
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    [Fact]
    public void AndEquals_negative_index_throws()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 10);
        try
        {
            bm.AndEquals(-1, false);
            Assert.Fail("Expected ArgumentOutOfRangeException");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    // ── IsSet ───────────────────────────────────────────────────────

    [Fact]
    public void IsSet_beyond_capacity_returns_false()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 10);
        bm.SetAll();
        bm.IsSet(10).Should().BeFalse();
        bm.IsSet(63).Should().BeFalse();
        bm.IsSet(999).Should().BeFalse();
    }

    // ── PopCount ────────────────────────────────────────────────────

    [Fact]
    public void PopCount_on_empty_is_zero()
    {
        Span<ulong> words = stackalloc ulong[2];
        var bm = new PageSelectionBitmap(words, 128);
        bm.Clear();
        bm.PopCount().Should().Be(0);
    }

    [Fact]
    public void PopCount_tracks_individual_clears()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 16);
        bm.SetAll();
        bm.PopCount().Should().Be(16);
        bm.AndEquals(0, false);
        bm.PopCount().Should().Be(15);
        bm.AndEquals(15, false);
        bm.PopCount().Should().Be(14);
    }

    // ── Word boundary ───────────────────────────────────────────────

    [Fact]
    public void Exact_word_boundary_64_bits()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 64);
        bm.SetAll();
        bm.PopCount().Should().Be(64);
        bm.IsSet(63).Should().BeTrue();
    }

    [Fact]
    public void One_past_word_boundary_65_bits()
    {
        Span<ulong> words = stackalloc ulong[2];
        var bm = new PageSelectionBitmap(words, 65);
        bm.SetAll();
        bm.PopCount().Should().Be(65);
        bm.IsSet(64).Should().BeTrue();
        bm.IsSet(65).Should().BeFalse();
    }

    [Fact]
    public void Single_bit()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 1);
        bm.SetAll();
        bm.PopCount().Should().Be(1);
        bm.IsSet(0).Should().BeTrue();
        bm.AndEquals(0, false);
        bm.PopCount().Should().Be(0);
    }

    // ── Enumerator ──────────────────────────────────────────────────

    [Fact]
    public void Enumerator_empty_yields_nothing()
    {
        Span<ulong> words = stackalloc ulong[1];
        var bm = new PageSelectionBitmap(words, 8);
        bm.Clear();
        var seen = new List<int>();
        var en = bm.GetEnumerator();
        while (en.MoveNext()) seen.Add(en.Current);
        seen.Should().BeEmpty();
    }

    [Fact]
    public void Enumerator_full_yields_all_ascending()
    {
        Span<ulong> words = stackalloc ulong[2];
        var bm = new PageSelectionBitmap(words, 70);
        bm.SetAll();
        var seen = new List<int>();
        var en = bm.GetEnumerator();
        while (en.MoveNext()) seen.Add(en.Current);
        seen.Should().Equal(Enumerable.Range(0, 70));
    }

    [Fact]
    public void Enumerator_skips_cleared_bits()
    {
        Span<ulong> words = stackalloc ulong[2];
        var bm = new PageSelectionBitmap(words, 80);
        bm.SetAll();
        bm.AndEquals(0, false);
        bm.AndEquals(63, false);
        bm.AndEquals(64, false);
        bm.AndEquals(79, false);

        var seen = new List<int>();
        var en = bm.GetEnumerator();
        while (en.MoveNext()) seen.Add(en.Current);
        seen.Should().Equal(
            Enumerable.Range(0, 80).Where(i => i != 0 && i != 63 && i != 64 && i != 79));
    }

    [Fact]
    public void Enumerator_sparse_bits()
    {
        Span<ulong> words = stackalloc ulong[3];
        var bm = new PageSelectionBitmap(words, 150);
        bm.Clear();
        // SetAll 後に大半をクリアし、期待する疎な集合が得られることを確認する。
        bm.SetAll();
        for (int i = 0; i < 150; i++)
            if (i != 7 && i != 64 && i != 127 && i != 149)
                bm.AndEquals(i, false);

        var seen = new List<int>();
        var en = bm.GetEnumerator();
        while (en.MoveNext()) seen.Add(en.Current);
        seen.Should().Equal([7, 64, 127, 149]);
    }

    // ── Cross-word operations ───────────────────────────────────────

    [Fact]
    public void Operations_across_three_words()
    {
        Span<ulong> words = stackalloc ulong[3];
        var bm = new PageSelectionBitmap(words, 192);
        bm.SetAll();
        bm.PopCount().Should().Be(192);

        bm.AndEquals(63, false);
        bm.AndEquals(64, false);
        bm.AndEquals(127, false);
        bm.AndEquals(128, false);
        bm.AndEquals(191, false);

        bm.PopCount().Should().Be(187);
        bm.IsSet(62).Should().BeTrue();
        bm.IsSet(63).Should().BeFalse();
        bm.IsSet(64).Should().BeFalse();
        bm.IsSet(65).Should().BeTrue();
        bm.IsSet(127).Should().BeFalse();
        bm.IsSet(128).Should().BeFalse();
        bm.IsSet(129).Should().BeTrue();
        bm.IsSet(191).Should().BeFalse();
    }
}
