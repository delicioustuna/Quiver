using Quiver.Core;

namespace Quiver.Rag;

/// <summary>
/// 検索 1 件の結果。隣接チャンクを連結した本文・見出しパス・親文書情報・順位・代表チャンク ID を持つ。
/// </summary>
/// <param name="ChunkText">隣接連結済みの本文 (オーバーラップは除去、隣接ヒットはマージ済)。</param>
/// <param name="HeadingPath">代表チャンク (最上位ヒット) の見出しパス。</param>
/// <param name="Document">親文書の参照 (<see cref="RagSearchOptions.IncludeDocument"/> が false のときは空)。</param>
/// <param name="Rank">融合ランキング上の順位 (1 起点。マージ時は最良順位を採用)。</param>
/// <param name="ChunkVertexId">代表チャンクのVertex ID。</param>
public sealed record RagHit(
    string ChunkText,
    string HeadingPath,
    RagDocumentRef Document,
    int Rank,
    VertexId ChunkVertexId);

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
