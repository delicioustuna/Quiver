using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Storage.Records;
using Quiver.Text;

namespace Quiver.Rag;

/// <summary>
/// ローカル RAG バックエンドのファサード。<see cref="GraphStore"/> をラップし、Document/Chunk
/// スキーマの索引 (sourceId 索引 + ベクトル索引 + 任意で全文索引) をコンストラクタで冪等に用意する。
/// 取込/再取込 (<see cref="UpsertDocumentAsync"/>) と削除 (<see cref="DeleteDocument"/>) を原子的に実行する。
/// </summary>
/// <remarks>
/// コンストラクタは複数回・複数プロセスから呼んでも安全 (既存索引は再作成しない)。エンジン本体には
/// RAG 語彙を持ち込まず、ここがその境界となる。
/// </remarks>
public sealed class RagStore
{
    private readonly GraphStore? _graphStore;
    private readonly QuiverDatabase _db;
    private readonly RagStoreOptions _options;
    private readonly string _profileDescriptor;
    private readonly string _profileFingerprint;

    /// <summary>
    /// 指定 DB の上に RAG スキーマを用意する。索引が無ければ作成し、あれば何もしない (冪等)。
    /// </summary>
    /// <param name="db">ラップ対象のグラフ DB。</param>
    /// <param name="options">ベクトル次元・距離尺度・全文索引の有効/無効などの構成。</param>
    /// <exception cref="ArgumentNullException"><paramref name="db"/> / <paramref name="options"/> が null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="RagStoreOptions.EmbeddingDimensions"/> が 1 未満。</exception>
    internal RagStore(QuiverDatabase db, RagStoreOptions options)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(_options);
        _profileDescriptor = BuildProfileDescriptor(_options);
        _profileFingerprint = ComputeHash(_profileDescriptor);

        PreflightIngestionProfile();
        EnsureSchema();
        EnsureIngestionProfile();
    }

    /// <summary>
    /// 採用済みの graph store 境界上に RAG スキーマを用意する。
    /// RAG 内部の transaction は同じ下層 store と snapshot 規則を共有する。
    /// </summary>
    /// <param name="store">ラップ対象の graph store。</param>
    /// <param name="options">ベクトル次元・距離尺度・全文索引の構成。</param>
    public RagStore(GraphStore store, RagStoreOptions options)
    {
        _graphStore = store ?? throw new ArgumentNullException(nameof(store));
        _db = store.DatabaseInternal;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(_options);
        _profileDescriptor = BuildProfileDescriptor(_options);
        _profileFingerprint = ComputeHash(_profileDescriptor);

        PreflightIngestionProfile();
        EnsureSchema();
        EnsureIngestionProfile();
    }

    /// <summary>ラップしているグラフ DB。検索や直接問い合わせで利用する。</summary>
    internal QuiverDatabase Database => _db;

    /// <summary>
    /// callback、明示 session、型付き workspace を提供する graph store。
    /// RAG と同じ下層 snapshot を共有する公開 graph store。
    /// </summary>
    public GraphStore GraphStore => _graphStore
        ?? throw new InvalidOperationException("内部回帰用の旧入口には GraphStore がありません。");

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
    /// 文書単位でべき等に取込/差し替えを行う。本文、表示属性、content revision、取込 profile の
    /// fingerprint が一致すれば DB を変更せず no-op を返す。本文が同じで title、metadata、revision だけが
    /// 異なる場合は Document を同じ ID のまま更新し、chunking と embedding を省略する。本文が異なる場合は
    /// <b>単一トランザクション</b>で旧 Document、Chunk、接続 Edge、参加 Nexus を削除し、新しい Document ID と
    /// チャンク群を作成する。
    /// </summary>
    /// <remarks>
    /// 埋め込み生成 (<paramref name="embedder"/>) はトランザクションを開く前に実行されるため、embedder が
    /// 失敗しても DB は無変更。差し替えは単一 Tx 性にクラッシュ安全性を委ね、追加機構は持たない
    /// (取込中 kill → 旧版が無傷 or 新版が完全、中間状態は残らない)。埋め込み入力・全文索引対象には
    /// 見出しパスを前置する (<c>searchText</c> プロパティ) ので、KNN は文脈を、BM25 は見出し語を引ける。
    /// 格納する <c>text</c> プロパティは生のチャンク本文のままで <c>charStart</c>/<c>charEnd</c> のスライス
    /// 不変条件を保つ。HNSW は削除/上書きで再リンクされ (RemoveVector/SetVector)、頻繁な再取込でも
    /// 物理削除 + 自動再構築でグラフ劣化を回収する。
    /// 取込 profile はコーパス単位で固定される。chunking、embedding model、normalization、埋め込み入力を
    /// 変更する場合、同じコーパス内へ異なる vector semantics を混在させず、別コーパスを構築して利用側で
    /// 切り替える。
    /// </remarks>
    /// <param name="doc">取込対象の正規化文書。</param>
    /// <param name="embedder">チャンク埋め込み生成器。<see cref="IChunkEmbedder.Dimensions"/> は
    /// <see cref="RagStoreOptions.EmbeddingDimensions"/> と一致している必要がある。</param>
    /// <param name="ct">キャンセルトークン (埋め込み生成にのみ作用)。</param>
    public async ValueTask<UpsertResult> UpsertDocumentAsync(
        IngestedDocument doc, IChunkEmbedder embedder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(embedder);
        if (embedder.Dimensions != _options.EmbeddingDimensions)
            throw new InvalidOperationException(
                $"embedder.Dimensions ({embedder.Dimensions}) が RagStoreOptions.EmbeddingDimensions " +
                $"({_options.EmbeddingDimensions}) と一致しません。");
        if (!string.Equals(
                embedder.ProfileId,
                _options.IngestionProfile.EmbeddingProfileId,
                StringComparison.Ordinal))
        {
            string requestedProfile = ComputeHash(BuildProfileDescriptor(
                _options with
                {
                    IngestionProfile = _options.IngestionProfile with
                    {
                        EmbeddingProfileId = embedder.ProfileId ?? string.Empty,
                    },
                }));
            throw new RagIngestionProfileMismatchException(
                _profileFingerprint,
                requestedProfile,
                $"embedder.ProfileId ('{embedder.ProfileId}') が構成済み embedding profile " +
                $"('{_options.IngestionProfile.EmbeddingProfileId}') と一致しません。");
        }

        var blocks = doc.Blocks ?? Array.Empty<IngestedBlock>();
        string contentHash = ComputeContentHash(blocks);
        string metadataJson = MetadataToJson(doc.Metadata);
        string ingestionFingerprint = ComputeIngestionFingerprint(
            contentHash,
            doc.Title,
            metadataJson,
            doc.ContentRevision);

        // 早期 no-op と属性だけの更新。write tx 下でも再確認するため、この間の競合は安全側に倒れる。
        if (TryGetUnchanged(
                doc.SourceId,
                contentHash,
                ingestionFingerprint,
                out bool contentMatches,
                out var unchanged))
            return unchanged;
        if (contentMatches
            && TryUpdateAttributes(
                doc,
                contentHash,
                metadataJson,
                ingestionFingerprint,
                out var attributesUpdated))
            return attributesUpdated;

        var drafts = Chunker.Chunk(blocks, _options.Chunking);

        // 埋め込みは Tx の外で先に実行 (I/O 待ちで Tx を長時間保持しない / 失敗時 DB 無変更)。
        float[][] embeddings;
        if (drafts.Count == 0)
        {
            embeddings = Array.Empty<float[]>();
        }
        else
        {
            embeddings = await embedder.EmbedAsync(BuildEmbeddingInputs(drafts), ct).ConfigureAwait(false);
            if (embeddings is null || embeddings.Length != drafts.Count)
                throw new InvalidOperationException(
                    $"embedder は {drafts.Count} 件のベクトルを返す必要があります (実際: {embeddings?.Length ?? 0})。");
            for (int i = 0; i < embeddings.Length; i++)
                if (embeddings[i] is null || embeddings[i].Length != _options.EmbeddingDimensions)
                    throw new InvalidOperationException(
                        $"ベクトル[{i}] の次元が不正です (期待 {_options.EmbeddingDimensions})。");
        }

        return UpsertCore(
            doc,
            contentHash,
            metadataJson,
            ingestionFingerprint,
            drafts,
            embeddings);
    }

    /// <summary>指定 sourceId の文書と全チャンク・関係・ベクトルを単一 Tx で削除する。存在しなければ <c>false</c>。</summary>
    /// <param name="sourceId">削除対象文書の一意キー。</param>
    public bool DeleteDocument(string sourceId)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        using var tx = _db.BeginWriteTransaction();

        // 索引には削除済みノードの orphan エントリが残り得るため生存確認で絞る
        // (DeleteVertex は二次索引を維持しない。エンジンの RepairIndexes が後で掃除する)。
        var docIds = new List<VertexId>();
        var seek = tx.SeekIndex(RagSchema.DocSourceIndex, PropertyValue.FromString(sourceId));
        while (seek.MoveNext())
            if (seek.Current.Kind == EntityKind.Vertex)
            {
                var id = new VertexId(seek.Current.Value);
                if (tx.VertexExists(id)) docIds.Add(id);
            }
        seek.Dispose();

        if (docIds.Count == 0)
        {
            tx.Rollback();
            return false;
        }

        foreach (var docId in docIds)
            DeleteDocumentVertex(tx, docId);
        tx.Commit();
        return true;
    }

    // ──  内部実装 ──────────────────────────────────────────

    /// <summary>PropertyValue (ref struct) を扱う同期トランザクション本体。async 本体から分離する。</summary>
    private UpsertResult UpsertCore(
        IngestedDocument doc,
        string contentHash,
        string metadataJson,
        string ingestionFingerprint,
        IReadOnlyList<ChunkDraft> drafts, float[][] embeddings)
    {
        using var tx = _db.BeginWriteTransaction();

        bool hasExisting = TryFindLiveDocument(tx, doc.SourceId, out VertexId existingDocument);
        if (hasExisting
            && TryReadString(
                tx,
                existingDocument,
                RagSchema.PropIngestionFingerprint,
                out string existing)
            && existing == ingestionFingerprint)
        {
            int existingCount = CountChunks(tx, existingDocument);
            tx.Rollback();
            return new UpsertResult(
                Disposition: RagUpsertDisposition.Unchanged,
                ChunkCount: existingCount,
                DocumentVertexId: new VertexKey(existingDocument),
                ReplacedDocumentVertexId: null);
        }

        if (hasExisting
            && TryReadString(
                tx,
                existingDocument,
                RagSchema.PropContentHash,
                out string existingContentHash)
            && existingContentHash == contentHash)
        {
            UpdateDocumentAttributes(
                tx,
                existingDocument,
                doc,
                metadataJson,
                ingestionFingerprint);
            int existingCount = CountChunks(tx, existingDocument);
            tx.Commit();
            return new UpsertResult(
                Disposition: RagUpsertDisposition.AttributesUpdated,
                ChunkCount: existingCount,
                DocumentVertexId: new VertexKey(existingDocument),
                ReplacedDocumentVertexId: null);
        }

        VertexId? replacedDocument = null;
        if (hasExisting)
        {
            replacedDocument = existingDocument;
            // 旧 ID の利用者関係を新 ID へ暗黙継承すると、再取込が利用者 graph の
            // 意味まで推測することになる。旧 Document の Edge/Nexus は cascade し、
            // 呼び出し側が返却 mapping を使って必要な関係だけを明示的に再アンカーする。
            DeleteDocumentVertex(tx, existingDocument);
        }

        VertexId docId = tx.CreateVertex(RagSchema.DocumentLabel);
        tx.SetProperty(
            docId,
            RagSchema.PropSourceId,
            PropertyValue.FromString(doc.SourceId));
        tx.SetProperty(docId, RagSchema.PropTitle, PropertyValue.FromString(doc.Title ?? string.Empty));
        tx.SetProperty(docId, RagSchema.PropContentHash, PropertyValue.FromString(contentHash));
        tx.SetProperty(
            docId,
            RagSchema.PropIngestionFingerprint,
            PropertyValue.FromString(ingestionFingerprint));
        tx.SetProperty(
            docId,
            RagSchema.PropContentRevision,
            PropertyValue.FromString(doc.ContentRevision ?? string.Empty));
        tx.SetProperty(docId, RagSchema.PropIngestedAt, PropertyValue.FromDateTime(DateTime.UtcNow));
        tx.SetProperty(docId, RagSchema.PropMetadataJson, PropertyValue.FromString(metadataJson));
        UpdatePromotedMetadata(tx, docId, doc.Metadata);

        // 新チャンク挿入 + HAS_CHUNK / NEXT_CHUNK 連結 + ベクトル。
        VertexId prev = default;
        bool hasPrev = false;
        for (int i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            var chunkId = tx.CreateVertex(RagSchema.ChunkLabel);
            tx.SetProperty(chunkId, RagSchema.PropText, PropertyValue.FromString(d.Text));
            // 全文索引対象は見出し語を含む searchText。SetProperty で透過維持フックが postings を追従する。
            tx.SetProperty(chunkId, RagSchema.PropSearchText,
                PropertyValue.FromString(ComposeSearchText(d.HeadingPath, d.Text)));
            tx.SetProperty(chunkId, RagSchema.PropOrdinal, PropertyValue.FromInt32(d.Ordinal));
            tx.SetProperty(chunkId, RagSchema.PropHeadingPath, PropertyValue.FromString(d.HeadingPath));
            if (d.Page.HasValue)
                tx.SetProperty(chunkId, RagSchema.PropPage, PropertyValue.FromInt32(d.Page.Value));
            tx.SetProperty(chunkId, RagSchema.PropCharStart, PropertyValue.FromInt32(d.CharStart));
            tx.SetProperty(chunkId, RagSchema.PropCharEnd, PropertyValue.FromInt32(d.CharEnd));

            tx.CreateEdge(docId, chunkId, RagSchema.HasChunkType);
            if (hasPrev) tx.CreateEdge(prev, chunkId, RagSchema.NextChunkType);
            tx.SetVectorProperty(
                EntityRef.From(chunkId),
                RagSchema.PropEmbedding,
                embeddings[i]);

            prev = chunkId;
            hasPrev = true;
        }

        tx.Commit();
        return new UpsertResult(
            Disposition: replacedDocument.HasValue
                ? RagUpsertDisposition.Replaced
                : RagUpsertDisposition.Created,
            ChunkCount: drafts.Count,
            DocumentVertexId: new VertexKey(docId),
            ReplacedDocumentVertexId: replacedDocument is { } oldDocument
                ? new VertexKey(oldDocument)
                : null);
    }

    /// <summary>埋め込み入力を組み立てる。見出しパスがあれば本文の前へ付与し検索時の文脈を補う。</summary>
    private string[] BuildEmbeddingInputs(IReadOnlyList<ChunkDraft> drafts)
    {
        var inputs = new string[drafts.Count];
        for (int i = 0; i < drafts.Count; i++)
            inputs[i] = _options.IngestionProfile.EmbeddingInputTemplate switch
            {
                RagEmbeddingInputTemplate.ChunkText => drafts[i].Text,
                RagEmbeddingInputTemplate.HeadingPathAndChunkText =>
                    ComposeSearchText(drafts[i].HeadingPath, drafts[i].Text),
                _ => throw new InvalidOperationException("未対応の埋め込み入力テンプレートです。"),
            };
        return inputs;
    }

    /// <summary>
    /// 検索・埋め込みに流すテキストを組み立てる。見出しパスがあれば本文の前へ前置し、KNN は文脈を、
    /// BM25 は見出し語を引けるようにする。見出しが空なら本文そのもの。
    /// </summary>
    internal static string ComposeSearchText(string headingPath, string text)
        => string.IsNullOrEmpty(headingPath) ? text : headingPath + "\n" + text;

    /// <summary>既存文書の fingerprint 一致を読み取り専用 Tx で確認する。</summary>
    private bool TryGetUnchanged(
        string sourceId,
        string contentHash,
        string ingestionFingerprint,
        out bool contentMatches,
        out UpsertResult result)
    {
        using var tx = _db.BeginReadTransaction();
        if (TryFindLiveDocument(tx, sourceId, out var docId))
        {
            if (TryReadString(
                    tx,
                    docId,
                    RagSchema.PropIngestionFingerprint,
                    out var existing)
                && existing == ingestionFingerprint)
            {
                contentMatches = true;
                result = new UpsertResult(
                    Disposition: RagUpsertDisposition.Unchanged,
                    ChunkCount: CountChunks(tx, docId),
                    DocumentVertexId: new VertexKey(docId),
                    ReplacedDocumentVertexId: null);
                return true;
            }
            contentMatches = TryReadString(
                    tx,
                    docId,
                    RagSchema.PropContentHash,
                    out string existingContentHash)
                && existingContentHash == contentHash;
            result = default;
            return false;
        }
        contentMatches = false;
        result = default;
        return false;
    }

    /// <summary>
    /// 本文が同じ場合に Document 属性だけを更新する。本文が競合更新されていた場合は <c>false</c> を返し、
    /// 呼び出し側が通常の chunking / embedding 経路へ進む。
    /// </summary>
    private bool TryUpdateAttributes(
        IngestedDocument doc,
        string contentHash,
        string metadataJson,
        string ingestionFingerprint,
        out UpsertResult result)
    {
        using var tx = _db.BeginWriteTransaction();
        if (!TryFindLiveDocument(tx, doc.SourceId, out VertexId document)
            || !TryReadString(tx, document, RagSchema.PropContentHash, out string existingContentHash)
            || existingContentHash != contentHash)
        {
            tx.Rollback();
            result = default;
            return false;
        }

        if (TryReadString(
                tx,
                document,
                RagSchema.PropIngestionFingerprint,
                out string existingFingerprint)
            && existingFingerprint == ingestionFingerprint)
        {
            int unchangedCount = CountChunks(tx, document);
            tx.Rollback();
            result = new UpsertResult(
                RagUpsertDisposition.Unchanged,
                unchangedCount,
                new VertexKey(document),
                null);
            return true;
        }

        UpdateDocumentAttributes(
            tx,
            document,
            doc,
            metadataJson,
            ingestionFingerprint);
        int chunkCount = CountChunks(tx, document);
        tx.Commit();
        result = new UpsertResult(
            RagUpsertDisposition.AttributesUpdated,
            chunkCount,
            new VertexKey(document),
            null);
        return true;
    }

    private void UpdateDocumentAttributes(
        IWriteTransaction tx,
        VertexId document,
        IngestedDocument doc,
        string metadataJson,
        string ingestionFingerprint)
    {
        tx.SetProperty(
            document,
            RagSchema.PropTitle,
            PropertyValue.FromString(doc.Title ?? string.Empty));
        tx.SetProperty(
            document,
            RagSchema.PropMetadataJson,
            PropertyValue.FromString(metadataJson));
        tx.SetProperty(
            document,
            RagSchema.PropContentRevision,
            PropertyValue.FromString(doc.ContentRevision ?? string.Empty));
        tx.SetProperty(
            document,
            RagSchema.PropIngestionFingerprint,
            PropertyValue.FromString(ingestionFingerprint));
        tx.SetProperty(
            document,
            RagSchema.PropIngestedAt,
            PropertyValue.FromDateTime(DateTime.UtcNow));
        UpdatePromotedMetadata(tx, document, doc.Metadata);
    }

    private void UpdatePromotedMetadata(
        IWriteTransaction tx,
        VertexId document,
        IReadOnlyDictionary<string, string>? metadata)
    {
        foreach (RagMetadataIndex definition in _options.MetadataIndexes)
        {
            if (metadata is not null
                && metadata.TryGetValue(definition.MetadataKey, out string? value))
            {
                tx.SetProperty(
                    document,
                    definition.PropertyKey,
                    PropertyValue.FromString(value ?? string.Empty));
            }
            else if (tx.HasProperty(document, definition.PropertyKey))
            {
                tx.RemoveProperty(document, definition.PropertyKey);
            }
        }
    }

    /// <summary>sourceId 索引から最初の<b>生存</b> Document ノードを返す (orphan エントリは skip)。</summary>
    private static bool TryFindLiveDocument(IReadTransaction tx, string sourceId, out VertexId docId)
    {
        var seek = tx.SeekIndex(RagSchema.DocSourceIndex, PropertyValue.FromString(sourceId));
        try
        {
            while (seek.MoveNext())
            {
                if (seek.Current.Kind != EntityKind.Vertex) continue;
                var id = new VertexId(seek.Current.Value);
                if (tx.VertexExists(id)) { docId = id; return true; }
            }
        }
        finally { seek.Dispose(); }
        docId = default;
        return false;
    }

    /// <summary>Document に紐づく Chunk ノードを収集する (列挙中に削除しないため一旦リスト化)。</summary>
    private static List<VertexId> CollectChunks(IReadTransaction tx, VertexId docId)
    {
        var chunks = new List<VertexId>();
        var e = tx.EnumerateEdges(docId, Direction.Outgoing, RagSchema.HasChunkType);
        while (e.MoveNext()) chunks.Add(e.Current.Target);
        return chunks;
    }

    private static int CountChunks(IReadTransaction tx, VertexId docId)
    {
        int n = 0;
        var e = tx.EnumerateEdges(docId, Direction.Outgoing, RagSchema.HasChunkType);
        while (e.MoveNext()) n++;
        return n;
    }

    // DeleteVertex はノードに接続する全リレーションを自身でカスケード削除する
    // (GraphTransaction.DeleteVertex)。そのためここでは vector の除去とノード削除のみ行う。

    /// <summary>Chunk のベクトルとノード (接続関係はカスケード) を削除する。</summary>
    private void DeleteChunk(IWriteTransaction tx, VertexId chunkId)
    {
        tx.RemoveProperty(chunkId, RagSchema.PropEmbedding);
        tx.DeleteVertex(chunkId);
    }

    /// <summary>Document とその全 Chunk・関係・ベクトルを削除する。</summary>
    private void DeleteDocumentVertex(IWriteTransaction tx, VertexId docId)
    {
        foreach (var chunkId in CollectChunks(tx, docId))
            DeleteChunk(tx, chunkId);
        tx.DeleteVertex(docId);
    }

    private static bool TryReadString(IReadTransaction tx, VertexId vertexId, string key, out string value)
    {
        if (tx.HasProperty(vertexId, key))
        {
            var pv = tx.GetProperty(vertexId, key);
            if (pv.Type == PropertyValueType.String)
            {
                value = Encoding.UTF8.GetString(pv.Utf8StringValue);
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    /// <summary>Blocks の正規化 SHA-256 ハッシュ (16 進大文字)。属性更新と再チャンクを区別する。</summary>
    private static string ComputeContentHash(IReadOnlyList<IngestedBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            // 長さプレフィックス付きで連結し、本文に区切りが含まれても衝突しないようにする。
            sb.Append((int)b.Kind).Append('|')
              .Append(b.HeadingLevel ?? -1).Append('|')
              .Append(b.Page ?? -1).Append('|');
            string text = b.Text ?? string.Empty;
            sb.Append(text.Length).Append(':').Append(text).Append('\0');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>string→string メタデータを決定的な JSON オブジェクトへ直列化する (キー昇順)。</summary>
    private static string MetadataToJson(IReadOnlyDictionary<string, string>? meta)
    {
        if (meta is null || meta.Count == 0) return "{}";
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var kv in meta.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            AppendJsonString(sb, kv.Key);
            sb.Append(':');
            AppendJsonString(sb, kv.Value ?? string.Empty);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private string ComputeIngestionFingerprint(
        string contentHash,
        string? title,
        string metadataJson,
        string? contentRevision)
    {
        var descriptor = new StringBuilder();
        AppendFingerprintPart(descriptor, "contract", "rag-document-v1");
        AppendFingerprintPart(descriptor, "profile", _profileFingerprint);
        AppendFingerprintPart(descriptor, "content", contentHash);
        AppendFingerprintPart(descriptor, "title", title ?? string.Empty);
        AppendFingerprintPart(descriptor, "metadata", metadataJson);
        AppendFingerprintPart(descriptor, "revision", contentRevision ?? string.Empty);
        return ComputeHash(descriptor.ToString());
    }

    private static string BuildProfileDescriptor(RagStoreOptions options)
    {
        var descriptor = new StringBuilder();
        AppendFingerprintPart(descriptor, "contract", "rag-corpus-v1");
        AppendFingerprintPart(
            descriptor,
            "chunkingProfile",
            options.IngestionProfile.ChunkingProfileId);
        AppendFingerprintPart(
            descriptor,
            "chunkTargetSize",
            options.Chunking.TargetSize.ToString(CultureInfo.InvariantCulture));
        AppendFingerprintPart(
            descriptor,
            "chunkOverlap",
            options.Chunking.Overlap.ToString(CultureInfo.InvariantCulture));
        AppendFingerprintPart(
            descriptor,
            "chunkMaximumSize",
            options.Chunking.MaxChunkSize.ToString(CultureInfo.InvariantCulture));
        AppendFingerprintPart(
            descriptor,
            "embeddingProfile",
            options.IngestionProfile.EmbeddingProfileId);
        AppendFingerprintPart(
            descriptor,
            "normalizationProfile",
            options.IngestionProfile.NormalizationProfileId);
        AppendFingerprintPart(
            descriptor,
            "embeddingInputTemplate",
            options.IngestionProfile.EmbeddingInputTemplate.ToString());
        AppendFingerprintPart(
            descriptor,
            "embeddingDimensions",
            options.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture));
        AppendFingerprintPart(descriptor, "vectorMetric", options.VectorMetric.ToString());
        AppendFingerprintPart(descriptor, "vectorIndex", options.VectorIndexName);
        if (options.FullTextFilters.Count > 0)
        {
            AppendFingerprintPart(
                descriptor,
                "fullTextFilters",
                FullTextDefinitionCodec.EncodeFilters(options.FullTextFilters));
        }
        foreach (RagMetadataIndex metadataIndex in options.MetadataIndexes
                     .OrderBy(static item => item.MetadataKey, StringComparer.Ordinal)
                     .ThenBy(static item => item.PropertyKey, StringComparer.Ordinal)
                     .ThenBy(static item => item.IndexName, StringComparer.Ordinal))
        {
            AppendFingerprintPart(descriptor, "metadataKey", metadataIndex.MetadataKey);
            AppendFingerprintPart(descriptor, "metadataProperty", metadataIndex.PropertyKey);
            AppendFingerprintPart(descriptor, "metadataIndex", metadataIndex.IndexName);
        }
        return descriptor.ToString();
    }

    private static void AppendFingerprintPart(StringBuilder builder, string name, string value)
    {
        builder.Append(name.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(name)
            .Append('=')
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');
    }

    private static string ComputeHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateOptions(RagStoreOptions options)
    {
        if (options.EmbeddingDimensions < 1)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.EmbeddingDimensions,
                "EmbeddingDimensions は 1 以上である必要があります。");
        ArgumentNullException.ThrowIfNull(options.IngestionProfile);
        ArgumentNullException.ThrowIfNull(options.Chunking);
        ArgumentNullException.ThrowIfNull(options.MetadataIndexes);
        ArgumentNullException.ThrowIfNull(options.FullTextFilters);
        options.IngestionProfile.Validate();
        options.Chunking.Validate();
        if (string.IsNullOrWhiteSpace(options.VectorIndexName))
            throw new ArgumentException("VectorIndexName は空にできません。", nameof(options));
        foreach (ITokenFilter filter in options.FullTextFilters)
            ArgumentNullException.ThrowIfNull(filter);

        var metadataKeys = new HashSet<string>(StringComparer.Ordinal);
        var propertyKeys = new HashSet<string>(StringComparer.Ordinal);
        var indexNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (RagMetadataIndex definition in options.MetadataIndexes)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ValidateMetadataIndexPart(definition.MetadataKey, nameof(definition.MetadataKey));
            ValidateMetadataIndexPart(definition.PropertyKey, nameof(definition.PropertyKey));
            ValidateMetadataIndexPart(definition.IndexName, nameof(definition.IndexName));
            if (!metadataKeys.Add(definition.MetadataKey))
                throw new ArgumentException(
                    $"metadata key '{definition.MetadataKey}' が重複しています。",
                    nameof(options));
            if (!propertyKeys.Add(definition.PropertyKey))
                throw new ArgumentException(
                    $"metadata property '{definition.PropertyKey}' が重複しています。",
                    nameof(options));
            if (ReservedPropertyKeys.Contains(definition.PropertyKey, StringComparer.Ordinal))
                throw new ArgumentException(
                    $"metadata property '{definition.PropertyKey}' は RAG の予約 property です。",
                    nameof(options));
            if (!indexNames.Add(definition.IndexName)
                || string.Equals(
                    definition.IndexName,
                    options.VectorIndexName,
                    StringComparison.Ordinal)
                || string.Equals(
                    definition.IndexName,
                    RagSchema.DocSourceIndex,
                    StringComparison.Ordinal)
                || string.Equals(
                    definition.IndexName,
                    RagSchema.ChunkTextIndex,
                    StringComparison.Ordinal))
                throw new ArgumentException(
                    $"metadata index name '{definition.IndexName}' が重複または予約済みです。",
                    nameof(options));
        }
    }

    private static void ValidateMetadataIndexPart(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("metadata index の識別子は空にできません。", parameterName);
    }

    private static readonly string[] ReservedPropertyKeys =
    [
        RagSchema.PropSourceId,
        RagSchema.PropTitle,
        RagSchema.PropContentHash,
        RagSchema.PropIngestionFingerprint,
        RagSchema.PropContentRevision,
        RagSchema.PropIngestedAt,
        RagSchema.PropMetadataJson,
        RagSchema.PropProfileFingerprint,
        RagSchema.PropProfileDescriptor,
        RagSchema.PropText,
        RagSchema.PropSearchText,
        RagSchema.PropEmbedding,
        RagSchema.PropOrdinal,
        RagSchema.PropHeadingPath,
        RagSchema.PropPage,
        RagSchema.PropCharStart,
        RagSchema.PropCharEnd,
    ];

    /// <summary>既存 profile を schema 変更前に検証し、不一致 DB を変更せず拒否する。</summary>
    private void PreflightIngestionProfile()
    {
        using var tx = _db.BeginReadTransaction();
        List<VertexId> profiles = CollectProfileVertices(tx);
        if (profiles.Count > 1)
            throw new InvalidOperationException("RAG ingestion profile marker が複数存在します。");
        if (profiles.Count == 1)
        {
            ValidateStoredProfile(tx, profiles[0]);
            return;
        }

        if (HasRagDocuments(tx))
            ValidateLegacyProfileAdoption();
    }

    /// <summary>schema 作成後、空 DB または明示採用された旧コーパスへ profile marker を記録する。</summary>
    private void EnsureIngestionProfile()
    {
        using var tx = _db.BeginWriteTransaction();
        List<VertexId> profiles = CollectProfileVertices(tx);
        if (profiles.Count > 1)
            throw new InvalidOperationException("RAG ingestion profile marker が複数存在します。");
        if (profiles.Count == 1)
        {
            ValidateStoredProfile(tx, profiles[0]);
            tx.Rollback();
            return;
        }
        if (HasRagDocuments(tx))
            ValidateLegacyProfileAdoption();

        VertexId profile = tx.CreateVertex(RagSchema.IngestionProfileLabel);
        tx.SetProperty(
            profile,
            RagSchema.PropProfileFingerprint,
            PropertyValue.FromString(_profileFingerprint));
        tx.SetProperty(
            profile,
            RagSchema.PropProfileDescriptor,
            PropertyValue.FromString(_profileDescriptor));
        tx.Commit();
    }

    private void ValidateLegacyProfileAdoption()
    {
        if (!_options.AdoptLegacyIngestionProfile)
            throw new RagIngestionProfileMismatchException(
                string.Empty,
                _profileFingerprint,
                "既存 RAG コーパスに ingestion profile が記録されていません。" +
                "使用済み profile を特定できる場合だけ AdoptLegacyIngestionProfile を明示してください。");
        if (_options.MetadataIndexes.Count > 0)
            throw new RagIngestionProfileMismatchException(
                string.Empty,
                _profileFingerprint,
                "旧コーパスの profile 採用と metadata index 昇格は同時に実行できません。" +
                "別コーパスへ再取込してください。");
    }

    private void ValidateStoredProfile(IReadTransaction tx, VertexId profile)
    {
        if (!TryReadString(
                tx,
                profile,
                RagSchema.PropProfileFingerprint,
                out string storedFingerprint))
            throw new InvalidOperationException("RAG ingestion profile marker に fingerprint がありません。");
        if (!string.Equals(storedFingerprint, _profileFingerprint, StringComparison.Ordinal))
        {
            string storedDescriptor = TryReadString(
                tx,
                profile,
                RagSchema.PropProfileDescriptor,
                out string descriptor)
                ? descriptor
                : "<descriptor missing>";
            throw new RagIngestionProfileMismatchException(
                storedFingerprint,
                _profileFingerprint,
                "要求された RAG ingestion profile は既存コーパスと一致しません。" +
                $" stored='{storedDescriptor}', requested='{_profileDescriptor}'。");
        }
    }

    private static List<VertexId> CollectProfileVertices(IReadTransaction tx)
        => tx.Query.Vertices()
            .HasLabel(RagSchema.IngestionProfileLabel)
            .ToList()
            .Where(tx.VertexExists)
            .ToList();

    private static bool HasRagDocuments(IReadTransaction tx)
        => tx.Query.Vertices()
            .HasLabel(RagSchema.DocumentLabel)
            .ToList()
            .Any(tx.VertexExists);

    /// <summary>
    /// ラベル / 関係型 / プロパティキーを事前登録し、sourceId 索引・ベクトル索引・(任意で) 全文索引を
    /// 冪等に作成する。catalog 変更は write transaction と同じ durable boundary に参加する。
    /// </summary>
    private void EnsureSchema()
    {
        using (var schemaTx = _db.BeginWriteTransaction())
        {
            var schema = schemaTx.EditSchema;

            schema.GetOrCreateLabel(RagSchema.DocumentLabel);
            schema.GetOrCreateLabel(RagSchema.ChunkLabel);
            schema.GetOrCreateLabel(RagSchema.IngestionProfileLabel);
            schema.GetOrCreateEdgeType(RagSchema.HasChunkType);
            schema.GetOrCreateEdgeType(RagSchema.NextChunkType);
            schema.GetOrCreatePropertyKey(RagSchema.PropSourceId);
            schema.GetOrCreatePropertyKey(RagSchema.PropTitle);
            schema.GetOrCreatePropertyKey(RagSchema.PropContentHash);
            schema.GetOrCreatePropertyKey(RagSchema.PropIngestionFingerprint);
            schema.GetOrCreatePropertyKey(RagSchema.PropContentRevision);
            schema.GetOrCreatePropertyKey(RagSchema.PropIngestedAt);
            schema.GetOrCreatePropertyKey(RagSchema.PropMetadataJson);
            schema.GetOrCreatePropertyKey(RagSchema.PropProfileFingerprint);
            schema.GetOrCreatePropertyKey(RagSchema.PropProfileDescriptor);
            schema.GetOrCreatePropertyKey(RagSchema.PropText);
            schema.GetOrCreatePropertyKey(RagSchema.PropSearchText);
            schema.GetOrCreatePropertyKey(RagSchema.PropEmbedding);
            schema.GetOrCreatePropertyKey(RagSchema.PropOrdinal);
            schema.GetOrCreatePropertyKey(RagSchema.PropHeadingPath);
            schema.GetOrCreatePropertyKey(RagSchema.PropPage);
            schema.GetOrCreatePropertyKey(RagSchema.PropCharStart);
            schema.GetOrCreatePropertyKey(RagSchema.PropCharEnd);
            foreach (RagMetadataIndex metadataIndex in _options.MetadataIndexes)
                schema.GetOrCreatePropertyKey(metadataIndex.PropertyKey);

            if (!schema.IndexExists(RagSchema.DocSourceIndex))
                schema.CreateIndex(new ScalarIndexDefinition(
                    RagSchema.DocSourceIndex,
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        RagSchema.PropSourceId,
                        RagSchema.DocumentLabel),
                    IndexKind.StringEquality));

            foreach (RagMetadataIndex metadataIndex in _options.MetadataIndexes)
            {
                var expectedTarget = new PropertyTarget(
                    PropertyOwnerKind.Vertex,
                    metadataIndex.PropertyKey,
                    RagSchema.DocumentLabel);
                if (schema.TryGetIndex(metadataIndex.IndexName, out IndexInfo existingMetadata))
                {
                    if (existingMetadata.Definition is not ScalarIndexDefinition scalar
                        || scalar.Kind != IndexKind.StringEquality
                        || scalar.Target != expectedTarget)
                        throw new InvalidOperationException(
                            $"既存index '{metadataIndex.IndexName}' は要求された RAG metadata index と一致しません。");
                }
                else
                {
                    schema.CreateIndex(new ScalarIndexDefinition(
                        metadataIndex.IndexName,
                        expectedTarget,
                        IndexKind.StringEquality));
                }
            }

            if (schema.TryGetIndex(_options.VectorIndexName, out IndexInfo existing))
            {
                if (existing.Definition is not VectorIndexDefinition existingVector)
                    throw new InvalidOperationException(
                        $"既存index '{_options.VectorIndexName}' はvector indexではありません。");
                if (existingVector.Dimensions != _options.EmbeddingDimensions)
                    throw new InvalidOperationException(
                        $"既存index '{_options.VectorIndexName}' の次元は" +
                        $"{existingVector.Dimensions}ですが、要求値は{_options.EmbeddingDimensions}です。");
                if (existingVector.Metric != _options.VectorMetric)
                    throw new InvalidOperationException(
                        $"既存index '{_options.VectorIndexName}' の距離尺度は" +
                        $"{existingVector.Metric}ですが、要求値は{_options.VectorMetric}です。");
                if (existingVector.Target.PropertyKey != RagSchema.PropEmbedding)
                    throw new InvalidOperationException(
                        $"既存index '{_options.VectorIndexName}' のvector propertyが一致しません。");
            }
            else
            {
                schema.CreateIndex(new VectorIndexDefinition(
                    _options.VectorIndexName,
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        RagSchema.PropEmbedding,
                        RagSchema.ChunkLabel),
                    _options.EmbeddingDimensions,
                    _options.VectorMetric));
            }
            schemaTx.Commit();
        }

        // Chunk.searchText 全文索引。作成は EnableFullTextIndex で制御するが、FullTextEnabled は
        // 「索引が実在し利用可能か」を表すため、既存索引があれば設定に関わらず true にする。
        // バックエンド非対応 (SQLite) のときは CreateFullTextIndex が NotSupportedException を
        // 投げるので graceful degrade する。
        FullTextEnabled = false;
        try
        {
            using var schemaTx = _db.BeginWriteTransaction();
            var schema = schemaTx.EditSchema;
            bool exists = false;
            foreach (IndexInfo ft in schema.ListIndexes())
            {
                if (!string.Equals(ft.Name, RagSchema.ChunkTextIndex, StringComparison.Ordinal))
                    continue;
                if (ft.Definition is not FullTextIndexDefinition existingFullText)
                    throw new InvalidOperationException(
                        $"既存index '{RagSchema.ChunkTextIndex}' は全文indexではありません。");
                var expectedTarget = new PropertyTarget(
                    PropertyOwnerKind.Vertex,
                    RagSchema.PropSearchText,
                    RagSchema.ChunkLabel);
                string baseTokenizerId = existingFullText.TokenizerId.Split('+', 2)[0];
                if (existingFullText.Target != expectedTarget
                    || baseTokenizerId != MixedBigramTokenizer.UnigramTokenizerId
                    || FullTextDefinitionCodec.EncodeFilters(existingFullText.Filters)
                        != FullTextDefinitionCodec.EncodeFilters(_options.FullTextFilters))
                {
                    throw new InvalidOperationException(
                        $"既存index '{RagSchema.ChunkTextIndex}' は要求された RAG 全文indexと一致しません。");
                }
                exists = true;
                break;
            }
            if (!exists && _options.EnableFullTextIndex)
            {
                // 見出し語も BM25 で引けるよう searchText (= 見出しパス + 本文) を索引対象にする。
                schema.CreateIndex(new FullTextIndexDefinition(
                    RagSchema.ChunkTextIndex,
                    new PropertyTarget(
                        PropertyOwnerKind.Vertex,
                        RagSchema.PropSearchText,
                        RagSchema.ChunkLabel),
                    Filters: _options.FullTextFilters));
                exists = true;
            }
            schemaTx.Commit();
            FullTextEnabled = exists;
        }
        catch (NotSupportedException)
        {
            // SQLite backend など全文索引非対応。ベクトルのみで動作させる。
            FullTextEnabled = false;
        }
    }
}

/// <summary><see cref="RagStore.UpsertDocumentAsync"/> が適用した変更種別。</summary>
public enum RagUpsertDisposition
{
    /// <summary>新しい Document と Chunk を作成した。</summary>
    Created,

    /// <summary>fingerprint が一致し、DB を変更しなかった。</summary>
    Unchanged,

    /// <summary>本文と Chunk を維持し、title、metadata、content revision だけを更新した。</summary>
    AttributesUpdated,

    /// <summary>本文変更により旧 Document と Chunk を置き換えた。</summary>
    Replaced,
}

/// <summary>
/// <see cref="RagStore.UpsertDocumentAsync"/> の結果。
/// </summary>
/// <param name="Disposition">適用した変更種別。</param>
/// <param name="ChunkCount">取込後の文書のチャンク数 (no-op 時は既存値)。</param>
/// <param name="DocumentVertexId">対象 Document ノードの不透明 key。</param>
/// <param name="ReplacedDocumentVertexId">内容変更で置換した旧 Document key。新規作成または no-op は <see langword="null"/>。</param>
public readonly record struct UpsertResult(
    RagUpsertDisposition Disposition,
    int ChunkCount,
    VertexKey DocumentVertexId,
    VertexKey? ReplacedDocumentVertexId)
{
    /// <summary>fingerprint が一致し、DB を変更しなかったか。</summary>
    public bool Unchanged => Disposition == RagUpsertDisposition.Unchanged;
}
