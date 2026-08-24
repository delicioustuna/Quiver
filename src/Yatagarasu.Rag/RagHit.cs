namespace Yatagarasu.Rag;

/// <summary>
/// 検索 1 件の結果。隣接チャンクを連結した本文・見出しパス・親文書情報・順位・代表チャンク ID・score 内訳を持つ。
/// </summary>
/// <param name="ChunkText">隣接連結済みの本文 (オーバーラップは除去、隣接ヒットはマージ済)。</param>
/// <param name="HeadingPath">代表チャンク (最上位ヒット) の見出しパス。</param>
/// <param name="Document">親文書の参照 (<see cref="RagSearchOptions.IncludeDocument"/> が false のときは空)。</param>
/// <param name="Rank">融合ランキング上の順位 (1 起点。マージ時は最良順位を採用)。</param>
/// <param name="ChunkVertexId">代表チャンクの不透明 key。</param>
/// <param name="Score">代表チャンクのチャンネル別 score と融合結果。</param>
public sealed record RagHit(
    string ChunkText,
    string HeadingPath,
    RagDocumentRef Document,
    int Rank,
    VertexKey ChunkVertexId,
    RagScore Score);

/// <summary>RAG 検索結果の融合方式。</summary>
public enum RagFusionMethod
{
    /// <summary>BM25 だけを使用し、融合後 score は BM25 score と同じ。</summary>
    TextOnly,
    /// <summary>vector 検索だけを使用し、融合後 score は vector similarity と同じ。</summary>
    VectorOnly,
    /// <summary>BM25 と vector の順位を Reciprocal Rank Fusion で融合する。</summary>
    ReciprocalRankFusion,
}

/// <summary>検索結果の score 診断。</summary>
/// <param name="Bm25Score">BM25 が計算した score。text チャンネルを使わない場合は <see langword="null"/>。</param>
/// <param name="VectorSimilarity">vector index の距離尺度が計算した similarity。vector チャンネルを使わない場合は <see langword="null"/>。</param>
/// <param name="FusedScore">利用した融合方式の最終 score。</param>
/// <param name="FusionMethod">最終 score の融合方式。</param>
/// <param name="ReciprocalRankConstant">RRF の rank 定数。RRF 以外は 0。</param>
public sealed record RagScore(
    double? Bm25Score,
    float? VectorSimilarity,
    double FusedScore,
    RagFusionMethod FusionMethod,
    int ReciprocalRankConstant);

/// <summary>ヒットの親文書の最小参照。</summary>
/// <param name="SourceId">文書の一意キー。</param>
/// <param name="Title">文書タイトル。</param>
public sealed record RagDocumentRef(string SourceId, string Title);

/// <summary>
/// <see cref="RagSearchOptions.MetadataFilter"/> に渡される文書メタデータ。
/// <see cref="Metadata"/> は取込時の <c>metadataJson</c> を復元したもの。
/// </summary>
/// <param name="SourceId">文書の一意キー。</param>
/// <param name="Title">文書タイトル。</param>
/// <param name="Metadata">取込時に渡された任意メタデータ。</param>
public sealed record RagMetadata(
    string SourceId,
    string Title,
    IReadOnlyDictionary<string, string> Metadata);
