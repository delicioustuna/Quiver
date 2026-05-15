using System.Globalization;
using System.Text;

namespace Quiver.Embedding.Text;

/// <summary>
/// Default-on normalizer for Japanese-heavy corpora: NFKC + ASCII lowercase +
/// strip controls + collapse whitespace. NFKC turns half-width katakana,
/// full-width digits, ㈱ etc. into their canonical compositions which is
/// usually the right tradeoff for embedding-source text.
/// </summary>
public sealed class JapaneseAwareNormalizer : ITextNormalizer
{
    public NormalizationFlags DefaultFlags { get; init; } =
        NormalizationFlags.UnicodeNFKC
        | NormalizationFlags.LowerCaseAscii
        | NormalizationFlags.StripControlChars
        | NormalizationFlags.CollapseWhitespace;

    public NormalizedText Normalize(ReadOnlySpan<char> input)
        => NormalizerCore.Apply(input, DefaultFlags);
}

/// <summary>
/// Loss-averse normalizer: NFC + strip controls only. Use when downstream
/// consumers require the original form (e.g. provider that handles its own
/// case-folding) or for full-text indexing.
/// </summary>
public sealed class MinimalNormalizer : ITextNormalizer
{
    public NormalizationFlags DefaultFlags { get; init; } =
        NormalizationFlags.UnicodeNFC | NormalizationFlags.StripControlChars;

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
