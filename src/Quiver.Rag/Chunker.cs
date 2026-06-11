using System.Text;

namespace Quiver.Rag;

/// <summary>
/// 正規化ブロック列をチャンクへ分割する純粋ロジック (ストレージ非依存)。ブロック境界と見出しを
/// 尊重し、目標サイズまで小ブロックをパックし、超過する段落はオーバーラップ付きで分割する。
/// <see cref="BlockKind.Heading"/> は headingPath を更新するのみで本文チャンクには出力しない。
/// <see cref="BlockKind.Table"/> / <see cref="BlockKind.Code"/> は (既定では) 分割しない。
/// </summary>
/// <remarks>
/// 計算量は文書長 N に対し O(N)。スレッドセーフ (状態は呼び出しローカル)。
/// </remarks>
public static class Chunker
{
    /// <summary>
    /// パック時にブロック本文の間へ挿入する区切り。ソーステキスト構築の基準でもあり、
    /// チャンクの <see cref="ChunkDraft.CharStart"/>/<see cref="ChunkDraft.CharEnd"/> はこの区切りを
    /// 含めて連結したソーステキスト上のオフセットになる。
    /// </summary>
    public const string BlockSeparator = "\n\n";

    /// <summary>
    /// <paramref name="blocks"/> を読み順のままチャンク列へ分割する。
    /// </summary>
    /// <param name="blocks">読み順に並んだ正規化ブロック列。</param>
    /// <param name="options">分割設定。<c>null</c> なら既定値。</param>
    /// <returns>出力順 (= <see cref="ChunkDraft.Ordinal"/> 昇順) のチャンクドラフト列。</returns>
    public static IReadOnlyList<ChunkDraft> Chunk(
        IReadOnlyList<IngestedBlock> blocks, ChunkingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        options ??= new ChunkingOptions();
        options.Validate();

        // 1) ソーステキストとブロック span を構築する (区切りは span の外側に置く)。
        var sb = new StringBuilder();
        var spans = new (int Start, int End)[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0) sb.Append(BlockSeparator);
            int start = sb.Length;
            sb.Append(blocks[i].Text ?? string.Empty);
            spans[i] = (start, sb.Length);
        }
        string source = sb.ToString();

        var result = new List<ChunkDraft>();
        var headingStack = new List<(int Level, string Text)>();
        int ordinal = 0;

        // 連続する小ブロックを 1 チャンクへ詰めるパックバッファ。bufLo < 0 は空を表す。
        int bufLo = -1, bufHi = -1;
        int? bufPage = null;
        string bufHeading = string.Empty;

        string CurrentHeading() => string.Join(
            " > ", headingStack.Where(h => !string.IsNullOrEmpty(h.Text)).Select(h => h.Text));

        void Emit(int lo, int hi, string heading, int? page)
        {
            if (hi <= lo) return; // 空チャンクは出力しない
            result.Add(new ChunkDraft(source.Substring(lo, hi - lo), ordinal++, heading, page, lo, hi));
        }

        void FlushBuffer()
        {
            if (bufLo >= 0) Emit(bufLo, bufHi, bufHeading, bufPage);
            bufLo = -1;
        }

        // 分割境界 b がサロゲートペアを割らないよう手前へ丸める (low surrogate の直前は high surrogate)。
        // ブロック端 (b == e) や非サロゲート位置はそのまま返す。
        int AvoidSplit(int b)
            => (b > 0 && b < source.Length && char.IsLowSurrogate(source[b])) ? b - 1 : b;

        // [s, e) を window=TargetSize / overlap=Overlap で分割して出力する。
        void SplitRange(int s, int e, string heading, int? page)
        {
            int window = options.TargetSize;
            int overlap = options.Overlap;
            int p = s;
            while (true)
            {
                int hi = Math.Min(p + window, e);
                int rounded = AvoidSplit(hi);
                if (rounded > p) hi = rounded; // window が 1 でペアを収容できない病的ケースは丸めない
                Emit(p, hi, heading, page);
                if (hi >= e) break;
                int next = AvoidSplit(hi - overlap);
                p = next > p ? next : hi; // overlap >= window への安全弁 (通常 Validate で排除)
            }
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var (s, e) = spans[i];
            int len = e - s;

            switch (block.Kind)
            {
                case BlockKind.Heading:
                    // headingPath を更新するのみ (本文には出さない)。見出し変更はチャンク境界。
                    FlushBuffer();
                    int level = block.HeadingLevel ?? 1;
                    headingStack.RemoveAll(h => h.Level >= level);
                    headingStack.Add((level, block.Text ?? string.Empty));
                    break;

                case BlockKind.Table:
                case BlockKind.Code:
                    // 分割しない。既定では超過しても単独チャンク。MaxChunkSize>0 のときのみ安全弁分割。
                    FlushBuffer();
                    if (options.MaxChunkSize > 0 && len > options.MaxChunkSize)
                    {
                        int p = s;
                        while (true)
                        {
                            int hi = Math.Min(p + options.MaxChunkSize, e);
                            int rounded = AvoidSplit(hi);
                            if (rounded > p) hi = rounded;
                            Emit(p, hi, CurrentHeading(), block.Page);
                            if (hi >= e) break;
                            p = hi;
                        }
                    }
                    else
                    {
                        Emit(s, e, CurrentHeading(), block.Page);
                    }
                    break;

                default: // Paragraph
                    if (len == 0) break; // 空段落はスキップ
                    if (len > options.TargetSize)
                    {
                        FlushBuffer();
                        SplitRange(s, e, CurrentHeading(), block.Page);
                    }
                    else if (bufLo < 0)
                    {
                        bufLo = s; bufHi = e; bufPage = block.Page; bufHeading = CurrentHeading();
                    }
                    else if (e - bufLo <= options.TargetSize)
                    {
                        // 区切り込みでも target 以内なら同一バッファへパック。
                        bufHi = e;
                        bufPage ??= block.Page;
                    }
                    else
                    {
                        FlushBuffer();
                        bufLo = s; bufHi = e; bufPage = block.Page; bufHeading = CurrentHeading();
                    }
                    break;
            }
        }

        FlushBuffer();
        return result;
    }
}
