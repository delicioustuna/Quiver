namespace Quiver.Text;

/// <summary>
/// Transforms, drops, or expands tokens between a tokenizer and a sink.
/// Filters are composed into a chain: each filter receives a token from the
/// upstream stage and forwards zero or more tokens to <paramref name="downstream"/>.
/// <list type="bullet">
/// <item><b>Pass through</b>: call <c>downstream.Accept(token)</c> unchanged.</item>
/// <item><b>Drop</b>: do not call <c>downstream</c> (e.g. stop-word removal).</item>
/// <item><b>Transform</b>: modify the token, then call <c>downstream.Accept(modified)</c>.</item>
/// <item><b>Expand</b>: call <c>downstream.Accept()</c> multiple times (e.g. synonym injection).</item>
/// </list>
/// </summary>
public interface ITokenFilter
{
    /// <summary>Stable identifier for catalog persistence, e.g. <c>"lowercase-v1"</c>.</summary>
    string FilterId { get; }

    /// <summary>
    /// Process <paramref name="token"/> and forward the result(s) to
    /// <paramref name="downstream"/>. The span is only valid for the
    /// duration of this call.
    /// </summary>
    void Apply(ReadOnlySpan<char> token, ITokenSink downstream);
}
