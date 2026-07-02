# Quiver アーキテクチャ図

用途別の Mermaid 図集。
内部実装の図（書き込みフロー、リカバリ、クエリパイプライン、MVCC 可視性）は [内部実装の図](design/internals-diagrams.md) を参照。

---

## 1. レイヤ概要

エンジン中核の内部レイヤと、オプションパッケージの依存関係を示す。

```mermaid
flowchart TB
    subgraph optional["オプションパッケージ"]
        Rag["Quiver.Rag<br/>RAG レイヤ"]
        Hosting["Quiver.Hosting<br/>DI 統合"]
        OTel["Quiver.OpenTelemetry<br/>計装"]
    end

    subgraph core["Quiver（エンジン中核）"]
        API["GraphDatabase / GraphTransaction<br/>Fluent Traversal / Match DSL"]
        Query["Query Engine"]
        TxMgr["Transaction Manager<br/>MVCC / Checkpoint"]
        Index["B+Tree / FullTextIndex"]
        Records["NodeStore / RelationshipStore<br/>PropertyStore"]
        Vector["PersistentVectorStore + HNSW"]
        WAL["Write-Ahead Log"]
        Storage["PagedFile / バッファプール"]
    end

    File[("*.quiver<br/>+ *.quiver-wal")]

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

## 3. 文書取込（Quiver.Rag）

外部アプリが生成した正規化ブロック列を `RagStore` が受け取り、グラフに格納する。

```mermaid
sequenceDiagram
    participant Ext as 取込アプリ
    participant Rag as RagStore
    participant Chk as Chunker
    participant Emb as IChunkEmbedder
    participant DB as GraphTransaction

    Ext->>Rag: IngestAsync(IngestedDocument)
    Rag->>Chk: ブロック列をチャンク分割
    Rag->>Emb: EmbedAsync(チャンクテキスト[])
    Emb-->>Rag: float[][]
    Rag->>DB: Document ノード作成
    Rag->>DB: Chunk ノード N 件作成
    Rag->>DB: HAS_CHUNK / NEXT_CHUNK 作成
    Rag->>DB: 全文インデックス登録 + ベクトル登録
    Rag->>DB: Commit
```

---

## 4. RAG スキーマ

`Quiver.Rag` が構築するグラフ構造を示す。

```mermaid
flowchart LR
    Doc["Document<br/>sourceId, title,<br/>contentHash"]
    C1["Chunk #1<br/>text, ordinal"]
    C2["Chunk #2"]
    C3["Chunk #3"]
    VecIdx[("ベクトルインデックス<br/>rag_chunk_embedding")]
    FtsIdx[("全文インデックス<br/>idx_rag_chunk_text")]

    Doc -- HAS_CHUNK --> C1
    Doc -- HAS_CHUNK --> C2
    Doc -- HAS_CHUNK --> C3
    C1 -- NEXT_CHUNK --> C2
    C2 -- NEXT_CHUNK --> C3
    C1 -.- VecIdx
    C2 -.- VecIdx
    C3 -.- VecIdx
    C1 -.- FtsIdx
    C2 -.- FtsIdx
    C3 -.- FtsIdx
```

---

## 5. パッケージ依存関係

NuGet パッケージとしての依存グラフ。破線は incubating（NuGet 非公開）を示す。

```mermaid
flowchart LR
    SG["Quiver.SourceGen<br/>(analyzer 同梱)"]
    Core["Quiver<br/>（エンジン中核）"]
    Rag["Quiver.Rag"]
    Host["Quiver.Hosting"]
    OTel["Quiver.OpenTelemetry"]
    Emb["Quiver.Embedding<br/>(incubating)"]

    SG -- analyzer --> Core
    Core --> Rag
    Core --> Host
    Core --> OTel
    Core --> Emb

    style Emb stroke-dasharray: 5 5
```
