using Quiver.Core;

namespace Quiver.Rag;

/// <summary>
/// ローカル RAG バックエンドのファサード。<see cref="GraphDatabase"/> をラップし、Document/Chunk
/// スキーマの索引 (sourceId 索引 + ベクトル索引 + 任意で全文索引) をコンストラクタで冪等に用意する。
/// 取込/再取込 (<see cref="UpsertDocumentAsync"/>) と削除 (<see cref="DeleteDocument"/>) は RAG-3 で実装する。
/// </summary>
/// <remarks>
/// コンストラクタは複数回・複数プロセスから呼んでも安全 (既存索引は再作成しない)。エンジン本体には
/// RAG 語彙を持ち込まず、ここがその境界となる (14_rag_layer.md §2)。
/// </remarks>
public sealed class RagStore
{
    private readonly GraphDatabase _db;
    private readonly RagStoreOptions _options;

    /// <summary>
    /// 指定 DB の上に RAG スキーマを用意する。索引が無ければ作成し、あれば何もしない (冪等)。
    /// </summary>
    /// <param name="db">ラップ対象のグラフ DB。</param>
    /// <param name="options">ベクトル次元・距離尺度・全文索引の有効/無効などの構成。</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> / <paramref name="options"/> が null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="RagStoreOptions.EmbeddingDimensions"/> が 1 未満。</exception>
    public RagStore(GraphDatabase db, RagStoreOptions options)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.EmbeddingDimensions < 1)
            throw new ArgumentOutOfRangeException(
                nameof(options), _options.EmbeddingDimensions,
                "EmbeddingDimensions は 1 以上である必要があります。");

        EnsureSchema();
    }

    /// <summary>ラップしているグラフ DB。検索 (RAG-4) や直接問い合わせで利用する。</summary>
    public GraphDatabase Database => _db;

    /// <summary>このストアの構成。</summary>
    public RagStoreOptions Options => _options;

    /// <summary>チャンク埋め込みベクトル索引の名前。</summary>
    public string VectorIndexName => _options.VectorIndexName;

    /// <summary>
    /// 全文索引が実際に存在し利用可能か。索引が既に存在すれば
    /// <see cref="RagStoreOptions.EnableFullTextIndex"/> の値に関わらず <c>true</c> (前セッションで作った
    /// 索引は透過維持フックで更新され続けるため検索に使える)。<c>EnableFullTextIndex</c> が <c>false</c>
    /// で索引も無い場合、またはバックエンドが全文索引非対応 (SQLite) の場合は <c>false</c> となり、
    /// 検索はベクトルのみで動く。
    /// </summary>
    public bool FullTextEnabled { get; private set; }

    /// <summary>
    /// 文書単位でべき等に取込/差し替えを行う。<c>contentHash</c> が既存と一致すれば no-op。
    /// </summary>
    /// <remarks>RAG-3 で実装予定。</remarks>
    public ValueTask<UpsertResult> UpsertDocumentAsync(
        IngestedDocument doc, IChunkEmbedder embedder, CancellationToken ct = default)
        => throw new NotImplementedException("UpsertDocumentAsync は RAG-3 で実装する。");

    /// <summary>指定 sourceId の文書と全チャンク・関係・索引エントリを削除する。存在しなければ <c>false</c>。</summary>
    /// <remarks>RAG-3 で実装予定。</remarks>
    public bool DeleteDocument(string sourceId)
        => throw new NotImplementedException("DeleteDocument は RAG-3 で実装する。");

    /// <summary>
    /// ラベル / 関係型 / プロパティキーを事前登録し、sourceId 索引・ベクトル索引・(任意で) 全文索引を
    /// 冪等に作成する。catalog 操作はトランザクション外で完結する。
    /// </summary>
    private void EnsureSchema()
    {
        var schema = _db.Schema;

        // トークンの事前登録 (GetOrCreate* はそれ自体が冪等)。
        schema.GetOrCreateLabel(RagSchema.DocumentLabel);
        schema.GetOrCreateLabel(RagSchema.ChunkLabel);
        schema.GetOrCreateRelationshipType(RagSchema.HasChunkType);
        schema.GetOrCreateRelationshipType(RagSchema.NextChunkType);
        schema.GetOrCreatePropertyKey(RagSchema.PropSourceId);
        schema.GetOrCreatePropertyKey(RagSchema.PropTitle);
        schema.GetOrCreatePropertyKey(RagSchema.PropContentHash);
        schema.GetOrCreatePropertyKey(RagSchema.PropIngestedAt);
        schema.GetOrCreatePropertyKey(RagSchema.PropMetadataJson);
        var textKey = schema.GetOrCreatePropertyKey(RagSchema.PropText);
        schema.GetOrCreatePropertyKey(RagSchema.PropOrdinal);
        schema.GetOrCreatePropertyKey(RagSchema.PropHeadingPath);
        schema.GetOrCreatePropertyKey(RagSchema.PropPage);
        schema.GetOrCreatePropertyKey(RagSchema.PropCharStart);
        schema.GetOrCreatePropertyKey(RagSchema.PropCharEnd);

        // Document.sourceId の検索用索引 (StringEquality)。エンジンに unique 制約は無く、
        // 一意性は RAG-3 の upsert (MergeNode) 側で担保する。
        if (!schema.IndexExists(RagSchema.DocSourceIndex))
            schema.CreateIndex(
                RagSchema.DocSourceIndex, RagSchema.DocumentLabel, RagSchema.PropSourceId,
                IndexKind.StringEquality);

        // Chunk 埋め込みベクトル索引。埋め込み元は Chunk.text。既存があれば次元・距離尺度が
        // options と一致することを照合する (reopen 時の取り違えを SetVector/検索まで遅延させない)。
        if (_db.Vectors.TryGetIndex(_options.VectorIndexName, out var existingVector))
        {
            if (existingVector.Dimensions != _options.EmbeddingDimensions)
                throw new InvalidOperationException(
                    $"既存ベクトル索引 '{_options.VectorIndexName}' の次元 {existingVector.Dimensions} が " +
                    $"options.EmbeddingDimensions {_options.EmbeddingDimensions} と一致しません。");
            if (existingVector.Metric != _options.VectorMetric)
                throw new InvalidOperationException(
                    $"既存ベクトル索引 '{_options.VectorIndexName}' の距離尺度 {existingVector.Metric} が " +
                    $"options.VectorMetric {_options.VectorMetric} と一致しません。");
        }
        else
        {
            _db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                Name: _options.VectorIndexName,
                EntityKind: EntityKind.Node,
                SourcePropertyKeyId: textKey,
                Dimensions: _options.EmbeddingDimensions,
                Metric: _options.VectorMetric,
                ProviderId: _options.VectorProviderId));
        }

        // Chunk.text 全文索引。作成は EnableFullTextIndex で制御するが、FullTextEnabled は
        // 「索引が実在し利用可能か」を表すため、既存索引があれば設定に関わらず true にする。
        // バックエンド非対応 (SQLite) のときは CreateFullTextIndex が NotSupportedException を
        // 投げるので graceful degrade する。
        FullTextEnabled = false;
        try
        {
            bool exists = false;
            foreach (var ft in schema.ListFullTextIndexes())
            {
                if (string.Equals(ft.Name, RagSchema.ChunkTextIndex, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }
            }
            if (!exists && _options.EnableFullTextIndex)
            {
                schema.CreateFullTextIndex(
                    RagSchema.ChunkTextIndex, RagSchema.ChunkLabel, RagSchema.PropText);
                exists = true;
            }
            FullTextEnabled = exists;
        }
        catch (NotSupportedException)
        {
            // SQLite backend など全文索引非対応。ベクトルのみで動作させる。
            FullTextEnabled = false;
        }
    }
}

/// <summary>
/// <see cref="RagStore.UpsertDocumentAsync"/> の結果。
/// </summary>
/// <param name="Unchanged"><c>contentHash</c> が既存と一致し no-op だったか。</param>
/// <param name="ChunkCount">取込後の文書のチャンク数 (no-op 時は既存値)。</param>
/// <param name="DocumentNodeId">対象 Document ノードの ID。</param>
public readonly record struct UpsertResult(bool Unchanged, int ChunkCount, NodeId DocumentNodeId);
