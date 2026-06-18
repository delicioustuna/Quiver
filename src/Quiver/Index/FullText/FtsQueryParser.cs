using System.Text;
using Quiver.Text;

namespace Quiver.Index.FullText;

/// <summary>
/// Pre-processes a full-text query string, splitting it into exact terms and
/// prefix terms (trailing <c>*</c>). Prefix terms are normalized through the
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
