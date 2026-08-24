# Yatagarasu アーキテクチャ図

用途別の Mermaid 図集。
内部実装の契約は[現行仕様](spec/00_overview.md)を参照。

---

## 1. レイヤ概要

エンジン中核の内部レイヤと、オプションパッケージの依存関係を示す。

```mermaid
flowchart TB
    subgraph optional["オプションパッケージ"]
        Rag["Yatagarasu.Rag<br/>RAG レイヤ"]
        Hosting["Yatagarasu.Hosting<br/>DI 統合"]
        OTel["Yatagarasu.OpenTelemetry<br/>計装"]
    end

    subgraph core["Yatagarasu（エンジン中核）"]
        API["YatagarasuDatabase / IReadTransaction / IWriteTransaction<br/>Fluent Traversal / Match DSL"]
        Query["Query Engine"]
        TxMgr["Transaction Manager<br/>MVCC / Checkpoint"]
        Index["Scalar B+Tree / Immutable Full-Text Segments"]
        Records["VertexStore / EdgeStore<br/>PropertyStore"]
        Vector["Vector property + immutable HNSW segments"]
        WAL["Write-Ahead Log"]
        Storage["PagedFile / バッファプール"]
    end

    File[("*.yata<br/>+ *.yata-wal")]

    Rag --> API
    Hosting --> API
    OTel --> API

    API --> Query
    API --> TxMgr
    Query --> TxMgr
    TxMgr --> WAL
    TxMgr --> Records
    TxMgr --> Index
    TxMgr --> Vector
    Records --> Storage
    Index --> Storage
    Vector --> Storage
    WAL --> File
    Storage --> File
```

---

## 2. ハイブリッド検索

BM25 全文検索と KNN ベクトル検索を RRF で統合し、Graph Expansion で文脈を復元する。

```mermaid
flowchart TB
    Query["検索クエリ"]
    FTS["BM25 全文検索<br/>WAND top-k"]
    KNN["KNN ベクトル検索<br/>HNSW top-k"]
    RRF["RRF<br/>Reciprocal Rank Fusion"]
    GE["Graph Expansion<br/>NEXT_CHUNK / HAS_CHUNK"]
    Result["RagHit[]<br/>チャンク + 文脈 + 親文書"]

    Query --> FTS
    Query --> KNN
    FTS --> RRF
    KNN --> RRF
    RRF --> GE
    GE --> Result
```

---

## 3. 文書取込（Yatagarasu.Rag）

外部アプリが生成した正規化ブロック列を `RagStore` が受け取り、グラフに格納する。

```mermaid
sequenceDiagram
    participant Ext as 取込アプリ
    participant Rag as RagStore
    participant Chk as Chunker
    participant Emb as IChunkEmbedder
    participant DB as IWriteTransaction

    Ext->>Rag: UpsertDocumentAsync(IngestedDocument)
    Rag->>Rag: corpus profile と document fingerprint を検証
    alt fingerprint が完全一致
        Rag-->>Ext: Unchanged（DB変更なし）
    else Blocks は同じで属性だけ変更
        Rag->>DB: title / metadata / revision 更新
        Rag->>DB: Commit
        Rag-->>Ext: AttributesUpdated
    else Blocks が変更
        Rag->>Chk: ブロック列をチャンク分割
        Rag->>Emb: EmbedAsync(チャンクテキスト[])
        Emb-->>Rag: float[][]
        Rag->>DB: Document / Chunk / relation / vector を置換
        Rag->>DB: Commit
        Rag-->>Ext: Created または Replaced
    end
```

---

## 4. RAG スキーマ

`Yatagarasu.Rag` が構築するグラフ構造を示す。

```mermaid
flowchart LR
    Profile["RagIngestionProfile<br/>profileFingerprint"]
    Doc["Document<br/>sourceId, title, contentHash,<br/>ingestionFingerprint, metadataJson"]
    C1["Chunk #1<br/>text, ordinal"]
    C2["Chunk #2"]
    C3["Chunk #3"]
    VecIdx[("ベクトルインデックス<br/>rag_chunk_embedding")]
    FtsIdx[("全文インデックス<br/>idx_rag_chunk_text")]
    MetaIdx[("任意 metadata scalar index")]

    Profile -. corpus contract .-> Doc
    Doc -- HAS_CHUNK --> C1
    Doc -- HAS_CHUNK --> C2
    Doc -- HAS_CHUNK --> C3
    C1 -- NEXT_CHUNK --> C2
    C2 -- NEXT_CHUNK --> C3
    C1 -.- VecIdx
    C2 -.- VecIdx
    C3 -.- VecIdx
    C1 -.- FtsIdx
    Doc -. opt-in .- MetaIdx
    C2 -.- FtsIdx
    C3 -.- FtsIdx
```

---

## 5. パッケージ依存関係

NuGet パッケージとしての依存グラフ。矢印はパッケージ参照の向きを示す。

```mermaid
flowchart LR
    SG["Yatagarasu.SourceGen<br/>(analyzer 同梱)"]
    Core["Yatagarasu<br/>（エンジン中核）"]
    Rag["Yatagarasu.Rag"]
    Host["Yatagarasu.Hosting"]
    OTel["Yatagarasu.OpenTelemetry"]
    SG -- analyzer --> Core
    Rag --> Core
    Host --> Core
    OTel --> Core
```
