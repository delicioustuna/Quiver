using System.Text;

namespace Yatagarasu.Rag;

/// <summary>
/// 正規化ブロック列をチャンクへ分割する純粋ロジック (ストレージ非依存)。
/// ブロック境界と見出しを尊重し、目標サイズまで小ブロックをパックし、超過する段落はオーバーラップ付きで分割する。
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

        // 末尾 hi を (lo, hi] の範囲で語境界 (空白の直後) へ後退させ、語の途中で切るのを避ける。
        // 既に境界 (hi が空白の前後)、または窓内に空白が無い (語が窓より長い・CJK・記号列) ときは hi を保つ。
        // 空白が無いケースでは従来の char 窓と同一挙動に倒れる。
        int RoundEndToWord(int lo, int hi)
        {
            if (hi <= lo || hi >= source.Length) return hi;
            if (char.IsWhiteSpace(source[hi]) || char.IsWhiteSpace(source[hi - 1])) return hi;
            int w = hi - 1;
            while (w > lo && !char.IsWhiteSpace(source[w])) w--;
            return w > lo ? w + 1 : hi; // 空白の直後で切る (左チャンクは空白で終わる)。無ければ hi。
        }

        // 次チャンク開始 start を [start, limit) の最初の語頭 (空白の直後) へ前進させる。
        // オーバーラップを語単位にし、前進方向に丸めるので実オーバーラップは設定値以下に保たれる。
        // 既に語頭、または境界が無いときは start を保つ。
        int RoundStartToWord(int start, int limit)
        {
            if (start <= 0 || char.IsWhiteSpace(source[start - 1])) return start;
            int w = start;
            while (w < limit && !char.IsWhiteSpace(source[w])) w++;
            return w < limit ? w + 1 : start; // 空白の直後 = 次の語頭。無ければ start。
        }

        // 切り出し末尾 hardHi を「語境界 → サロゲート保護」の順で丸める。丸めて lo 以下へ潰れる
        // 病的ケース (窓 1 でペア収容不能等) は hardHi をそのまま使う。呼び出し側は hardHi < e を保証する。
        int RoundCut(int lo, int hardHi)
        {
            int hi = AvoidSplit(RoundEndToWord(lo, hardHi));
            return hi > lo ? hi : hardHi;
        }

        // [s, e) を window=TargetSize / overlap=Overlap で分割して出力する。語境界を尊重し、
        // 空白が無い区間では従来の char 窓に倒れる (CJK / 長大語 / 記号列はそのまま窓分割)。
        void SplitRange(int s, int e, string heading, int? page)
        {
            int window = options.TargetSize;
            int overlap = options.Overlap;
            int p = s;
            while (true)
            {
                int hardHi = Math.Min(p + window, e);
                int hi = hardHi < e ? RoundCut(p, hardHi) : hardHi;
                Emit(p, hi, heading, page);
                if (hi >= e) break;
                int rawNext = hi - overlap;
                int next = rawNext > p ? AvoidSplit(RoundStartToWord(rawNext, hi)) : hi;
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
                            int hardHi = Math.Min(p + options.MaxChunkSize, e);
                            int hi = hardHi < e ? RoundCut(p, hardHi) : hardHi; // 強制分割でも語境界尊重
                            Emit(p, hi, CurrentHeading(), block.Page);
                            if (hi >= e) break;
                            p = hi; // overlap 無し: 次は語境界で切った末尾から連続
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
