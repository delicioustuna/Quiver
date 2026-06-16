using System.Buffers.Binary;
using System.Text;

namespace Quiver.Index.FullText;

/// <summary>
/// Encodes the postings B+Tree composite key:
/// <c>termLen(2B BE) ‖ term_utf8 ‖ entityId(8B BE sign-flipped)</c>.
/// <para>
/// The 2-byte length prefix keeps a term's postings contiguous and prevents
/// prefix collisions ("ab" vs "abc") during a term range scan: without it,
/// <c>"abc"+entityId</c> would fall inside the byte range of term <c>"ab"</c>.
/// The trailing entityId uses the same order-preserving big-endian sign-flip as
/// <see cref="Int64KeyCodec"/> so the entityId can be recovered for orphan sweep.
/// </para>
/// </summary>
internal static class PostingsKey
{
    private const int LenPrefix = 2;
    private const int EntityIdSize = 8;

    /// <summary>Encode <c>(term, entityId)</c> into a fresh key buffer.</summary>
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
    /// Inclusive lower/upper byte bounds for a term's full postings list, for use
    /// with <c>IBTreeIndex&lt;byte[]&gt;.Range(lower, true, upper, true)</c>.
    /// </summary>
    public static (byte[] Lower, byte[] Upper) TermRange(ReadOnlySpan<byte> termUtf8)
    {
        var lower = Encode(termUtf8, 0);  // entityId field set to all-min below
        var upper = Encode(termUtf8, 0);
        lower.AsSpan(LenPrefix + termUtf8.Length).Fill(0x00);
        upper.AsSpan(LenPrefix + termUtf8.Length).Fill(0xFF);
        return (lower, upper);
    }

    /// <summary>Recover the term (UTF-8 decoded) from a postings key's length-prefixed prefix.</summary>
    public static string DecodeTerm(ReadOnlySpan<byte> key)
    {
        int termLen = BinaryPrimitives.ReadUInt16BigEndian(key);
        return Encoding.UTF8.GetString(key.Slice(LenPrefix, termLen));
    }

    /// <summary>Recover the entityId from a postings key (its trailing 8 bytes).</summary>
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
