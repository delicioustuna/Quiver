namespace Quiver.Text;

/// <summary>
/// テキストを全文索引の索引 / 検索用ターム列に分割する。
/// </summary>
/// <remarks>
/// <see cref="TokenizerId"/> がインデックスカタログに永続化されるため、
/// 検索時のトークン化がインデックス構築時と一致することが保証される
/// (不一致があるとヒットがサイレントに失われる)。
/// </remarks>
public interface ITokenizer
{
    /// <summary>カタログに永続化される安定識別子 (例: <c>"mixed-bigram-v1"</c>)。</summary>
    string TokenizerId { get; }

    /// <summary>
    /// <paramref name="text"/> を正規化・トークン化し、ターム毎に <paramref name="sink"/> を呼び出す。
    /// </summary>
    /// <remarks>
    /// トークンは内部バッファ上のスパンとして渡される。呼び出しをまたいで保持する場合は
    /// シンク側でコピーすること。放出順序は左から右。
    /// </remarks>
    void Tokenize(ReadOnlySpan<char> text, ITokenSink sink);
}

/// <summary>
/// <see cref="ITokenizer.Tokenize"/> の割り当てフリーなコールバック先。
/// <see cref="Accept"/> に渡されるスパンは呼び出し中のみ有効。
/// </summary>
public interface ITokenSink
{
    /// <summary>放出された 1 タームを受け取る。</summary>
    void Accept(ReadOnlySpan<char> token);
}
