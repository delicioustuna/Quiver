namespace Quiver.Core;

/// <summary>
/// ARCH-3: B+Tree 索引の「値レーン」(裸の <c>long</c>) に世代 (Generation) を載せるための
/// 64 ビットパック表現。slot の物理再利用 (vacuum free list) に伴う stale 索引エントリが
/// 別の生存エンティティを指す ABA 問題を、解決時の世代照合で検出可能にする。
///
/// <para>レイアウト (上位→下位、<see cref="EntityId.ToPacked"/> の「上位 4bit = Kind」規約と整合):</para>
/// <list type="bullet">
///   <item>bits [63..60] = <see cref="EntityKind"/> (4bit)。索引は現状 Node 限定だが、
///     将来の relationship-property 索引 / ベクトル binding (Phase 3) で rel/vector も同じ
///     packing を再利用できるよう Kind を予約する (docs/design/11 §3.3 / Poseidon Fig.2)。</item>
///   <item>bits [59..44] = Generation (16bit)。slot ごとの incarnation。65,536 回再利用まで。</item>
///   <item>bits [43..0] = Sequence (44bit)。= <c>NodeId.Value</c> 等の局所 ID。17.6 兆まで。</item>
/// </list>
///
/// <para>B+Tree は値を 8B のまま等値比較するだけなのでページ/codec は不変
/// (docs/design/11 §3.3 の 64bit パック案に整合)。Generation は slot incarnation を表し、
/// MVCC version (xmin/xmax/sstamp) とは別概念として並存する。</para>
///
/// <para>論理ハンドルの <see cref="EntityRef"/> (Kind, Id) とは別物 — こちらは「世代付きの
/// 永続パック表現」。Phase 2 で両者を統一する想定。</para>
/// </summary>
internal static class GenerationalRef
{
    /// <summary>Kind フィールドの開始ビット位置。</summary>
    public const int KindShift = 60;

    /// <summary>Generation フィールドの開始ビット位置。</summary>
    public const int GenerationShift = 44;

    /// <summary>Generation のビット幅。</summary>
    public const int GenerationBits = 16;

    /// <summary>Sequence のビット幅。</summary>
    public const int SequenceBits = 44;

    /// <summary>Sequence の最大値 (= 下位 44 ビットのマスク)。</summary>
    public const long SequenceMask = (1L << SequenceBits) - 1;

    /// <summary>Generation の最大値。これを超える再利用要求があった slot は永久退役させる。</summary>
    public const int MaxGeneration = (1 << GenerationBits) - 1;

    private const long GenerationMask = (1L << GenerationBits) - 1;

    /// <summary>
    /// (Kind, Sequence, Generation) を 64 ビット値にパックする。<paramref name="sequence"/> は
    /// 0..<see cref="SequenceMask"/>、<paramref name="generation"/> は 0..<see cref="MaxGeneration"/>。
    /// </summary>
    public static long Pack(EntityKind kind, long sequence, int generation)
    {
        if (sequence < 0 || sequence > SequenceMask)
            throw new ArgumentOutOfRangeException(nameof(sequence),
                $"Sequence {sequence} は {SequenceBits} ビット (0..{SequenceMask}) に収まりません。");
        if (generation < 0 || generation > MaxGeneration)
            throw new ArgumentOutOfRangeException(nameof(generation),
                $"Generation {generation} は {GenerationBits} ビット (0..{MaxGeneration}) に収まりません。");
        return ((long)(byte)kind << KindShift)
             | ((long)generation << GenerationShift)
             | (sequence & SequenceMask);
    }

    /// <summary>パック済み値から <see cref="EntityKind"/> を取り出す。</summary>
    public static EntityKind Kind(long packed) => (EntityKind)(byte)((ulong)packed >> KindShift & 0xF);

    /// <summary>パック済み値から Generation を取り出す。</summary>
    public static int Generation(long packed) => (int)((ulong)packed >> GenerationShift & GenerationMask);

    /// <summary>パック済み値から Sequence (局所 ID) を取り出す。</summary>
    public static long Sequence(long packed) => packed & SequenceMask;
}
