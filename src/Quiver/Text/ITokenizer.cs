namespace Quiver.Text;

/// <summary>
/// Splits text into the index/query terms used by the full-text index. The
/// <see cref="TokenizerId"/> is recorded in the index catalog so query-time
/// tokenization is guaranteed to match the tokenizer used at index-build time
/// (mismatched tokenizers would silently miss hits).
/// </summary>
public interface ITokenizer
{
    /// <summary>Stable identifier persisted in the catalog, e.g. <c>"mixed-bigram-v1"</c>.</summary>
    string TokenizerId { get; }

    /// <summary>
    /// Normalize and tokenize <paramref name="text"/>, invoking
    /// <paramref name="sink"/> once per emitted term. Tokens are surfaced as
    /// spans over an internal buffer; the sink must copy any token it intends
    /// to keep past the call. Order of emission is left-to-right.
    /// </summary>
    void Tokenize(ReadOnlySpan<char> text, ITokenSink sink);
}

/// <summary>
/// Allocation-free callback target for <see cref="ITokenizer.Tokenize"/>. The
/// span handed to <see cref="Accept"/> is only valid for the duration of the
/// call; do not store it.
/// </summary>
public interface ITokenSink
{
    /// <summary>Receive one emitted term.</summary>
    void Accept(ReadOnlySpan<char> token);
}
