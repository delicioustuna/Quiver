using System.Globalization;
using System.Text;

namespace Yatagarasu.Text;

/// <summary>
/// 日本語主体コーパス向けの既定ノーマライザ。NFKC + ASCII 小文字化 + 制御文字除去 + 空白折り畳み。
/// </summary>
/// <remarks>
/// NFKC は半角カタカナ、全角数字、㈱ 等を正準合成に変換する。埋め込み元テキストと
/// 全文索引の両方で適切なトレードオフとなる。<see cref="DefaultFlags"/> で変換セットを上書き可。
/// </remarks>
public sealed class JapaneseAwareNormalizer : ITextNormalizer
{
    /// <summary>このノーマライザが適用する変換セット。</summary>
    public NormalizationFlags DefaultFlags { get; init; } =
        NormalizationFlags.UnicodeNFKC
        | NormalizationFlags.LowerCaseAscii
        | NormalizationFlags.StripControlChars
        | NormalizationFlags.CollapseWhitespace;

    /// <inheritdoc/>
    public NormalizedText Normalize(ReadOnlySpan<char> input)
        => NormalizerCore.Apply(input, DefaultFlags);
}

/// <summary>
/// 低損失ノーマライザ。NFC + 制御文字除去のみ。
/// 後段が独自のケースフォールディングを行う場合など、元の形を保持したいときに使う。
/// </summary>
public sealed class MinimalNormalizer : ITextNormalizer
{
    /// <summary>このノーマライザが適用する変換セット。</summary>
    public NormalizationFlags DefaultFlags { get; init; } =
        NormalizationFlags.UnicodeNFC | NormalizationFlags.StripControlChars;

    /// <inheritdoc/>
    public NormalizedText Normalize(ReadOnlySpan<char> input)
        => NormalizerCore.Apply(input, DefaultFlags);
}

internal static class NormalizerCore
{
    public static NormalizedText Apply(ReadOnlySpan<char> input, NormalizationFlags flags)
    {
        int originalLen = input.Length;
        string text = input.ToString();

        if ((flags & NormalizationFlags.UnicodeNFKC) != 0)
            text = text.Normalize(NormalizationForm.FormKC);
        else if ((flags & NormalizationFlags.UnicodeNFC) != 0)
            text = text.Normalize(NormalizationForm.FormC);

        if ((flags & NormalizationFlags.LowerCaseAscii) != 0)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
                sb.Append((c >= 'A' && c <= 'Z') ? (char)(c + 32) : c);
            text = sb.ToString();
        }

        if ((flags & NormalizationFlags.StripControlChars) != 0)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (!char.IsControl(c) || c == '\n' || c == '\t')
                    sb.Append(c);
            }
            text = sb.ToString();
        }

        if ((flags & NormalizationFlags.CollapseWhitespace) != 0)
        {
            var sb = new StringBuilder(text.Length);
            bool prevWs = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!prevWs) sb.Append(' ');
                    prevWs = true;
                }
                else
                {
                    sb.Append(c);
                    prevWs = false;
                }
            }
            text = sb.ToString().Trim();
        }

        if ((flags & NormalizationFlags.NormalizeDashes) != 0)
        {
            text = text
                .Replace('‐', '-')
                .Replace('‑', '-')
                .Replace('‒', '-')
                .Replace('–', '-')
                .Replace('—', '-')
                .Replace('―', '-');
        }

        bool isLossy = (flags & (NormalizationFlags.UnicodeNFKC
                                 | NormalizationFlags.LowerCaseAscii
                                 | NormalizationFlags.CollapseWhitespace
                                 | NormalizationFlags.NormalizeDashes)) != 0;

        return new NormalizedText
        {
            Text = text,
            Applied = flags,
            IsLossy = isLossy,
            OriginalLengthChars = originalLen,
            NormalizedLengthChars = text.Length,
        };
    }
}
