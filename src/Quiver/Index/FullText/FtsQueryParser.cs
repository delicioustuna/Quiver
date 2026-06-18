using System.Text;
using Quiver.Text;

namespace Quiver.Index.FullText;

/// <summary>
/// Pre-processes a full-text query string, splitting it into exact terms and
/// prefix terms (trailing <c>*</c>), with support for Boolean operators
/// (<c>AND</c>, <c>OR</c>, <c>NOT</c>). Prefix terms are normalized through the
/// index's tokenizer so that <c>"Quiv*"</c> matches indexed terms starting
/// with <c>"quiv"</c>. The expanded term set is then fed to the BM25 scorer.
/// </summary>
internal static class FtsQueryParser
{
    /// <summary>
    /// Parse <paramref name="queryText"/> and resolve all terms (expanding prefixes
    /// against the <paramref name="index"/>). Returns the complete set of terms to
    /// score. Each prefix expands to zero or more indexed terms; exact segments are
    /// tokenized normally.
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

            bool isPrefix = i < span.Length && span[i] == '*';
            if (isPrefix) i++;

            var word = span[start..(isPrefix ? i - 1 : i)];
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
            else
            {
                tokenizer.Tokenize(word, new CollectingSink(result));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="queryText"/> contains at least one
    /// prefix wildcard (<c>*</c>), so callers can fast-path the common exact-only case.
    /// </summary>
    public static bool ContainsWildcard(string queryText)
        => queryText.Contains('*');

    /// <summary>
    /// Returns <c>true</c> when <paramref name="queryText"/> contains at least one
    /// Boolean operator (<c>AND</c>, <c>OR</c>, <c>NOT</c> — uppercase only, Lucene convention).
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
    /// Parse a Boolean query (<c>AND</c>/<c>OR</c>/<c>NOT</c>) and expand all terms
    /// (including prefix wildcards) against the index. Returns a structured result
    /// with clause-level Required/Optional/Excluded grouping.
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
            var (text, isPrefix, kind) = rawTokens[t];

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

            var terms = ExpandSingleWord(text, isPrefix, tokenizer, index);
            clauses.Add(new FtsClause(terms, nextMode));
            lastTermIdx = clauses.Count - 1;
            nextMode = FtsClauseMode.Optional;
        }

        return new ParsedFtsQuery(clauses);
    }

    private static HashSet<string> ExpandSingleWord(
        string word, bool isPrefix, ITokenizer tokenizer, FullTextIndex index)
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
        else
        {
            tokenizer.Tokenize(word, new CollectingSink(result));
        }
        return result;
    }

    private enum RawTokenKind { Term, And, Or, Not }

    private static List<(string Text, bool IsPrefix, RawTokenKind Kind)> Tokenize(ReadOnlySpan<char> span)
    {
        var tokens = new List<(string, bool, RawTokenKind)>();
        int i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && !char.IsLetterOrDigit(span[i]) && span[i] != '*')
                i++;
            if (i >= span.Length) break;

            int start = i;
            while (i < span.Length && char.IsLetterOrDigit(span[i]))
                i++;

            bool isPrefix = i < span.Length && span[i] == '*';
            if (isPrefix) i++;

            var word = span[start..(isPrefix ? i - 1 : i)];
            if (word.IsEmpty) continue;

            string wordStr = word.ToString();
            if (!isPrefix)
            {
                if (wordStr is "AND") { tokens.Add(("", false, RawTokenKind.And)); continue; }
                if (wordStr is "OR") { tokens.Add(("", false, RawTokenKind.Or)); continue; }
                if (wordStr is "NOT") { tokens.Add(("", false, RawTokenKind.Not)); continue; }
            }

            tokens.Add((wordStr, isPrefix, RawTokenKind.Term));
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

/// <summary>Mode for a single clause in a Boolean FTS query.</summary>
internal enum FtsClauseMode
{
    /// <summary>Document MUST contain at least one term from this clause (AND).</summary>
    Required,
    /// <summary>Document MAY contain terms from this clause — they contribute to scoring (OR / default).</summary>
    Optional,
    /// <summary>Document MUST NOT contain any term from this clause (NOT).</summary>
    Excluded,
}

/// <summary>
/// A single clause in a parsed Boolean FTS query. Holds the expanded term set
/// and the clause mode (required/optional/excluded).
/// </summary>
internal readonly struct FtsClause(HashSet<string> terms, FtsClauseMode mode)
{
    public HashSet<string> Terms { get; } = terms;
    public FtsClauseMode Mode { get; } = mode;

    internal FtsClause WithMode(FtsClauseMode newMode) => new(Terms, newMode);
}

/// <summary>
/// Result of parsing a Boolean FTS query. Contains clauses grouped by
/// Required/Optional/Excluded mode. All terms within each clause are
/// already expanded (prefix wildcards resolved against the index).
/// </summary>
internal readonly struct ParsedFtsQuery(IReadOnlyList<FtsClause> clauses)
{
    public IReadOnlyList<FtsClause> Clauses { get; } = clauses;

    /// <summary>Union of all positive (Required + Optional) terms for BM25 scoring.</summary>
    public HashSet<string> AllPositiveTerms()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Clauses)
            if (c.Mode != FtsClauseMode.Excluded)
                foreach (var t in c.Terms) result.Add(t);
        return result;
    }
}
