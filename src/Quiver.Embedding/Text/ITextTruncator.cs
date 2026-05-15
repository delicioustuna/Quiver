using System.Globalization;
using System.Text;
using Quiver.Embedding.Providers;

namespace Quiver.Embedding.Text;

/// <summary>
/// Cap text so the provider's input limit isn't exceeded. The caller picks
/// the counting mode (matching <c>EmbeddingInputLimits</c>) and the policy
/// (which end gets sacrificed).
/// </summary>
public interface ITextTruncator
{
    string Truncate(string input, int maxLength, LengthCountingMode mode, TruncationPolicy policy);
}

/// <summary>
/// Grapheme-cluster-aware default truncator. Uses <see cref="StringInfo"/>'s
/// UAX #29 enumerator so ZWJ sequences (👨‍👩‍👧‍👦), regional-indicator pairs
/// (🇯🇵), and skin-tone modifiers (👍🏽) stay intact.
/// </summary>
public sealed class GraphemeTextTruncator : ITextTruncator
{
    public string Truncate(string input, int maxLength, LengthCountingMode mode, TruncationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (maxLength <= 0) return string.Empty;
        int current = Measure(input, mode);
        if (current <= maxLength) return input;

        return policy switch
        {
            TruncationPolicy.Tail => TruncateTail(input, maxLength, mode),
            TruncationPolicy.Head => TruncateHead(input, maxLength, mode),
            TruncationPolicy.MiddleEllipsis => TruncateMiddle(input, maxLength, mode),
            TruncationPolicy.ThrowOnExceed =>
                throw new InvalidOperationException(
                    $"Text exceeds limit ({current} > {maxLength}, mode={mode}) and policy is ThrowOnExceed."),
            _ => TruncateTail(input, maxLength, mode),
        };
    }

    private static int Measure(string s, LengthCountingMode mode) => mode switch
    {
        LengthCountingMode.Utf8Bytes => Encoding.UTF8.GetByteCount(s),
        LengthCountingMode.Utf16CodeUnits => s.Length,
        LengthCountingMode.Runes => RuneCount(s),
        LengthCountingMode.Graphemes => GraphemeCount(s),
        // Tokens has no in-process estimator — fall back to UTF-8 byte count.
        _ => Encoding.UTF8.GetByteCount(s),
    };

    private static int RuneCount(string s)
    {
        int n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    private static int GraphemeCount(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        int n = 0;
        while (e.MoveNext()) n++;
        return n;
    }

    private static string TruncateTail(string input, int maxLength, LengthCountingMode mode)
    {
        // Iterate graphemes; stop when adding the next one would exceed maxLength.
        var e = StringInfo.GetTextElementEnumerator(input);
        var sb = new StringBuilder();
        while (e.MoveNext())
        {
            string g = (string)e.Current;
            string candidate = sb.ToString() + g;
            if (Measure(candidate, mode) > maxLength) break;
            sb.Append(g);
        }
        return sb.ToString();
    }

    private static string TruncateHead(string input, int maxLength, LengthCountingMode mode)
    {
        // Collect grapheme list, then walk from the end, prepending until the
        // accumulated string would exceed maxLength.
        var graphemes = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(input);
        while (e.MoveNext()) graphemes.Add((string)e.Current);

        var sb = new StringBuilder();
        for (int i = graphemes.Count - 1; i >= 0; i--)
        {
            string candidate = graphemes[i] + sb;
            if (Measure(candidate, mode) > maxLength) break;
            sb.Insert(0, graphemes[i]);
        }
        return sb.ToString();
    }

    private static string TruncateMiddle(string input, int maxLength, LengthCountingMode mode)
    {
        const string ellipsis = "...";
        int ellipsisLen = Measure(ellipsis, mode);
        if (ellipsisLen >= maxLength) return TruncateTail(input, maxLength, mode);

        int budget = maxLength - ellipsisLen;
        int half = budget / 2;
        string head = TruncateTail(input, half, mode);
        string tail = TruncateHead(input, budget - Measure(head, mode), mode);
        return head + ellipsis + tail;
    }
}
