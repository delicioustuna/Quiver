namespace Quiver.Embedding.Text;

/// <summary>
/// Pre-embedding text normalization. The result drives both the bytes sent to
/// the provider and the SHA-256 content hash used for idempotency, so two
/// inputs that normalize to the same string deduplicate naturally.
/// </summary>
public interface ITextNormalizer
{
    NormalizedText Normalize(ReadOnlySpan<char> input);
}

public sealed class NormalizedText
{
    public required string Text { get; init; }
    public required NormalizationFlags Applied { get; init; }

    /// <summary>True when at least one applied flag is non-roundtrippable (NFKC, ASCII lowercase, etc.).</summary>
    public required bool IsLossy { get; init; }

    public required int OriginalLengthChars { get; init; }
    public required int NormalizedLengthChars { get; init; }
}

[Flags]
public enum NormalizationFlags
{
    None              = 0,
    UnicodeNFC        = 1 << 0,
    UnicodeNFKC       = 1 << 1,
    LowerCaseAscii    = 1 << 2,
    StripControlChars = 1 << 3,
    CollapseWhitespace = 1 << 4,
    NormalizeDashes   = 1 << 5,
}
