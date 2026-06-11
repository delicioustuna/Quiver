using System.Text;
using Quiver.Text;

namespace Quiver.Index.FullText;

/// <summary>
/// A logical full-text index (design 13 section 3): a pair of B+Tree tenants — a
/// postings index (<c>byte[]</c> composite key <c>(term, entityId)</c> -> tf) and
/// a norms index (<c>long</c> key entityId -> docLen) — plus the metadata needed
/// to maintain and query it (label, property key, tokenizer id).
/// <para>
/// Postings and norms are ordinary B+Tree tenants, so transactional maintenance,
/// abort/crash rollback (index ARIES, FT-17/19), buffer pool, and WAL are all
/// inherited for free. Orphan sweep needs a dedicated path because the entityId
/// lives in the postings <i>key</i> and the norms <i>key</i>, not in the value
/// (lands in FTS-2 increment 4).
/// </para>
/// </summary>
internal sealed class FullTextIndex : IDisposable
{
    private readonly IBTreeIndex<byte[]> _postings;
    private readonly IBTreeIndex<long> _norms;

    internal FullTextIndex(
        string name, string label, string propertyKey, string tokenizerId,
        byte postingsTenantId, byte normsTenantId,
        IBTreeIndex<byte[]> postings, IBTreeIndex<long> norms)
    {
        Name = name;
        Label = label;
        PropertyKey = propertyKey;
        TokenizerId = tokenizerId;
        PostingsTenantId = postingsTenantId;
        NormsTenantId = normsTenantId;
        _postings = postings;
        _norms = norms;
    }

    public string Name { get; }
    public string Label { get; }
    public string PropertyKey { get; }
    public string TokenizerId { get; }
    public byte PostingsTenantId { get; }
    public byte NormsTenantId { get; }

    /// <summary>Number of documents (N for BM25); = norms entry count.</summary>
    public long DocumentCount => _norms.EntryCount;

    /// <summary>
    /// Tokenize <paramref name="text"/> and write its postings (per-term tf,
    /// saturated to u16) and norm (docLen). Caller resolves <paramref name="tokenizer"/>
    /// from <see cref="TokenizerId"/> so index- and query-time tokenization match.
    /// </summary>
    public void AddDocument(long entityId, ITokenizer tokenizer, ReadOnlySpan<char> text)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        var sink = new TfSink();
        tokenizer.Tokenize(text, sink);
        foreach (var (term, tf) in sink.Tf)
        {
            byte[] key = PostingsKey.Encode(Encoding.UTF8.GetBytes(term), entityId);
            _postings.Insert(key, Math.Min(tf, ushort.MaxValue));
        }
        _norms.Insert(entityId, sink.Total);
    }

    /// <summary>
    /// Remove every postings entry and the norm produced by <paramref name="text"/>
    /// for <paramref name="entityId"/>. The (term, entityId) key is unique, so each
    /// term's entry is looked up and deleted by its current value (robust to tf drift).
    /// </summary>
    public void RemoveDocument(long entityId, ITokenizer tokenizer, ReadOnlySpan<char> text)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        var sink = new TfSink();
        tokenizer.Tokenize(text, sink);
        foreach (var term in sink.Tf.Keys)
        {
            byte[] key = PostingsKey.Encode(Encoding.UTF8.GetBytes(term), entityId);
            var seek = _postings.Seek(key);
            if (seek.MoveNext())
                _postings.Delete(key, seek.Current);
        }
        foreach (var docLen in _norms.SeekValues(entityId).ToList())
            _norms.Delete(entityId, docLen);
    }

    /// <summary>All postings for <paramref name="term"/> as (entityId, tf), via prefix range scan.</summary>
    public List<(long EntityId, int Tf)> GetPostings(string term)
        => GetPostings(Encoding.UTF8.GetBytes(term));

    /// <summary>All postings for a UTF-8 term as (entityId, tf), via prefix range scan.</summary>
    public List<(long EntityId, int Tf)> GetPostings(ReadOnlySpan<byte> termUtf8)
    {
        var (lower, upper) = PostingsKey.TermRange(termUtf8);
        var result = new List<(long, int)>();
        var e = _postings.Range(lower, true, upper, true);
        while (e.MoveNext())
        {
            KeyValueEntry entry = e.Current;
            result.Add((PostingsKey.DecodeEntityId(entry.KeyBytes), (int)entry.Value));
        }
        return result;
    }

    /// <summary>Document length (token count) for <paramref name="entityId"/>, if indexed.</summary>
    public bool TryGetDocLength(long entityId, out int docLen)
    {
        foreach (var v in _norms.SeekValues(entityId))
        {
            docLen = (int)v;
            return true;
        }
        docLen = 0;
        return false;
    }

    /// <summary>FTS-2: abort の before-image undo 後に postings/norms のヘッダキャッシュを読み直す。</summary>
    public void ReloadFromHeader()
    {
        _postings.ReloadFromHeader();
        _norms.ReloadFromHeader();
    }

    public void Dispose()
    {
        _postings.Dispose();
        _norms.Dispose();
    }

    private sealed class TfSink : ITokenSink
    {
        public Dictionary<string, int> Tf { get; } = new(StringComparer.Ordinal);
        public int Total { get; private set; }

        public void Accept(ReadOnlySpan<char> token)
        {
            Total++;
            string s = token.ToString();
            Tf[s] = Tf.TryGetValue(s, out var c) ? c + 1 : 1;
        }
    }
}
