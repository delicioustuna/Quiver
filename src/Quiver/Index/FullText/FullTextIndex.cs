using System.Text;
using Quiver.Text;
using static Quiver.Text.MixedBigramTokenizer;

namespace Quiver.Index.FullText;

/// <summary>
/// 論理的な全文索引: postings 索引 (<c>byte[]</c> 複合キー <c>(term, entityId)</c> -> tf) と
/// norms 索引 (<c>long</c> キー entityId -> docLen) の B+Tree テナントペア、
/// および維持・検索に必要なメタデータ (label, property key, tokenizer id)。
/// <para>
/// postings/norms は通常の B+Tree テナントなので、トランザクション維持、
/// abort の in-process rollback、buffer pool、commit winner の page-WAL はすべて継承される。
/// orphan sweep は entityId が値ではなく postings/norms の<i>キー</i>に入っているため
/// 専用経路が必要。
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

    /// <summary>ドキュメント数 (BM25 の N)。norms のエントリ数と等しい。</summary>
    public long DocumentCount => _norms.EntryCount;

    /// <summary>
    /// BM25 統計: ドキュメント数 N と合計ドキュメント長 (avgdl = total / N)。
    /// norms を 1 回スキャンして算出する近似値で、BM25 には十分。
    /// corpus レベルの N/avgdl は GraphStats にも保持される。
    /// </summary>
    public (long DocCount, long TotalTokens) NormsSummary()
    {
        long count = 0, total = 0;
        foreach (var kv in _norms.EnumerateRawEntries())
        {
            count++;
            total += kv.Value;
        }
        return (count, total);
    }

    /// <summary>
    /// <paramref name="text"/> をトークナイズし、postings (ターム単位の tf、u16 飽和) と
    /// norm (docLen) を書き込む。呼び出し側が <see cref="TokenizerId"/> から
    /// <paramref name="tokenizer"/> を解決することで、インデックス時とクエリ時の
    /// トークナイゼーションが一致する。
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

        int docLen = tokenizer is INormTokenCounter counter
            ? counter.CountNormTokens(text)
            : sink.Total;
        if (docLen < 0) docLen = sink.Total;
        _norms.Insert(entityId, docLen);
    }

    /// <summary>
    /// <paramref name="entityId"/> について <paramref name="text"/> が生成した全 postings エントリ
    /// と norm を削除する。(term, entityId) キーは一意なので、各タームのエントリは
    /// 現在の値で検索・削除する (tf のドリフトに頑健)。
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

    /// <summary><paramref name="term"/> の全 postings を (entityId, tf) として返す。prefix range scan による。</summary>
    public List<(long EntityId, int Tf)> GetPostings(string term)
        => GetPostings(Encoding.UTF8.GetBytes(term));

    /// <summary>UTF-8 タームの全 postings を (entityId, tf) として返す。prefix range scan による。</summary>
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

    /// <summary>
    /// 正規化済み <paramref name="termUtf8"/> から Levenshtein 編集距離
    /// <paramref name="maxEditDistance"/> 以内の全索引タームを収集する。
    /// postings B+Tree をバイト長ごとに range scan し、文字レベルの編集距離でフィルタする。
    /// マルチバイト UTF-8 を考慮したバイト長の走査窓を使う。
    /// </summary>
    internal HashSet<string> ExpandFuzzy(ReadOnlySpan<byte> termUtf8, int maxEditDistance)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        string queryTerm = Encoding.UTF8.GetString(termUtf8);
        int termByteLen = termUtf8.Length;
        if (termByteLen == 0 || maxEditDistance <= 0) return terms;

        bool queryIsCjkUnigram = queryTerm.Length == 1 && IsCjk(queryTerm[0]);

        int minLen = Math.Max(1, termByteLen - maxEditDistance * 4);
        int maxLen = termByteLen + maxEditDistance * 4;
        const int maxTermLen = 256;

        int emptyStreak = 0;
        for (int len = minLen; len <= Math.Min(maxLen, maxTermLen) && emptyStreak < 32; len++)
        {
            var (lower, upper) = PostingsKey.PrefixRange(Array.Empty<byte>(), len);
            var e = _postings.Range(lower, true, upper, true);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool found = false;
            while (e.MoveNext())
            {
                string candidate = PostingsKey.DecodeTerm(e.Current.KeyBytes);
                if (seen.Add(candidate))
                {
                    found = true;

                    // CJK ユニグラム同士の置換展開を禁止 (全 CJK 文字が相互に
                    // edit distance 1 となり N² 爆発するため)。
                    if (queryIsCjkUnigram
                        && candidate.Length == 1 && IsCjk(candidate[0])
                        && candidate[0] != queryTerm[0])
                        continue;

                    if (LevenshteinDistance(queryTerm, candidate) <= maxEditDistance)
                        terms.Add(candidate);
                }
            }
            if (found) emptyStreak = 0;
            else emptyStreak++;
        }
        return terms;
    }

    /// <summary>
    /// <paramref name="prefixUtf8"/> で始まる全索引タームを収集する。
    /// postings B+Tree をバイト長ごとに range scan する (複合キーの 2 バイト長プレフィックスにより
    /// 異なる長さのタームは連続しないため)。
    /// </summary>
    internal HashSet<string> ExpandPrefix(ReadOnlySpan<byte> prefixUtf8)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        int prefixLen = prefixUtf8.Length;
        if (prefixLen == 0) return terms;

        const int maxTermLen = 256;
        int emptyStreak = 0;
        for (int len = prefixLen; len <= maxTermLen && emptyStreak < 32; len++)
        {
            var (lower, upper) = PostingsKey.PrefixRange(prefixUtf8, len);
            var e = _postings.Range(lower, true, upper, true);
            bool found = false;
            while (e.MoveNext())
            {
                found = true;
                terms.Add(PostingsKey.DecodeTerm(e.Current.KeyBytes));
            }
            if (found) emptyStreak = 0;
            else emptyStreak++;
        }
        return terms;
    }

    /// <summary>
    /// WAND 枝刈り用のタームごと統計。postings を 1 回走査して
    /// <c>term → (df, maxTf)</c>、norms を 1 回走査して <c>(minDocLen, N, totalTokens)</c> を得る。
    /// <see cref="Quiver.GraphStats"/> 収集時のみ呼ばれ (クエリごとではない)、
    /// スナップショットが BM25 の N/avgdl とタームごと上界の両方を駆動する。
    /// df はタームの posting 数、maxTf は観測最大 tf (スコア分子の上限)。
    /// </summary>
    internal (Dictionary<string, (int Df, int MaxTf)> Terms, int MinDocLen, long DocCount, long TotalTokens) CollectTermStats()
    {
        var terms = new Dictionary<string, (int Df, int MaxTf)>(StringComparer.Ordinal);
        foreach (var kv in _postings.EnumerateRawEntries())
        {
            string term = PostingsKey.DecodeTerm(kv.Key);
            int tf = (int)kv.Value;
            if (terms.TryGetValue(term, out var cur))
                terms[term] = (cur.Df + 1, Math.Max(cur.MaxTf, tf));
            else
                terms[term] = (1, tf);
        }

        long count = 0, total = 0;
        int minDocLen = int.MaxValue;
        foreach (var kv in _norms.EnumerateRawEntries())
        {
            count++;
            total += kv.Value;
            if (kv.Value < minDocLen) minDocLen = (int)kv.Value;
        }
        if (count == 0) minDocLen = 0;
        return (terms, minDocLen, count, total);
    }

    /// <summary>
    /// タームの postings に対する forward-only seekable cursor (entityId 昇順) を開く。
    /// WAND の document-at-a-time スコアリング用。<see cref="PostingsCursor.SeekTo"/> で
    /// B+Tree root 降下により pivot entityId へスキップする。
    /// </summary>
    internal PostingsCursor OpenPostingsCursor(ReadOnlySpan<byte> termUtf8)
    {
        var (lower, upper) = PostingsKey.TermRange(termUtf8);
        return new PostingsCursor(_postings.OpenScanCursor(lower, upper), termUtf8.ToArray());
    }

    /// <summary><paramref name="entityId"/> のドキュメント長 (トークン数)。索引済みの場合のみ有効。</summary>
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

    /// <summary>abort の before-image undo 後に postings/norms のヘッダキャッシュを読み直す。</summary>
    public void ReloadFromHeader()
    {
        _postings.ReloadFromHeader();
        _norms.ReloadFromHeader();
    }

    // ---- orphan sweep support ----
    // postings は entityId を key 末尾 8B に、norms は entityId を key (Int64) に持つため、
    // 値ベースの汎用 sweep ではなく key からの entityId デコードが要る。

    internal IEnumerable<KeyValuePair<byte[], long>> EnumeratePostingsRaw() => _postings.EnumerateRawEntries();
    internal IEnumerable<KeyValuePair<byte[], long>> EnumerateNormsRaw() => _norms.EnumerateRawEntries();
    internal bool DeletePostingsRaw(ReadOnlySpan<byte> rawKey, long value) => _postings.DeleteRawEntry(rawKey, value);
    internal bool DeleteNormsRaw(ReadOnlySpan<byte> rawKey, long value) => _norms.DeleteRawEntry(rawKey, value);

    public void Dispose()
    {
        _postings.Dispose();
        _norms.Dispose();
    }

    internal static int LevenshteinDistance(string s, string t)
    {
        int sLen = s.Length, tLen = t.Length;
        if (sLen == 0) return tLen;
        if (tLen == 0) return sLen;

        var prev = new int[tLen + 1];
        for (int j = 0; j <= tLen; j++) prev[j] = j;

        for (int i = 1; i <= sLen; i++)
        {
            int prevDiag = prev[0];
            prev[0] = i;
            for (int j = 1; j <= tLen; j++)
            {
                int temp = prev[j];
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                prev[j] = Math.Min(Math.Min(prev[j] + 1, prev[j - 1] + 1), prevDiag + cost);
                prevDiag = temp;
            }
        }
        return prev[tLen];
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

/// <summary>
/// WAND 用の単一タームの postings cursor。タームの <c>(term, entityId)</c> キー範囲に
/// 対する raw B+Tree cursor をラップし、デコード済み entityId と tf、および pivot
/// entityId へスキップする <see cref="SeekTo"/> を提供する。cursor は entityId 順
/// (= 複合キー順) に進むため、マルチターム merge で postings 自体の明示的ソートは不要。
/// </summary>
internal sealed class PostingsCursor
{
    private readonly BTreeRawCursor _raw;
    private readonly byte[] _termUtf8;

    internal PostingsCursor(BTreeRawCursor raw, byte[] termUtf8)
    {
        _raw = raw;
        _termUtf8 = termUtf8;
    }

    public bool Exhausted => _raw.Exhausted;
    public long CurrentEid { get; private set; }
    public int CurrentTf { get; private set; }

    public bool MoveNext()
    {
        if (!_raw.MoveNext()) return false;
        Decode();
        return true;
    }

    /// <summary>entityId が <paramref name="eid"/> 以上の最初の posting まで前方スキップする。</summary>
    public bool SeekTo(long eid)
    {
        if (!_raw.SeekTo(PostingsKey.Encode(_termUtf8, eid))) return false;
        Decode();
        return true;
    }

    private void Decode()
    {
        CurrentEid = PostingsKey.DecodeEntityId(_raw.CurrentKey);
        CurrentTf = (int)_raw.CurrentValue;
    }
}
