# Quiver: システム概要

> as-built 仕様（QUIVER-SW family version 2、2026-07-19）
>
> **current (as-built)**: identity、Single Writer + Snapshot Readers、no-steal page-WAL、redo-only recovery、統一スカラ索引、immutable vector/full-text segment、horizon-aware maintenance、トランザクション境界付き query を実装している。

## ポジショニング {#positioning}

Quiver は .NET 向けの **pure C# 組み込み (in-process) グラフ + ベクトル + 全文検索データベースエンジン** である。
比較軸は SQLite / LiteDB / KuzuDB 系の組み込み DB であり、サーバ規模や分散グラフシステムは物差しにしない。

## 主要ユースケース {#use-case}

**ローカルRAGバックエンド**を主要ユースケースとする。
エンジン自体は汎用とし、RAG固有のAPIは`Quiver.Rag`に置く。

## ゼロ依存テーゼ {#zero-dep}

コアエンジンが実行時に依存するのは.NET BCLだけである。
ストレージ、WAL、リカバリ、インデックス、ベクトル検索、全文検索、クエリエンジンは、サードパーティ依存のないマネージドC#で実装する。
埋め込み生成とLLM呼び出しはアプリケーション側の責務とし、`Quiver.Rag`にはプロバイダを注入する。

## アーキテクチャレイヤ {#layers}

```
┌─────────────────────────────────────────────────┐
│  Quiver.Rag / Quiver.Hosting / Quiver.OpenTelemetry │  オプションのアドオン
├─────────────────────────────────────────────────┤
│  QuiverDatabase (facade)                         │
│  ├─ ISchemaCatalog / ISchemaEditor              │
│  ├─ IReadTransaction / IWriteTransaction        │
│  ├─ GraphTraversalSource / GraphMutationSource  │
│  ├─ IDiagnosticsApi (consistency check, repair) │
│  └─ Logical mutation sink (audit / replication) │
├─────────────────────────────────────────────────┤
│  Query Engine                                   │
│  ├─ Logical IR + Optimizer                      │
│  ├─ Volcano physical operators                  │
│  └─ GraphKernel (BFS/DFS/shortest-path)         │
├─────────────────────────────────────────────────┤
│  Transaction Manager                            │
│  ├─ MVCC (snapshot isolation)                   │
│  ├─ QUIVER-SW page WAL + Commit-only recovery   │
│  └─ Checkpointer                                │
├─────────────────────────────────────────────────┤
│  Storage Engine                                 │
│  ├─ PagedFile (8 KB pages, Clock buffer pool)   │
│  ├─ SingleFileContainer (*.quiver)              │
│  ├─ Versioned Vertex / Edge / Nexus stores      │
│  ├─ PropertyVersionStore / payload stores       │
│  ├─ Incidence / AdjacencySegment stores         │
│  ├─ B+Tree indexes                              │
│  ├─ Immutable full-text segments + BM25/WAND    │
│  └─ Immutable vector segments + HNSW artifacts  │
└─────────────────────────────────────────────────┘
```

## アセンブリ {#assemblies}

| アセンブリ | 役割 |
|---|---|
| `Quiver` | エンジン中核（単一アセンブリ、全サブシステム）+ 型付き属性（`Quiver.Api`） |
| `Quiver.SourceGen` | 型付きグラフモデル向け Roslyn ソースジェネレータ（`Quiver` に analyzer として同梱） |
| `Quiver.Rag` | ローカル RAG レイヤ（Document/Chunk スキーマ、取り込み、ハイブリッド検索 + グラフ展開） |
| `Quiver.Hosting` | `Microsoft.Extensions.Hosting` 連携（DI） |
| `Quiver.OpenTelemetry` | OpenTelemetry エクスポート |

## RAG 利用者契約 {#rag-contract}

`Quiver.Rag` は Document と Chunk の取込、BM25 と vector の融合検索、graph expansion を提供する。
`MetadataEquals` は一致文書の Chunk を scorer の候補集合へ渡し、全文と vector の top-k を候補集合内で確定する。
後段 filter と oversampling を正しさの前提にしない。

`RagHit.Score` は BM25 score、vector similarity、融合後 score、融合方式、RRF の rank 定数を返す。
片方の検索チャンネルだけを使う場合は、使わない側の生 score を `null` にする。

内容変更による upsert は Document ID を維持しない。
旧 Document、Chunk、旧 ID に接続した Edge、旧 ID が参加した Nexus を同じ logical delete 境界で削除する。
`UpsertResult` は旧 ID と新 ID の対応を返し、利用者が所有する関係だけを明示的に再アンカーできるようにする。
旧 ID の利用者関係を新 ID へ暗黙継承しない。

## ファイルレイアウト {#file-layout}

primary state は `*.quiver` に格納する。
稼働中は WAL サイドカー `*.quiver-wal` が並び、クリーンシャットダウン時には空になるか存在しない。
全文索引を使う場合は、manifest が参照する immutable artifact を `*.quiver-ftseg/` ディレクトリへ個別ファイルとして格納する。
snapshot は primary file、WAL、参照可能な artifact directory を一組として複製する。

## フォーマットバージョン {#format-version}

現行のデータファイルと WAL は `QUIVER-SW` family version 2 である。
旧フォーマットを読み替える decoder と自動マイグレーションは提供しない。
旧データベースは `StorageFormatMismatchException`、旧 WAL は `WalFormatMismatchException` で拒否する。

## 非目標 {#non-goals}

- サーバプロセス / ネットワークプロトコル
- 分散 / シャーディング構成
- SQL クエリ言語
- オンディスクフォーマット変更に対する自動スキーママイグレーション（1.0 以前）
