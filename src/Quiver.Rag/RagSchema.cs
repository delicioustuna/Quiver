namespace Quiver.Rag;

/// <summary>
/// Quiver.Rag が使うグラフスキーマの正準名 (ラベル / 関係型 / プロパティキー / 索引名)。
/// RAG スキーマ表に対応する。利用側がVertexを直接問い合わせる際にも参照できるよう公開する。
/// </summary>
public static class RagSchema
{
    // ── ラベル ──
    /// <summary>文書Vertexのラベル。</summary>
    public const string DocumentLabel = "Document";
    /// <summary>チャンクVertexのラベル。</summary>
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
    /// <summary>
    /// チャンク本文 (原文スライス)。<c>charStart</c>/<c>charEnd</c> はこの値上の半開区間で、
    /// graph expansion の連結元になる。全文索引の対象は見出し語を含む <see cref="PropSearchText"/> の方。
    /// </summary>
    public const string PropText = "text";
    /// <summary>
    /// 全文索引 (BM25) の対象テキスト。見出しパスを本文の前に連結したもの (見出しが空なら本文と同一)。
    /// 見出し語を BM25 で引けるようにしつつ、<see cref="PropText"/> のスライス不変条件を保つために別持ちする
    /// (本文を二重に持つストレージコストとのトレードオフ)。
    /// </summary>
    public const string PropSearchText = "searchText";
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
    /// <summary>Chunk.text 全文索引名。</summary>
    public const string ChunkTextIndex = "idx_rag_chunk_text";
}
