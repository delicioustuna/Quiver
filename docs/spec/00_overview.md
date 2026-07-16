# Quiver: システム概要

> as-built 仕様（QUIVER-SW family version 1、2026-07-15）
>
> **current (as-built)**: identity と QUIVER-SW format/WAL foundation は再設計 Wave 2 の契約である。
> Single Writer + Snapshot Readers の後続 wave は [再設計正本](../../plans/single-writer-redesign.md) に従って段階的に実装する。

## ポジショニング {#positioning}

Quiver は .NET 向けの **pure C# 組み込み (in-process) グラフ + ベクトル + 全文検索データベースエンジン** である。
比較軸は SQLite / LiteDB / KuzuDB 系の組み込み DB であり、サーバ規模や分散グラフシステムは物差しにしない。

## 主要ユースケース {#use-case}

**ローカル RAG バックエンド** -- ただしエンジン自体は汎用である。RAG 固有の API は `Quiver.Rag` に置く。

## ゼロ依存テーゼ {#zero-dep}

外部に依存するのは LLM モデルの駆動のみ。それ以外のコンポーネント（ストレージ、WAL、リカバリ、インデックス、
ベクトル検索、全文検索、クエリエンジン）はすべてサードパーティ依存ゼロのマネージド C# でフルスクラッチ実装する。

**Trusted Computing Base (TCB):**

| コンポーネント | 信頼の根拠 |
|---|---|
| .NET BCL | プラットフォームランタイム |
| Claude | 実装者（全コードは人間のレビュー下で AI が記述） |
| LLM プロバイダ | ランタイム依存（埋め込み / 生成） |

マネージド C# は、C/C++ ストレージエンジンが抱えるメモリ安全性の脆弱性クラスを排除する。
テストはセキュリティ統制として機能し、主要な検証メカニズムである。

## アーキテクチャレイヤ {#layers}

```
┌─────────────────────────────────────────────────┐
│  Quiver.Rag / Quiver.Hosting / Quiver.OpenTelemetry │  オプションのアドオン
├─────────────────────────────────────────────────┤
│  QuiverDatabase (facade)                         │
│  ├─ ISchemaApi (labels, indexes, FT indexes)    │
│  ├─ IGraphTransaction (CRUD, index, vector)     │
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
│  ├─ Versioned Vertex / Edge / Nexus stores   │
│  ├─ PropertyVersionStore / payload stores    │
│  ├─ Incidence / AdjacencySegment stores      │
│  ├─ B+Tree indexes                              │
│  ├─ FullTextIndex (postings + norms B+Trees)    │
│  └─ PersistentVectorStore + HNSW                │
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

## ファイルレイアウト {#file-layout}

静止時、Quiver データベースは **単一ファイル** `*.quiver` である。稼働中は WAL サイドカー
`*.quiver-wal` が並んで存在する。クリーンシャットダウン時には WAL は空になるか存在しない。

## フォーマットバージョン {#format-version}

現行のデータファイルと WAL は `QUIVER-SW` family version 1 である。
旧フォーマットを読み替える decoder と自動マイグレーションは提供しない。
旧データベースは `StorageFormatMismatchException`、旧 WAL は `WalFormatMismatchException` で拒否する。

## 非目標 {#non-goals}

- サーバプロセス / ネットワークプロトコル
- 分散 / シャーディング構成
- SQL クエリ言語
- オンディスクフォーマット変更に対する自動スキーママイグレーション（1.0 以前）
