namespace Quiver.Text;

/// <summary>
/// Text normalization applied before tokenization / embedding. Living in the
/// core <c>Quiver.Text</c> namespace so both the full-text index pipeline and
/// the embedding pipeline share one normalization contract: two inputs that
/// normalize to the same string tokenize and hash identically.
/// </summary>
public interface ITextNormalizer
{
    /// <summary>Normalize <paramref name="input"/> and report which flags were applied.</summary>
    NormalizedText Normalize(ReadOnlySpan<char> input);
}

/// <summary>Result of <see cref="ITextNormalizer.Normalize"/>: the normalized string plus metadata.</summary>
public sealed class NormalizedText
{
    /// <summary>The normalized text.</summary>
    public required string Text { get; init; }

    /// <summary>The set of transformations actually applied.</summary>
    public required NormalizationFlags Applied { get; init; }

    /// <summary>True when at least one applied flag is non-roundtrippable (NFKC, ASCII lowercase, etc.).</summary>
    public required bool IsLossy { get; init; }

    /// <summary>Length of the original input in UTF-16 code units.</summary>
    public required int OriginalLengthChars { get; init; }

    /// <summary>Length of the normalized text in UTF-16 code units.</summary>
    public required int NormalizedLengthChars { get; init; }
}

/// <summary>Individual normalization transformations, combinable as a bit set.</summary>
[Flags]
public enum NormalizationFlags
{
    /// <summary>No transformation.</summary>
    None              = 0,
    /// <summary>Unicode NFC composition.</summary>
    UnicodeNFC        = 1 << 0,
    /// <summary>Unicode NFKC compatibility composition (half-width katakana, full-width digits, etc.).</summary>
    UnicodeNFKC       = 1 << 1,
    /// <summary>Lower-case ASCII A–Z only.</summary>
    LowerCaseAscii    = 1 << 2,
    /// <summary>Strip control characters (keeping newline and tab).</summary>
    StripControlChars = 1 << 3,
    /// <summary>Collapse runs of whitespace to a single space and trim.</summary>
    CollapseWhitespace = 1 << 4,
    /// <summary>Fold the various Unicode dash characters to ASCII '-'.</summary>
    NormalizeDashes   = 1 << 5,
}
