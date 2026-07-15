# Quiver: システム概要

> as-built 仕様 (on-disk FormatVersion V5, 2026-07-08)
>
> **current (as-built)**: 以下は現在実装されている FormatVersion V5 の契約である。
> **target (未実装)**: [Single Writer + Snapshot Readers 抜本再設計](../../plans/single-writer-redesign.md) が将来の設計正本であり、本書の本文はその target を先取りして記述しない。
> **実装済み境界**: identity の kind、Generation、packed representation は現行実装の契約である。
> transaction、WAL、store の再設計は target の段階的な実装対象として残る。

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
│  GraphDatabase (facade)                         │
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
│  ├─ ARIES WAL + recovery                        │
│  └─ Checkpointer                                │
├─────────────────────────────────────────────────┤
│  Storage Engine                                 │
│  ├─ PagedFile (8 KB pages, Clock buffer pool)   │
│  ├─ SingleFileContainer (*.quiver)              │
│  ├─ NodeStore / RelationshipStore / PropertyStore│
│  ├─ HyperedgeStore / IncidenceStore              │
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

`FormatVersion.Current = V5 = 5`。
V2 は自己記述 vector catalog / per-index HNSW レイアウト、V3 は第一級ハイパーエッジ用の
ID kind、type / role token 空間、固定 tenant 18–25 の予約、V4 は incidence の
fixed-slot 直接アドレスレイアウト、V5 は relationship delta の head sidecar と
append-only page store 用固定 tenant 27–28 を導入した clean break である。
自動マイグレーションは行わない。
異なるフォーマットバージョンのデータベースを開くと `FormatVersionMismatchException` をスローする。

## 非目標 {#non-goals}

- サーバプロセス / ネットワークプロトコル
- 分散 / シャーディング構成
- SQL クエリ言語
- オンディスクフォーマット変更に対する自動スキーママイグレーション（1.0 以前）
