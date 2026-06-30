using System.Buffers.Binary;
using System.Text;

namespace Quiver.Index.FullText;

/// <summary>
/// postings B+Tree の複合キーをエンコードする:
/// <c>termLen(2B BE) ‖ term_utf8 ‖ entityId(8B BE sign-flipped)</c>。
/// <para>
/// 2 バイトの長さプレフィックスにより、同一タームの postings が連続し、
/// term range scan 中のプレフィックス衝突 ("ab" vs "abc") を防ぐ。
/// 末尾の entityId は <see cref="Int64KeyCodec"/> と同じ order-preserving big-endian
/// sign-flip を使い、orphan sweep で entityId を復元できるようにする。
/// </para>
/// </summary>
internal static class PostingsKey
{
    private const int LenPrefix = 2;
    private const int EntityIdSize = 8;

    /// <summary><c>(term, entityId)</c> を新しいキーバッファにエンコードする。</summary>
    public static byte[] Encode(ReadOnlySpan<byte> termUtf8, long entityId)
    {
        if (termUtf8.Length > ushort.MaxValue)
            throw new ArgumentException($"Term too long ({termUtf8.Length} bytes, max {ushort.MaxValue}).", nameof(termUtf8));

        var key = new byte[LenPrefix + termUtf8.Length + EntityIdSize];
        BinaryPrimitives.WriteUInt16BigEndian(key, (ushort)termUtf8.Length);
        termUtf8.CopyTo(key.AsSpan(LenPrefix));
        WriteEntityId(key.AsSpan(LenPrefix + termUtf8.Length), entityId);
        return key;
    }

    /// <summary>
    /// タームの全 postings に対する inclusive な lower/upper バイト境界。
    /// <c>IBTreeIndex&lt;byte[]&gt;.Range(lower, true, upper, true)</c> で使用する。
    /// </summary>
    public static (byte[] Lower, byte[] Upper) TermRange(ReadOnlySpan<byte> termUtf8)
    {
        var lower = Encode(termUtf8, 0);  // entityId field set to all-min below
        var upper = Encode(termUtf8, 0);
        lower.AsSpan(LenPrefix + termUtf8.Length).Fill(0x00);
        upper.AsSpan(LenPrefix + termUtf8.Length).Fill(0xFF);
        return (lower, upper);
    }

    /// <summary>
    /// <paramref name="prefixUtf8"/> で始まる UTF-8 バイト長 <paramref name="termLen"/> のタームの
    /// 全 postings に対する inclusive な lower/upper バイト境界。
    /// prefix 展開で候補長ごとに 1 回呼ばれる。
    /// </summary>
    public static (byte[] Lower, byte[] Upper) PrefixRange(ReadOnlySpan<byte> prefixUtf8, int termLen)
    {
        if (termLen < prefixUtf8.Length)
            throw new ArgumentOutOfRangeException(nameof(termLen),
                "Term length must be >= prefix length.");

        var lower = new byte[LenPrefix + termLen + EntityIdSize];
        BinaryPrimitives.WriteUInt16BigEndian(lower, (ushort)termLen);
        prefixUtf8.CopyTo(lower.AsSpan(LenPrefix));
        // suffix + entityId already 0x00

        var upper = new byte[LenPrefix + termLen + EntityIdSize];
        BinaryPrimitives.WriteUInt16BigEndian(upper, (ushort)termLen);
        prefixUtf8.CopyTo(upper.AsSpan(LenPrefix));
        upper.AsSpan(LenPrefix + prefixUtf8.Length).Fill(0xFF);

        return (lower, upper);
    }

    /// <summary>postings キーの長さプレフィックスからターム (UTF-8 デコード) を復元する。</summary>
    public static string DecodeTerm(ReadOnlySpan<byte> key)
    {
        int termLen = BinaryPrimitives.ReadUInt16BigEndian(key);
        return Encoding.UTF8.GetString(key.Slice(LenPrefix, termLen));
    }

    /// <summary>postings キーの末尾 8 バイトから entityId を復元する。</summary>
    public static long DecodeEntityId(ReadOnlySpan<byte> key)
    {
        if (key.Length < LenPrefix + EntityIdSize)
            throw new ArgumentException("Key shorter than the minimum postings key length.", nameof(key));
        return ReadEntityId(key[^EntityIdSize..]);
    }

    private static void WriteEntityId(Span<byte> dest, long entityId)
        => BinaryPrimitives.WriteUInt64BigEndian(dest, (ulong)entityId ^ 0x8000_0000_0000_0000uL);

    private static long ReadEntityId(ReadOnlySpan<byte> src)
        => (long)(BinaryPrimitives.ReadUInt64BigEndian(src) ^ 0x8000_0000_0000_0000uL);
}
