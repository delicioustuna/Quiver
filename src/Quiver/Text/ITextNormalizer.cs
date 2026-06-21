namespace Quiver.Text;

/// <summary>
/// トークン化 / 埋め込みの前段に適用するテキスト正規化。
/// </summary>
/// <remarks>
/// 全文索引パイプラインと埋め込みパイプラインが同一の正規化契約を共有する。
/// 同じ文字列に正規化される 2 つの入力は同一のトークン列とハッシュを生成する。
/// </remarks>
public interface ITextNormalizer
{
    /// <summary><paramref name="input"/> を正規化し、適用されたフラグとともに返す。</summary>
    NormalizedText Normalize(ReadOnlySpan<char> input);
}

/// <summary><see cref="ITextNormalizer.Normalize"/> の結果。正規化済み文字列とメタデータを保持する。</summary>
public sealed class NormalizedText
{
    /// <summary>正規化済みテキスト。</summary>
    public required string Text { get; init; }

    /// <summary>実際に適用された変換のセット。</summary>
    public required NormalizationFlags Applied { get; init; }

    /// <summary>適用された変換に非可逆なもの (NFKC、ASCII 小文字化等) が含まれる場合に <c>true</c>。</summary>
    public required bool IsLossy { get; init; }

    /// <summary>元の入力の長さ (UTF-16 コード単位)。</summary>
    public required int OriginalLengthChars { get; init; }

    /// <summary>正規化後テキストの長さ (UTF-16 コード単位)。</summary>
    public required int NormalizedLengthChars { get; init; }
}

/// <summary>個々の正規化変換。ビットセットとして組み合わせ可能。</summary>
[Flags]
public enum NormalizationFlags
{
    /// <summary>変換なし。</summary>
    None              = 0,
    /// <summary>Unicode NFC 合成。</summary>
    UnicodeNFC        = 1 << 0,
    /// <summary>Unicode NFKC 互換合成 (半角カタカナ、全角数字等)。</summary>
    UnicodeNFKC       = 1 << 1,
    /// <summary>ASCII A–Z のみ小文字化。</summary>
    LowerCaseAscii    = 1 << 2,
    /// <summary>制御文字の除去 (改行・タブは保持)。</summary>
    StripControlChars = 1 << 3,
    /// <summary>連続空白の単一スペースへの折り畳みとトリム。</summary>
    CollapseWhitespace = 1 << 4,
    /// <summary>各種 Unicode ダッシュ文字の ASCII '-' への正規化。</summary>
    NormalizeDashes   = 1 << 5,
}
