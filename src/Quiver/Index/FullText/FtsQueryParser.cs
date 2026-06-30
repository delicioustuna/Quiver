using System.Text;
using Quiver.Text;

namespace Quiver.Index.FullText;

/// <summary>
/// 全文検索クエリ文字列を前処理し、完全一致ターム・prefix ターム (末尾 <c>*</c>)・
/// fuzzy ターム (末尾 <c>~N</c>) に分割する。Boolean 演算子 (<c>AND</c>, <c>OR</c>,
/// <c>NOT</c>) にも対応する。prefix タームは索引の B+Tree に対して展開し、
/// fuzzy タームは Levenshtein 編集距離で展開する。展開後のターム集合が BM25
/// スコアラに渡される。
/// </summary>
internal static class FtsQueryParser
{
    /// <summary>
    /// <paramref name="queryText"/> をパースし、全タームを解決する (prefix ターム・fuzzy タームを
    /// <paramref name="index"/> に対して展開)。スコアリング対象のターム集合を返す。
    /// </summary>
    public static HashSet<string> ParseAndExpand(
        string queryText, ITokenizer tokenizer, FullTextIndex index)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        ReadOnlySpan<char> span = queryText.AsSpan();
        int i = 0;

        while (i < span.Length)
        {
            while (i < span.Length && !char.IsLetterOrDigit(span[i]) && span[i] != '*')
                i++;
            if (i >= span.Length) break;

            int start = i;
            while (i < span.Length && char.IsLetterOrDigit(span[i]))
                i++;
            int wordEnd = i;

            bool isPrefix = i < span.Length && span[i] == '*';
            if (isPrefix) i++;

            int fuzzyDist = 0;
            if (!isPrefix && i < span.Length && span[i] == '~')
            {
                i++;
                if (i < span.Length && char.IsAsciiDigit(span[i]))
                {
                    fuzzyDist = span[i] - '0';
                    i++;
                }
                else
                {
                    fuzzyDist = 1;
                }
                if (fuzzyDist > 2) fuzzyDist = 2;
            }

            var word = span[start..wordEnd];
            if (word.IsEmpty) continue;

            if (isPrefix)
            {
                var sink = new SingleTokenSink();
                tokenizer.Tokenize(word, sink);
                if (sink.Token is not null)
                {
                    byte[] prefixUtf8 = Encoding.UTF8.GetBytes(sink.Token);
                    foreach (var expanded in index.ExpandPrefix(prefixUtf8))
                        result.Add(expanded);
                }
            }
            else if (fuzzyDist > 0)
            {
                var sink = new SingleTokenSink();
                tokenizer.Tokenize(word, sink);
                if (sink.Token is not null)
                {
                    byte[] termUtf8 = Encoding.UTF8.GetBytes(sink.Token);
                    foreach (var expanded in index.ExpandFuzzy(termUtf8, fuzzyDist))
                        result.Add(expanded);
                }
            }
            else
            {
                tokenizer.Tokenize(word, new CollectingSink(result));
            }
        }

        return result;
    }

    /// <summary>
    /// <paramref name="queryText"/> に prefix ワイルドカード (<c>*</c>) が 1 つでも含まれていれば
    /// <c>true</c>。呼び出し側が完全一致のみの common case を fast-path するために使う。
    /// </summary>
    public static bool ContainsWildcard(string queryText)
        => queryText.Contains('*');

    /// <summary>
    /// <paramref name="queryText"/> に fuzzy 修飾子 (英数字の後に <c>~</c>) が 1 つでも含まれていれば <c>true</c>。
    /// </summary>
    public static bool ContainsFuzzy(string queryText)
    {
        for (int i = 1; i < queryText.Length; i++)
            if (queryText[i] == '~' && char.IsLetterOrDigit(queryText[i - 1]))
                return true;
        return false;
    }

    /// <summary>
    /// <paramref name="queryText"/> に Boolean 演算子 (<c>AND</c>, <c>OR</c>, <c>NOT</c> — 大文字のみ、Lucene 慣例) が
    /// 1 つでも含まれていれば <c>true</c>。
    /// </summary>
    public static bool ContainsBooleanOps(string queryText)
    {
        ReadOnlySpan<char> span = queryText.AsSpan();
        int i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && !char.IsLetterOrDigit(span[i]))
                i++;
            if (i >= span.Length) break;

            int start = i;
            while (i < span.Length && char.IsLetterOrDigit(span[i]))
                i++;

            var word = span[start..i];
            if (word is "AND" or "OR" or "NOT")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Boolean クエリ (<c>AND</c>/<c>OR</c>/<c>NOT</c>) をパースし、全タームを索引に対して
    /// 展開する (prefix ワイルドカード・fuzzy ターム含む)。clause 単位の
    /// Required/Optional/Excluded グルーピングを持つ構造化結果を返す。
    /// </summary>
    public static ParsedFtsQuery ParseBooleanAndExpand(
        string queryText, ITokenizer tokenizer, FullTextIndex index)
    {
        var rawTokens = Tokenize(queryText.AsSpan());
        if (rawTokens.Count == 0)
            return new ParsedFtsQuery(Array.Empty<FtsClause>());

        var clauses = new List<FtsClause>();
        FtsClauseMode nextMode = FtsClauseMode.Optional;
        int lastTermIdx = -1;

        for (int t = 0; t < rawTokens.Count; t++)
        {
            var (text, isPrefix, fuzzyDist, kind) = rawTokens[t];

            if (kind == RawTokenKind.And)
            {
                if (lastTermIdx >= 0 && clauses[lastTermIdx].Mode == FtsClauseMode.Optional)
                    clauses[lastTermIdx] = clauses[lastTermIdx].WithMode(FtsClauseMode.Required);
                nextMode = FtsClauseMode.Required;
                continue;
            }
            if (kind == RawTokenKind.Or)
            {
                nextMode = FtsClauseMode.Optional;
                continue;
            }
            if (kind == RawTokenKind.Not)
            {
                nextMode = FtsClauseMode.Excluded;
                continue;
            }

            var terms = ExpandSingleWord(text, isPrefix, fuzzyDist, tokenizer, index);
            clauses.Add(new FtsClause(terms, nextMode));
            lastTermIdx = clauses.Count - 1;
            nextMode = FtsClauseMode.Optional;
        }

        return new ParsedFtsQuery(clauses);
    }

    private static HashSet<string> ExpandSingleWord(
        string word, bool isPrefix, int fuzzyDistance, ITokenizer tokenizer, FullTextIndex index)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (isPrefix)
        {
            var sink = new SingleTokenSink();
            tokenizer.Tokenize(word, sink);
            if (sink.Token is not null)
            {
                byte[] prefixUtf8 = Encoding.UTF8.GetBytes(sink.Token);
                foreach (var expanded in index.ExpandPrefix(prefixUtf8))
                    result.Add(expanded);
            }
        }
        else if (fuzzyDistance > 0)
        {
            var sink = new SingleTokenSink();
            tokenizer.Tokenize(word, sink);
            if (sink.Token is not null)
            {
                byte[] termUtf8 = Encoding.UTF8.GetBytes(sink.Token);
                foreach (var expanded in index.ExpandFuzzy(termUtf8, fuzzyDistance))
                    result.Add(expanded);
            }
        }
        else
        {
            tokenizer.Tokenize(word, new CollectingSink(result));
        }
        return result;
    }

    private enum RawTokenKind { Term, And, Or, Not }

    private static List<(string Text, bool IsPrefix, int FuzzyDistance, RawTokenKind Kind)> Tokenize(ReadOnlySpan<char> span)
    {
        var tokens = new List<(string, bool, int, RawTokenKind)>();
        int i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && !char.IsLetterOrDigit(span[i]) && span[i] != '*')
                i++;
            if (i >= span.Length) break;

            int start = i;
            while (i < span.Length && char.IsLetterOrDigit(span[i]))
                i++;
            int wordEnd = i;

            bool isPrefix = i < span.Length && span[i] == '*';
            if (isPrefix) i++;

            int fuzzyDist = 0;
            if (!isPrefix && i < span.Length && span[i] == '~')
            {
                i++;
                if (i < span.Length && char.IsAsciiDigit(span[i]))
                {
                    fuzzyDist = span[i] - '0';
                    i++;
                }
                else
                {
                    fuzzyDist = 1;
                }
                if (fuzzyDist > 2) fuzzyDist = 2;
            }

            var word = span[start..wordEnd];
            if (word.IsEmpty) continue;

            string wordStr = word.ToString();
            if (!isPrefix && fuzzyDist == 0)
            {
                if (wordStr is "AND") { tokens.Add(("", false, 0, RawTokenKind.And)); continue; }
                if (wordStr is "OR") { tokens.Add(("", false, 0, RawTokenKind.Or)); continue; }
                if (wordStr is "NOT") { tokens.Add(("", false, 0, RawTokenKind.Not)); continue; }
            }

            tokens.Add((wordStr, isPrefix, fuzzyDist, RawTokenKind.Term));
        }
        return tokens;
    }

    private sealed class SingleTokenSink : ITokenSink
    {
        public string? Token { get; private set; }
        public void Accept(ReadOnlySpan<char> token) => Token ??= token.ToString();
    }

    private sealed class CollectingSink(HashSet<string> target) : ITokenSink
    {
        public void Accept(ReadOnlySpan<char> token) => target.Add(token.ToString());
    }
}

/// <summary>Boolean 全文検索クエリにおける clause のモード。</summary>
internal enum FtsClauseMode
{
    /// <summary>ドキュメントはこの clause のタームを 1 つ以上含む必要がある (AND)。</summary>
    Required,
    /// <summary>ドキュメントはこの clause のタームを含んでもよい — スコアリングに寄与する (OR / 既定)。</summary>
    Optional,
    /// <summary>ドキュメントはこの clause のタームを含んではならない (NOT)。</summary>
    Excluded,
}

/// <summary>
/// パース済み Boolean 全文検索クエリの単一 clause。展開済みターム集合と
/// clause モード (required/optional/excluded) を保持する。
/// </summary>
internal readonly struct FtsClause(HashSet<string> terms, FtsClauseMode mode)
{
    public HashSet<string> Terms { get; } = terms;
    public FtsClauseMode Mode { get; } = mode;

    internal FtsClause WithMode(FtsClauseMode newMode) => new(Terms, newMode);
}

/// <summary>
/// Boolean 全文検索クエリのパース結果。Required/Optional/Excluded モードで
/// グルーピングされた clause を持つ。各 clause 内のタームは全て展開済み
/// (prefix ワイルドカード・fuzzy タームは索引に対して解決済み)。
/// </summary>
internal readonly struct ParsedFtsQuery(IReadOnlyList<FtsClause> clauses)
{
    public IReadOnlyList<FtsClause> Clauses { get; } = clauses;

    /// <summary>BM25 スコアリング用の全 positive ターム (Required + Optional) の和集合。</summary>
    public HashSet<string> AllPositiveTerms()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Clauses)
            if (c.Mode != FtsClauseMode.Excluded)
                foreach (var t in c.Terms) result.Add(t);
        return result;
    }
}
