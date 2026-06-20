namespace Quiver.Rag;

/// <summary>
/// <see cref="Chunker"/> のチャンク分割設定。目標サイズ・オーバーラップ・(任意の) 上限を持つ。
/// </summary>
public sealed record ChunkingOptions
{
    /// <summary>
    /// チャンクの目標文字数。小ブロックはこのサイズまでパックし、
    /// これを超える単一段落はこのサイズの窓で分割する。既定 800。
    /// </summary>
    public int TargetSize { get; init; } = 800;

    /// <summary>
    /// 段落分割時に連続チャンク間で重ねる文字数。
    /// 前チャンクの末尾と次チャンクの先頭がこの数だけ重複する。
    /// 既定 100。<see cref="TargetSize"/> 未満であること。
    /// </summary>
    public int Overlap { get; init; } = 100;

    /// <summary>
    /// 安全弁としての絶対上限文字数。既定 0 
    /// (無効 = <see cref="BlockKind.Table"/> / <see cref="BlockKind.Code"/> は決して分割しない)。
    /// 0 より大きい値を設定すると、その長さを超える Table / Code ブロックもオーバーラップ無しで強制分割する。
    /// </summary>
    public int MaxChunkSize { get; init; } = 0;

    /// <summary>設定値の整合性を検証する。不正なら例外を投げる。</summary>
    internal void Validate()
    {
        if (TargetSize < 1)
            throw new ArgumentOutOfRangeException(nameof(TargetSize), TargetSize, "TargetSize は 1 以上である必要があります。");
        if (Overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(Overlap), Overlap, "Overlap は 0 以上である必要があります。");
        if (Overlap >= TargetSize)
            throw new ArgumentException($"Overlap ({Overlap}) は TargetSize ({TargetSize}) 未満である必要があります。", nameof(Overlap));
        if (MaxChunkSize < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxChunkSize), MaxChunkSize, "MaxChunkSize は 0 以上である必要があります。");
    }
}
