namespace Yatagarasu.Rag;

/// <summary>
/// チャンカー (<see cref="Chunker"/>) が出力する 1 チャンクのドラフト。永続化前の純粋な値で、
/// ストレージ ID を持たない。<see cref="CharStart"/>/<see cref="CharEnd"/> は文書ソーステキスト
/// (各ブロック本文を <see cref="Chunker.BlockSeparator"/> で連結したもの) 上の半開区間
/// [start, end) オフセットで、<see cref="Text"/> はその区間のスライスと一致する。
/// </summary>
/// <param name="Text">チャンク本文。</param>
/// <param name="Ordinal">文書内のチャンク順序 (0 起点、出力順に連番)。</param>
/// <param name="HeadingPath">見出しパス ("章 &gt; 節" 形式)。見出しが無ければ空文字列。</param>
/// <param name="Page">由来ブロックのページ番号 (チャンク内で最初に <c>Page</c> を持つブロックのもの。無ければ <c>null</c>)。</param>
/// <param name="CharStart">ソーステキスト上の開始オフセット (含む)。</param>
/// <param name="CharEnd">ソーステキスト上の終了オフセット (含まない)。</param>
public sealed record ChunkDraft(
    string Text,
    int Ordinal,
    string HeadingPath,
    int? Page,
    int CharStart,
    int CharEnd);
