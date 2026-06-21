namespace Quiver.Text;

/// <summary>
/// トークナイザとシンクの間でトークンを変換・除去・展開するフィルタ。
/// チェーンとして合成され、上流からのトークンを受け取り 0 個以上のトークンを下流へ転送する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>素通し</b>: <c>downstream.Accept(token)</c> をそのまま呼ぶ。</item>
/// <item><b>除去</b>: <c>downstream</c> を呼ばない (例: ストップワード除去)。</item>
/// <item><b>変換</b>: トークンを加工してから <c>downstream.Accept(modified)</c> を呼ぶ。</item>
/// <item><b>展開</b>: <c>downstream.Accept()</c> を複数回呼ぶ (例: 同義語注入)。</item>
/// </list>
/// </remarks>
public interface ITokenFilter
{
    /// <summary>カタログ永続化用の安定識別子 (例: <c>"lowercase-v1"</c>)。</summary>
    string FilterId { get; }

    /// <summary>
    /// <paramref name="token"/> を処理し、結果を <paramref name="downstream"/> へ転送する。
    /// スパンは呼び出し中のみ有効。
    /// </summary>
    void Apply(ReadOnlySpan<char> token, ITokenSink downstream);
}
