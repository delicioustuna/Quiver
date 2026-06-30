using System.Globalization;
using System.Text;
using Quiver.Embedding.Providers;

namespace Quiver.Embedding.Text;

/// <summary>
/// プロバイダの入力上限を超えないようテキストを切り詰める。
/// 呼び出し側が <c>EmbeddingInputLimits</c> に対応する計数方法と、
/// どの部分を切り捨てるかを表すポリシーを指定する。
/// </summary>
public interface ITextTruncator
{
    string Truncate(string input, int maxLength, LengthCountingMode mode, TruncationPolicy policy);
}

/// <summary>
/// 書記素クラスタを考慮する既定の切り詰め実装。
/// <see cref="StringInfo"/> の UAX #29 列挙子を使い、ZWJ シーケンス (👨‍👩‍👧‍👦)、
/// 地域識別子の組 (🇯🇵)、肌色修飾子 (👍🏽) を分割しない。
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
        // Tokens はプロセス内で見積もれないため、UTF-8 バイト数へフォールバックする。
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
        // 書記素単位で走査し、次の要素を加えると maxLength を超える時点で止める。
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
        // 書記素を収集して末尾から走査し、maxLength を超えない範囲で前方へ追加する。
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
