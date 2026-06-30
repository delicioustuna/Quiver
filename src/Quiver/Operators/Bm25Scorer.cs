using System.Text;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Text;

namespace Quiver.Query.Physical;

/// <summary>
/// WAND 枝刈り用の per-term コーパス統計。<see cref="Quiver.GraphStats"/> 収集時に
/// 計算した <c>term → (df, maxTf)</c> テーブルとコーパス最短文書長のスナップショットを保持する。
/// df が idf (および per-term WAND 上限) を駆動する。
/// </summary>
/// <remarks>
/// WAND の per-term 上限はもはや maxTf / minDocLen を使わない (スナップショットがライブ index に
/// 対して stale だと過小評価され top-k を取りこぼすため)。代わりに tf/docLen に依らない漸近上限
/// <c>idf*(K1+1)</c> を使う。maxTf / minDocLen は将来の block-max WAND 等のために収集を残す。
/// </remarks>
internal sealed class Bm25TermStats
{
    private readonly Dictionary<string, (int Df, int MaxTf)> _terms;

    public Bm25TermStats(Dictionary<string, (int Df, int MaxTf)> terms, int minDocLen)
    {
        _terms = terms;
        MinDocLen = minDocLen;
    }

    /// <summary>コーパス中の最短文書長 (トークン数)。空の場合は 0。</summary>
    public int MinDocLen { get; }

    /// <summary>追跡中のユニーク語数。</summary>
    public int TermCount => _terms.Count;

    /// <summary><paramref name="term"/> の文書頻度と最大語頻度を返す。</summary>
    public bool TryGet(string term, out int df, out int maxTf)
    {
        if (_terms.TryGetValue(term, out var v)) { df = v.Df; maxTf = v.MaxTf; return true; }
        df = 0; maxTf = 0; return false;
    }
}

/// <summary>
/// コーパスレベルの BM25 統計 (文書数 N、平均文書長)。
/// <see cref="Quiver.GraphStats"/> からスナップショットし、クエリごとに norms インデックスを
/// 再走査する代わりに使用する (BM25 は統計の古さに頑健なため、定期収集の近似で十分)。
/// <para>
/// <see cref="Terms"/> が non-null の場合、full term-at-a-time スキャンの代わりに
/// WAND document-at-a-time 枝刈り (スナップショットからの per-term 上限) を使用できる。
/// </para>
/// </summary>
internal readonly record struct Bm25CorpusStats(
    long DocumentCount, double AverageDocLength, Bm25TermStats? Terms = null);

/// <summary>
/// text-first (<see cref="FullTextScanOperator"/>) と graph-first
/// (<see cref="FilteredFullTextScanOperator"/>) の両経路で共有する
/// term-at-a-time BM25 アキュムレータ。どちらの経路でも同一文書が同一スコアを得る
/// ことで、graph-first リライトが text-first + post-filter とランク等価になる。
/// per-term スナップショット統計が利用可能な場合に text-first 経路が使う
/// exact-top-k WAND (<see cref="RankWand"/>) も提供する。
/// </summary>
internal static class Bm25Scorer
{
    /// <summary>BM25 語頻度飽和パラメータ (標準既定値)。</summary>
    public const double K1 = 1.2;

    /// <summary>BM25 文書長正規化パラメータ (標準既定値)。</summary>
    public const double B = 0.75;

    /// <summary>
    /// <paramref name="queryText"/> に対し BM25 スコア降順で文書をランキングする
    /// (同スコアは packed entityId 昇順で決定論的に解決)。返却 ID は生の packed entityId
    /// (generation + sequence); 呼び出し元がライブ <c>NodeId</c> に解決する。
    /// <para>
    /// <paramref name="candidateSequences"/> が non-null の場合、そのセットに限定して
    /// 累積する (graph-first)。パイプライン上の NodeId は sequence 空間だが posting キーは
    /// packed のため、posting の <see cref="EntityRef.Sequence"/> で照合する。
    /// df / idf は全 posting リスト (または <paramref name="termStats"/> 提供時は
    /// スナップショット df) から計算し、WAND 経路とスコアを一致させる。
    /// </para>
    /// </summary>
    public static List<long> Rank(
        FullTextIndex ft, ITokenizer tokenizer, string queryText,
        long n, double avgdl, HashSet<long>? candidateSequences, Bm25TermStats? termStats = null)
    {
        var sink = new TermSink();
        tokenizer.Tokenize(queryText, sink);
        if (sink.Terms.Count == 0) return new List<long>();
        return RankTerms(ft, sink.Terms, n, avgdl, candidateSequences, termStats);
    }

    /// <summary>
    /// 事前展開済みのクエリ語セットで文書をランキングする
    /// (プレフィックスワイルドカードをインデックスに対して展開済みの場合に使用)。
    /// </summary>
    public static List<long> RankTerms(
        FullTextIndex ft, IReadOnlySet<string> queryTerms,
        long n, double avgdl, HashSet<long>? candidateSequences, Bm25TermStats? termStats = null)
    {
        if (queryTerms.Count == 0) return new List<long>();
        return SortByScore(AccumulateScores(ft, queryTerms, n, avgdl, candidateSequences, termStats));
    }

    /// <summary>
    /// Boolean FTS クエリ (AND/OR/NOT) で文書をランキングする。全正語 (Required + Optional) を
    /// 標準 BM25 でスコアリング後、Required 節は全一致・Excluded 節は全除外でポストフィルタする。
    /// 各節は文書がその節の語を 1 つでも含めば一致とみなす
    /// (<c>quiv*</c> のようなプレフィックス展開節で有用)。
    /// </summary>
    public static List<long> RankBoolean(
        FullTextIndex ft, ParsedFtsQuery query,
        long n, double avgdl, HashSet<long>? candidateSequences, Bm25TermStats? termStats = null)
    {
        var allPositive = query.AllPositiveTerms();
        if (allPositive.Count == 0) return new List<long>();

        var scores = AccumulateScores(ft, allPositive, n, avgdl, candidateSequences, termStats);

        foreach (var clause in query.Clauses)
        {
            if (clause.Mode != FtsClauseMode.Required || clause.Terms.Count == 0) continue;
            var clauseEids = CollectPostingEids(ft, clause.Terms);
            var toRemove = new List<long>();
            foreach (var eid in scores.Keys)
                if (!clauseEids.Contains(eid))
                    toRemove.Add(eid);
            foreach (var eid in toRemove) scores.Remove(eid);
        }

        foreach (var clause in query.Clauses)
        {
            if (clause.Mode != FtsClauseMode.Excluded || clause.Terms.Count == 0) continue;
            var excludeEids = CollectPostingEids(ft, clause.Terms);
            foreach (var eid in excludeEids)
                scores.Remove(eid);
        }

        return SortByScore(scores);
    }

    private static Dictionary<long, double> AccumulateScores(
        FullTextIndex ft, IReadOnlySet<string> queryTerms,
        long n, double avgdl, HashSet<long>? candidateSequences, Bm25TermStats? termStats)
    {
        var scores = new Dictionary<long, double>();
        var docLenCache = new Dictionary<long, int>();
        foreach (var term in queryTerms)
        {
            var postings = ft.GetPostings(term);
            int df = termStats is not null && termStats.TryGet(term, out var sdf, out _) ? sdf : postings.Count;
            if (df == 0) continue;
            double idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));
            foreach (var (eid, tf) in postings)
            {
                if (candidateSequences is not null && !candidateSequences.Contains(EntityRef.Sequence(eid)))
                    continue;
                if (!docLenCache.TryGetValue(eid, out var dl))
                {
                    dl = ft.TryGetDocLength(eid, out var d) ? d : 0;
                    docLenCache[eid] = dl;
                }
                double denom = tf + K1 * (1.0 - B + (avgdl > 0 ? B * dl / avgdl : 0.0));
                double contrib = denom > 0 ? idf * (tf * (K1 + 1.0)) / denom : 0.0;
                scores[eid] = scores.TryGetValue(eid, out var prev) ? prev + contrib : contrib;
            }
        }
        return scores;
    }

    private static HashSet<long> CollectPostingEids(FullTextIndex ft, IReadOnlySet<string> terms)
    {
        var eids = new HashSet<long>();
        foreach (var term in terms)
            foreach (var (eid, _) in ft.GetPostings(term))
                eids.Add(eid);
        return eids;
    }

    /// <summary>
    /// WAND document-at-a-time 枝刈りによる exact top-<paramref name="k"/> BM25。
    /// per-term スナップショット df + 上限を使い、k 番目のスコアを超えられない高 df 語の
    /// posting をスキップし、遅れたカーソルを B+Tree <c>SeekTo</c> で前進させる。
    /// ランク済み packed entityId を返す。クエリ語が <paramref name="termStats"/> に無い場合は
    /// <c>null</c> を返し、呼び出し元が <see cref="Rank"/> にフォールバックする。
    /// <para>
    /// <paramref name="isLive"/> 指定時は dead / slot 再利用の posting を heap 投入前に
    /// 除外し、heap が top-k の <em>ライブ</em> 文書を保持する (heap は k 上限のため、
    /// 事後フィルタでは結果が k 未満に縮む可能性がある)。
    /// </para>
    /// </summary>
    public static List<long>? RankWand(
        FullTextIndex ft, ITokenizer tokenizer, string queryText,
        long n, double avgdl, Bm25TermStats termStats, int k, Func<long, bool>? isLive = null)
    {
        var sink = new TermSink();
        tokenizer.Tokenize(queryText, sink);
        return RankWandTerms(ft, sink.Terms, n, avgdl, termStats, k, isLive);
    }

    /// <summary>
    /// 事前展開済み語セットを受け取る WAND 変種 (プレフィックスワイルドカードクエリ用)。
    /// スナップショットに含まれない語がある場合は <c>null</c> を返す
    /// (呼び出し元が <see cref="RankTerms"/> にフォールバック)。
    /// </summary>
    public static List<long>? RankWandTerms(
        FullTextIndex ft, IReadOnlySet<string> queryTerms,
        long n, double avgdl, Bm25TermStats termStats, int k, Func<long, bool>? isLive = null)
    {
        if (queryTerms.Count == 0) return new List<long>();

        var cursors = new List<WandTerm>(queryTerms.Count);
        foreach (var term in queryTerms)
        {
            if (!termStats.TryGet(term, out int df, out _))
                return null; // unknown term: cannot bound safely → fall back to full scan
            if (df <= 0) continue;
            double idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));
            // per-term の上限は BM25 項寄与の漸近上限 idf*(K1+1) を使う。
            // 寄与 idf*(tf*(K1+1))/(tf + K1*lenNorm) は tf について単調増加で tf→∞ で
            // idf*(K1+1) に収束し、lenNorm≥0 なので任意の tf/docLen に対し ≤ idf*(K1+1)。
            // snapshot の maxTf/minDocLen に依存しないため、snapshot 後にライブ index へ
            // 高 tf / 短文書が増えても上限が過小評価されず top-k を取りこぼさない。
            double ub = idf * (K1 + 1.0);
            var cur = ft.OpenPostingsCursor(Encoding.UTF8.GetBytes(term));
            if (cur.MoveNext()) cursors.Add(new WandTerm(idf, ub, cur));
        }
        if (cursors.Count == 0) return new List<long>();

        var heap = new BoundedTopK(k);
        while (true)
        {
            for (int i = cursors.Count - 1; i >= 0; i--)
                if (cursors[i].Cursor.Exhausted) cursors.RemoveAt(i);
            if (cursors.Count == 0) break;

            cursors.Sort(static (a, b) => a.Cursor.CurrentEid.CompareTo(b.Cursor.CurrentEid));
            double theta = heap.IsFull ? heap.MinScore : double.NegativeInfinity;

            // pivot = 累積上限が theta を超える最小インデックス。
            double cum = 0;
            int pivot = -1;
            for (int i = 0; i < cursors.Count; i++)
            {
                cum += cursors[i].Ub;
                if (cum > theta) { pivot = i; break; }
            }
            if (pivot < 0) break; // 残りの文書は k 番目のスコアを超えられない

            long pivotEid = cursors[pivot].Cursor.CurrentEid;
            if (cursors[0].Cursor.CurrentEid == pivotEid)
            {
                int docLen = ft.TryGetDocLength(pivotEid, out var dl) ? dl : 0;
                double lenNorm = 1.0 - B + (avgdl > 0 ? B * docLen / avgdl : 0.0);
                double score = 0;
                for (int i = 0; i < cursors.Count; i++)
                {
                    if (cursors[i].Cursor.CurrentEid != pivotEid) continue;
                    int tf = cursors[i].Cursor.CurrentTf;
                    double denom = tf + K1 * lenNorm;
                    if (denom > 0) score += cursors[i].Idf * (tf * (K1 + 1.0)) / denom;
                    cursors[i].Cursor.MoveNext();
                }
                if (isLive is null || isLive(pivotEid))
                    heap.Offer(pivotEid, score);
            }
            else
            {
                // 最小 (遅延) カーソルを pivot 文書まで前進させる。
                cursors[0].Cursor.SeekTo(pivotEid);
            }
        }

        return heap.ToRankedEids();
    }

    /// <summary>
    /// N / avgdl を解決する。<paramref name="corpus"/> スナップショットに文書があれば
    /// それを使い (GraphStats 経路、O(N) norms スキャンを回避)、
    /// なければ norms summary へのフォールバック。
    /// </summary>
    public static (long N, double Avgdl) ResolveCorpus(FullTextIndex ft, Bm25CorpusStats? corpus)
    {
        if (corpus is { DocumentCount: > 0 } c)
            return (c.DocumentCount, c.AverageDocLength);
        long n = ft.DocumentCount;
        var (_, totalTokens) = ft.NormsSummary();
        double avgdl = n > 0 ? (double)totalTokens / n : 0.0;
        return (n, avgdl);
    }

    private static List<long> SortByScore(Dictionary<long, double> scores)
    {
        var ranked = new List<KeyValuePair<long, double>>(scores);
        ranked.Sort(static (a, b) =>
        {
            int byScore = b.Value.CompareTo(a.Value);
            return byScore != 0 ? byScore : a.Key.CompareTo(b.Key);
        });
        var result = new List<long>(ranked.Count);
        foreach (var kv in ranked) result.Add(kv.Key);
        return result;
    }

    private readonly struct WandTerm
    {
        public WandTerm(double idf, double ub, PostingsCursor cursor)
        {
            Idf = idf; Ub = ub; Cursor = cursor;
        }

        public double Idf { get; }
        public double Ub { get; }
        public PostingsCursor Cursor { get; }
    }

    /// <summary>
    /// (score desc, entityId asc) の全順序で上位 k 件を保持する。
    /// <see cref="SortByScore"/> と同じ順序。満杯時の <see cref="MinScore"/> が WAND の閾値 θ。
    /// k は小さい (数十) のでワーストエントリ探索はリニアスキャン。
    /// </summary>
    private sealed class BoundedTopK
    {
        private readonly int _k;
        private readonly List<(long Eid, double Score)> _items;

        public BoundedTopK(int k) { _k = k; _items = new List<(long, double)>(k); }

        public bool IsFull => _items.Count >= _k;

        /// <summary>現在保持中の最低スコア (k 番目)。満杯時のみ有効。</summary>
        public double MinScore { get; private set; }

        public void Offer(long eid, double score)
        {
            if (_items.Count < _k)
            {
                _items.Add((eid, score));
                if (_items.Count == _k) RecomputeMin();
                return;
            }
            // 保持中の最悪エントリを探す: 最低スコア、同スコアでは最大 eid
            // (同スコアでは小さい eid を残し SortByScore と一致させる)。
            int worst = 0;
            for (int i = 1; i < _items.Count; i++)
            {
                if (_items[i].Score < _items[worst].Score ||
                    (_items[i].Score == _items[worst].Score && _items[i].Eid > _items[worst].Eid))
                    worst = i;
            }
            var w = _items[worst];
            if (score > w.Score || (score == w.Score && eid < w.Eid))
            {
                _items[worst] = (eid, score);
                RecomputeMin();
            }
        }

        private void RecomputeMin()
        {
            double min = double.PositiveInfinity;
            foreach (var it in _items) if (it.Score < min) min = it.Score;
            MinScore = min;
        }

        public List<long> ToRankedEids()
        {
            _items.Sort(static (a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : a.Eid.CompareTo(b.Eid);
            });
            var result = new List<long>(_items.Count);
            foreach (var it in _items) result.Add(it.Eid);
            return result;
        }
    }

    private sealed class TermSink : ITokenSink
    {
        public HashSet<string> Terms { get; } = new(StringComparer.Ordinal);
        public void Accept(ReadOnlySpan<char> token) => Terms.Add(token.ToString());
    }
}
