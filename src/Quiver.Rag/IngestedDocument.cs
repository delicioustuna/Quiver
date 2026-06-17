namespace Quiver.Rag;

/// <summary>
/// 取込側 (PdfTools 等) が生成する正規化済みドキュメント。<see cref="Blocks"/> は
/// 正しい読み順で並んでいることが契約 (レイアウト解析・読み順復元は取込側の責務)。
/// <see cref="RagStore.UpsertDocumentAsync"/> がチャンキング → 格納 → 索引付けを行う。
/// </summary>
/// <param name="SourceId">文書を一意に識別するキー (ファイルパス・URL 等)。再取込の同一性判定に使う。</param>
/// <param name="Title">表示用タイトル。</param>
/// <param name="Metadata">任意のメタデータ。MVP では <c>metadataJson</c> 文字列プロパティに直列化される。</param>
/// <param name="Blocks">読み順に並んだ正規化ブロック列。</param>
public sealed record IngestedDocument(
    string SourceId,
    string Title,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<IngestedBlock> Blocks);

/// <summary>
/// 取込側が読み順復元・正規化まで済ませた 1 ブロック。チャンカーはこの境界を尊重して
/// チャンク化する (ブロック跨ぎ分割を避ける)。
/// </summary>
/// <param name="Kind">ブロック種別。<see cref="BlockKind.Heading"/> のとき <see cref="HeadingLevel"/> が見出しレベル。</param>
/// <param name="Text">ブロック本文 (正規化済み)。</param>
/// <param name="HeadingLevel"><see cref="Kind"/> が <see cref="BlockKind.Heading"/> のときの見出しレベル (1 起点)。</param>
/// <param name="Page">元文書での 1 起点ページ番号 (任意)。</param>
public sealed record IngestedBlock(
    BlockKind Kind,
    string Text,
    int? HeadingLevel = null,
    int? Page = null);

/// <summary>正規化ブロックの種別。チャンカーの分割規則 (Table / Code は分割しない 等) を分ける。</summary>
public enum BlockKind
{
    /// <summary>本文段落。</summary>
    Paragraph,
    /// <summary>見出し。<see cref="IngestedBlock.HeadingLevel"/> で階層を表し headingPath を更新する。</summary>
    Heading,
    /// <summary>表。分割せず単独チャンク化する。</summary>
    Table,
    /// <summary>コードブロック。分割せず単独チャンク化する。</summary>
    Code,
}
