using System.Security.Cryptography;
using System.Text;
using Quiver.Core;
using Quiver.Storage.Records;

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
    /// 文書単位でべき等に取込/差し替えを行う。Blocks の正規化ハッシュ (<c>contentHash</c>) が既存と
    /// 一致すれば DB を一切変更せず no-op を返す。不一致なら<b>単一トランザクション</b>で旧 Chunk 群と
    /// その関係を削除し、新チャンクを挿入して property / <c>NEXT_CHUNK</c> 連結 / ベクトルを設定する。
    /// </summary>
    /// <remarks>
    /// 埋め込み生成 (<paramref name="embedder"/>) はトランザクションを開く前に実行されるため、embedder が
    /// 失敗しても DB は無変更。差し替えは単一 Tx 性にクラッシュ安全性を委ね、追加機構は持たない
    /// (取込中 kill → 旧版が無傷 or 新版が完全、中間状態は残らない)。埋め込み入力・全文索引対象には
    /// 見出しパスを前置する (<c>searchText</c> プロパティ) ので、KNN は文脈を、BM25 は見出し語を引ける。
    /// 格納する <c>text</c> プロパティは生のチャンク本文のままで <c>charStart</c>/<c>charEnd</c> のスライス
    /// 不変条件を保つ。HNSW は削除/上書きで再リンクされ (RemoveVector/SetVector)、頻繁な再取込でも
    /// 物理削除 + 自動再構築でグラフ劣化を回収する。
    /// <para>
    /// <b>制限</b>: <c>contentHash</c> は <see cref="IngestedDocument.Blocks"/> のみから算出する。
    /// Blocks を変えずに <see cref="IngestedDocument.Title"/> / <see cref="IngestedDocument.Metadata"/>
    /// だけ変更して再取込しても no-op となり、既存の title / metadata が保持される
    /// (再チャンク・再埋め込みを避けるための割り切り)。メタだけ更新したい場合は Blocks に変化を与えるか
    /// 個別 API を別途用意すること。
    /// </para>
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

        var blocks = doc.Blocks ?? Array.Empty<IngestedBlock>();
        string contentHash = ComputeContentHash(blocks);

        // 早期 no-op: 既存文書とハッシュ一致なら chunk / embed を一切回さずに返す
        // (UpsertCore でも write tx 下で再確認するため、この間の競合は安全側に倒れる)。
        if (TryGetUnchanged(doc.SourceId, contentHash, out var unchanged))
            return unchanged;

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

        return UpsertCore(doc, contentHash, drafts, embeddings);
    }

    /// <summary>指定 sourceId の文書と全チャンク・関係・ベクトルを単一 Tx で削除する。存在しなければ <c>false</c>。</summary>
    /// <param name="sourceId">削除対象文書の一意キー。</param>
    public bool DeleteDocument(string sourceId)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        using var tx = _db.BeginTransaction();

        // 索引には削除済みノードの orphan エントリが残り得るため生存確認で絞る
        // (DeleteNode は二次索引を維持しない。エンジンの RepairIndexes が後で掃除する)。
        var docIds = new List<NodeId>();
        var seek = tx.SeekIndex(RagSchema.DocSourceIndex, PropertyValue.FromString(sourceId));
        while (seek.MoveNext())
            if (tx.NodeExists(seek.Current)) docIds.Add(seek.Current);
        seek.Dispose();

        if (docIds.Count == 0)
        {
            tx.Rollback();
            return false;
        }

        foreach (var docId in docIds)
            DeleteDocumentNode(tx, docId);
        tx.Commit();
        return true;
    }

    // ── RAG-3 内部実装 ──────────────────────────────────────────

    /// <summary>PropertyValue (ref struct) を扱う同期トランザクション本体。async 本体から分離する。</summary>
    private UpsertResult UpsertCore(
        IngestedDocument doc, string contentHash,
        IReadOnlyList<ChunkDraft> drafts, float[][] embeddings)
    {
        using var tx = _db.BeginTransaction();

        var (docId, created) = tx.MergeNode(
            RagSchema.DocumentLabel, RagSchema.PropSourceId, PropertyValue.FromString(doc.SourceId));

        // べき等: 既存文書でハッシュ一致なら何も変更しない。
        if (!created && TryReadString(tx, docId, RagSchema.PropContentHash, out var existing)
            && existing == contentHash)
        {
            int existingCount = CountChunks(tx, docId);
            tx.Rollback();
            return new UpsertResult(Unchanged: true, ChunkCount: existingCount, DocumentNodeId: docId);
        }

        // 差し替え: 既存 Chunk 群を削除してから入れ直す。
        if (!created)
            foreach (var oldChunk in CollectChunks(tx, docId))
                DeleteChunk(tx, oldChunk);

        // Document プロパティ (sourceId は MergeNode 作成時に設定済み)。
        tx.SetProperty(docId, RagSchema.PropTitle, PropertyValue.FromString(doc.Title ?? string.Empty));
        tx.SetProperty(docId, RagSchema.PropContentHash, PropertyValue.FromString(contentHash));
        tx.SetProperty(docId, RagSchema.PropIngestedAt, PropertyValue.FromDateTime(DateTime.UtcNow));
        tx.SetProperty(docId, RagSchema.PropMetadataJson, PropertyValue.FromString(MetadataToJson(doc.Metadata)));

        // 新チャンク挿入 + HAS_CHUNK / NEXT_CHUNK 連結 + ベクトル。
        NodeId prev = default;
        bool hasPrev = false;
        for (int i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            var chunkId = tx.CreateNode(RagSchema.ChunkLabel);
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

            tx.CreateRelationship(docId, chunkId, RagSchema.HasChunkType);
            if (hasPrev) tx.CreateRelationship(prev, chunkId, RagSchema.NextChunkType);
            tx.SetVector(EntityKind.Node, chunkId.Value, _options.VectorIndexName, embeddings[i]);

            prev = chunkId;
            hasPrev = true;
        }

        tx.Commit();
        return new UpsertResult(Unchanged: false, ChunkCount: drafts.Count, DocumentNodeId: docId);
    }

    /// <summary>埋め込み入力を組み立てる。見出しパスがあれば本文の前へ付与し検索時の文脈を補う。</summary>
    private static string[] BuildEmbeddingInputs(IReadOnlyList<ChunkDraft> drafts)
    {
        var inputs = new string[drafts.Count];
        for (int i = 0; i < drafts.Count; i++)
            inputs[i] = ComposeSearchText(drafts[i].HeadingPath, drafts[i].Text);
        return inputs;
    }

    /// <summary>
    /// 検索・埋め込みに流すテキストを組み立てる。見出しパスがあれば本文の前へ前置し、KNN は文脈を、
    /// BM25 は見出し語を引けるようにする。見出しが空なら本文そのもの。
    /// </summary>
    internal static string ComposeSearchText(string headingPath, string text)
        => string.IsNullOrEmpty(headingPath) ? text : headingPath + "\n" + text;

    /// <summary>既存文書のハッシュ一致を読み取り専用 Tx で確認する (embed 前の早期 no-op 判定)。</summary>
    private bool TryGetUnchanged(string sourceId, string contentHash, out UpsertResult result)
    {
        using var tx = _db.BeginReadOnlyTransaction();
        if (TryFindLiveDocument(tx, sourceId, out var docId)
            && TryReadString(tx, docId, RagSchema.PropContentHash, out var existing)
            && existing == contentHash)
        {
            result = new UpsertResult(Unchanged: true, ChunkCount: CountChunks(tx, docId), DocumentNodeId: docId);
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>sourceId 索引から最初の<b>生存</b> Document ノードを返す (orphan エントリは skip)。</summary>
    private static bool TryFindLiveDocument(IGraphTransaction tx, string sourceId, out NodeId docId)
    {
        var seek = tx.SeekIndex(RagSchema.DocSourceIndex, PropertyValue.FromString(sourceId));
        try
        {
            while (seek.MoveNext())
                if (tx.NodeExists(seek.Current)) { docId = seek.Current; return true; }
        }
        finally { seek.Dispose(); }
        docId = default;
        return false;
    }

    /// <summary>Document に紐づく Chunk ノードを収集する (列挙中に削除しないため一旦リスト化)。</summary>
    private static List<NodeId> CollectChunks(IGraphTransaction tx, NodeId docId)
    {
        var chunks = new List<NodeId>();
        var e = tx.EnumerateRelationships(docId, Direction.Outgoing, RagSchema.HasChunkType);
        while (e.MoveNext()) chunks.Add(e.Current.Target);
        return chunks;
    }

    private static int CountChunks(IGraphTransaction tx, NodeId docId)
    {
        int n = 0;
        var e = tx.EnumerateRelationships(docId, Direction.Outgoing, RagSchema.HasChunkType);
        while (e.MoveNext()) n++;
        return n;
    }

    // DeleteNode はノードに接続する全リレーションを自身でカスケード削除する
    // (GraphTransaction.DeleteNode)。そのためここでは vector の除去とノード削除のみ行う。

    /// <summary>Chunk のベクトルとノード (接続関係はカスケード) を削除する。</summary>
    private void DeleteChunk(IGraphTransaction tx, NodeId chunkId)
    {
        tx.RemoveVector(EntityKind.Node, chunkId.Value, _options.VectorIndexName);
        tx.DeleteNode(chunkId);
    }

    /// <summary>Document とその全 Chunk・関係・ベクトルを削除する。</summary>
    private void DeleteDocumentNode(IGraphTransaction tx, NodeId docId)
    {
        foreach (var chunkId in CollectChunks(tx, docId))
            DeleteChunk(tx, chunkId);
        tx.DeleteNode(docId);
    }

    private static bool TryReadString(IGraphTransaction tx, NodeId nodeId, string key, out string value)
    {
        if (tx.HasProperty(nodeId, key))
        {
            var pv = tx.GetProperty(nodeId, key);
            if (pv.Type == PropertyValueType.String)
            {
                value = Encoding.UTF8.GetString(pv.Utf8StringValue);
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    /// <summary>Blocks の正規化 SHA-256 ハッシュ (16 進大文字)。再取込の no-op 判定に使う。</summary>
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
            sb.Append(text.Length).Append(':').Append(text).Append(' ');
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
        schema.GetOrCreatePropertyKey(RagSchema.PropText);
        var searchTextKey = schema.GetOrCreatePropertyKey(RagSchema.PropSearchText);
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

        // Chunk 埋め込みベクトル索引。埋め込み元は Chunk.searchText (見出しパス + 本文 = 埋め込み入力と一致)。
        // 既存があれば次元・距離尺度が options と一致することを照合する (reopen 時の取り違えを
        // SetVector/検索まで遅延させない)。
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
                SourcePropertyKeyId: searchTextKey,
                Dimensions: _options.EmbeddingDimensions,
                Metric: _options.VectorMetric,
                ProviderId: _options.VectorProviderId));
        }

        // Chunk.searchText 全文索引。作成は EnableFullTextIndex で制御するが、FullTextEnabled は
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
                // 見出し語も BM25 で引けるよう searchText (= 見出しパス + 本文) を索引対象にする。
                schema.CreateFullTextIndex(
                    RagSchema.ChunkTextIndex, RagSchema.ChunkLabel, RagSchema.PropSearchText);
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
