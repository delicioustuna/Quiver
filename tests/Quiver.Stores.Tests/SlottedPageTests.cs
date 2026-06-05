using System.Text;
using FluentAssertions;
using Quiver.Storage;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>ARCH-5c Phase 1: slotted ページプリミティブの単体テスト。</summary>
public class SlottedPageTests
{
    private const int BodySize = 8160; // PageBodySize (8192 - 32B header)

    private static byte[] NewBody()
    {
        var body = new byte[BodySize];
        new SlottedPage(body).Init();
        return body;
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Insert_then_get_roundtrips()
    {
        var body = NewBody();
        var sp = new SlottedPage(body);
        sp.TryInsert(Bytes("hello"), out int slot).Should().BeTrue();
        slot.Should().Be(0);

        new SlottedPage(body).TryGet(slot, out var got).Should().BeTrue();
        got.ToArray().Should().Equal(Bytes("hello"));
    }

    [Fact]
    public void Multiple_inserts_get_distinct_slots()
    {
        var body = NewBody();
        var sp = new SlottedPage(body);
        sp.TryInsert(Bytes("a"), out int s0).Should().BeTrue();
        sp.TryInsert(Bytes("bb"), out int s1).Should().BeTrue();
        sp.TryInsert(Bytes("ccc"), out int s2).Should().BeTrue();
        s0.Should().Be(0); s1.Should().Be(1); s2.Should().Be(2);

        var read = new SlottedPage(body);
        read.TryGet(s0, out var g0).Should().BeTrue(); g0.ToArray().Should().Equal(Bytes("a"));
        read.TryGet(s1, out var g1).Should().BeTrue(); g1.ToArray().Should().Equal(Bytes("bb"));
        read.TryGet(s2, out var g2).Should().BeTrue(); g2.ToArray().Should().Equal(Bytes("ccc"));
    }

    [Fact]
    public void Delete_tombstones_slot()
    {
        var body = NewBody();
        var sp = new SlottedPage(body);
        sp.TryInsert(Bytes("x"), out int slot);
        sp.Delete(slot).Should().BeTrue();
        new SlottedPage(body).TryGet(slot, out _).Should().BeFalse();
        // 二重削除は false
        new SlottedPage(body).Delete(slot).Should().BeFalse();
    }

    [Fact]
    public void Insert_reuses_tombstone_slot()
    {
        var body = NewBody();
        var sp = new SlottedPage(body);
        sp.TryInsert(Bytes("a"), out int s0);
        sp.TryInsert(Bytes("b"), out int s1);
        new SlottedPage(body).Delete(s0).Should().BeTrue();

        new SlottedPage(body).TryInsert(Bytes("c"), out int s2).Should().BeTrue();
        s2.Should().Be(s0); // 解放済 slot index を再利用
        new SlottedPage(body).TryGet(s2, out var got).Should().BeTrue();
        got.ToArray().Should().Equal(Bytes("c"));
        new SlottedPage(body).TryGet(s1, out var g1).Should().BeTrue();
        g1.ToArray().Should().Equal(Bytes("b"));
    }

    [Fact]
    public void Update_in_place_for_equal_or_smaller()
    {
        var body = NewBody();
        new SlottedPage(body).TryInsert(Bytes("hello"), out int slot);
        new SlottedPage(body).TryUpdate(slot, Bytes("hi")).Should().BeTrue(); // 縮小
        new SlottedPage(body).TryGet(slot, out var got).Should().BeTrue();
        got.ToArray().Should().Equal(Bytes("hi"));
    }

    [Fact]
    public void Update_relocates_on_growth_preserving_other_records()
    {
        var body = NewBody();
        var sp = new SlottedPage(body);
        sp.TryInsert(Bytes("aa"), out int s0);
        sp.TryInsert(Bytes("bb"), out int s1);

        new SlottedPage(body).TryUpdate(s0, Bytes("aaaaaaaa")).Should().BeTrue(); // 拡大 → 再配置
        var read = new SlottedPage(body);
        read.TryGet(s0, out var g0).Should().BeTrue(); g0.ToArray().Should().Equal(Bytes("aaaaaaaa"));
        read.TryGet(s1, out var g1).Should().BeTrue(); g1.ToArray().Should().Equal(Bytes("bb"));
    }

    [Fact]
    public void Compact_reclaims_space_and_keeps_slot_indices()
    {
        var body = NewBody();
        var sp = new SlottedPage(body);
        sp.TryInsert(Bytes("keep0"), out int s0);
        sp.TryInsert(Bytes("dead-large-record"), out int s1);
        sp.TryInsert(Bytes("keep2"), out int s2);

        int freeBefore = new SlottedPage(body).ContiguousFree;
        new SlottedPage(body).Delete(s1);
        new SlottedPage(body).Compact();
        int freeAfter = new SlottedPage(body).ContiguousFree;

        freeAfter.Should().BeGreaterThan(freeBefore); // tombstone のバイトを回収
        var read = new SlottedPage(body);
        read.TryGet(s0, out var g0).Should().BeTrue(); g0.ToArray().Should().Equal(Bytes("keep0"));
        read.TryGet(s2, out var g2).Should().BeTrue(); g2.ToArray().Should().Equal(Bytes("keep2"));
        read.TryGet(s1, out _).Should().BeFalse(); // tombstone のまま
    }

    [Fact]
    public void Insert_fails_when_full_but_existing_records_intact()
    {
        var body = NewBody();
        var rec = new byte[200];
        Array.Fill(rec, (byte)0xAB);

        int count = 0;
        while (new SlottedPage(body).TryInsert(rec, out _)) count++;
        count.Should().BeGreaterThan(0);

        // 容量超過後も既存レコードは読める
        var read = new SlottedPage(body);
        read.TryGet(0, out var g0).Should().BeTrue();
        g0.Length.Should().Be(200);
        read.SlotCount.Should().Be(count);
    }

    [Fact]
    public void Update_growth_fails_cleanly_when_no_room()
    {
        var body = NewBody();
        var filler = new byte[200];
        new SlottedPage(body).TryInsert(filler, out int target);
        // 残りを埋め尽くす
        while (new SlottedPage(body).TryInsert(filler, out _)) { }

        // target を本体より大きく拡大しようとしても失敗し、無変更で残る
        var huge = new byte[VersionedRecordHeap.MaxPayloadSize + 1_000];
        new SlottedPage(body).TryUpdate(target, huge).Should().BeFalse();
        new SlottedPage(body).TryGet(target, out var still).Should().BeTrue();
        still.Length.Should().Be(200);
    }
}
