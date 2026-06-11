namespace Quiver.Rag;

/// <summary>
/// Quiver.Rag が使うグラフスキーマの正準名 (ラベル / 関係型 / プロパティキー / 索引名)。
/// 14_rag_layer.md §3 のスキーマ表に対応する。利用側がノードを直接問い合わせる際にも参照できるよう公開する。
/// </summary>
public static class RagSchema
{
    // ── ラベル ──
    /// <summary>文書ノードのラベル。</summary>
    public const string DocumentLabel = "Document";
    /// <summary>チャンクノードのラベル。</summary>
    public const string ChunkLabel = "Chunk";

    // ── 関係型 ──
    /// <summary>Document → Chunk。文書が保持するチャンク。</summary>
    public const string HasChunkType = "HAS_CHUNK";
    /// <summary>Chunk → Chunk。文書内のチャンク順序連結 (graph expansion の主経路)。</summary>
    public const string NextChunkType = "NEXT_CHUNK";

    // ── Document プロパティキー ──
    /// <summary>文書の一意キー (検索索引対象。一意性は upsert 側で担保)。</summary>
    public const string PropSourceId = "sourceId";
    /// <summary>文書タイトル。</summary>
    public const string PropTitle = "title";
    /// <summary>Blocks の正規化ハッシュ。再取込時の no-op 判定に使う。</summary>
    public const string PropContentHash = "contentHash";
    /// <summary>取込時刻 (UTC ticks)。</summary>
    public const string PropIngestedAt = "ingestedAt";
    /// <summary>メタデータの JSON 直列化 (MVP)。</summary>
    public const string PropMetadataJson = "metadataJson";

    // ── Chunk プロパティキー ──
    /// <summary>チャンク本文 (全文索引対象)。ベクトル索引の埋め込み元でもある。</summary>
    public const string PropText = "text";
    /// <summary>文書内のチャンク順序 (0 起点)。</summary>
    public const string PropOrdinal = "ordinal";
    /// <summary>見出しパス ("1.2 概要 &gt; 1.2.1 背景" 形式)。</summary>
    public const string PropHeadingPath = "headingPath";
    /// <summary>元文書でのページ番号 (任意)。</summary>
    public const string PropPage = "page";
    /// <summary>原文中の文字開始オフセット。</summary>
    public const string PropCharStart = "charStart";
    /// <summary>原文中の文字終了オフセット。</summary>
    public const string PropCharEnd = "charEnd";

    // ── 索引名 ──
    /// <summary>Document.sourceId の検索索引 (StringEquality)。一意性は upsert 側で担保。</summary>
    public const string DocSourceIndex = "idx_rag_doc_source";
    /// <summary>Chunk 埋め込みベクトル索引の既定名 (<see cref="RagStoreOptions.VectorIndexName"/> で上書き可)。</summary>
    public const string ChunkVectorIndex = "rag_chunk_embedding";
    /// <summary>Chunk.text 全文索引名 (FTS-2)。</summary>
    public const string ChunkTextIndex = "idx_rag_chunk_text";
}
