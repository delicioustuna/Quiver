namespace GraphDb.Engine.Index;

internal interface IKeyCodec<TKey>
{
    int GetEncodedSize(in TKey key);
    void Encode(in TKey key, Span<byte> dest);
    TKey Decode(ReadOnlySpan<byte> src);
    int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);
}
