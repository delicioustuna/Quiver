# Yatagarasu: システム概要

> as-built 仕様（QUIVER-SW family version 2、2026-08-03）
>
> **current (as-built)**: 不透明keyの公開境界、Single Writer + Snapshot Readers、no-steal page-WAL、redo-only recovery、統一スカラ索引、immutable vector/full-text segment、horizon-aware maintenance、callback/session境界付きqueryを実装している。

## ポジショニング {#positioning}

Yatagarasu は .NET 向けの **pure C# 組み込み (in-process) グラフ + ベクトル + 全文検索データベースエンジン** である。
比較軸は SQLite / LiteDB / KuzuDB 系の組み込み DB であり、サーバ規模や分散グラフシステムは物差しにしない。

## 主要ユースケース {#use-case}

**ローカルRAGバックエンド**を主要ユースケースとする。
エンジン自体は汎用とし、RAG固有のAPIは`Yatagarasu.Rag`に置く。

## ゼロ依存テーゼ {#zero-dep}

コアエンジンが実行時に依存するのは.NET BCLだけである。
ストレージ、WAL、リカバリ、インデックス、ベクトル検索、全文検索、クエリエンジンは、サードパーティ依存のないマネージドC#で実装する。
埋め込み生成とLLM呼び出しはアプリケーション側の責務とし、`Yatagarasu.Rag`にはプロバイダを注入する。

## アーキテクチャレイヤ {#layers}

```
┌─────────────────────────────────────────────────┐
│  Yatagarasu.Rag / Yatagarasu.Hosting / Yatagarasu.OpenTelemetry │  オプションのアドオン
├─────────────────────────────────────────────────┤
│  GraphStore / GraphWorkspace (public facade)     │
│  ├─ Read / Write callback scope                 │
│  ├─ Advanced read / write session               │
│  ├─ GraphQuery / Match                          │
│  ├─ typed Source Generator mapper               │
│  └─ opaque key / owned GraphValue               │
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
│  ├─ SingleFileContainer (*.yata)              │
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
| `Yatagarasu` | エンジン中核（単一アセンブリ、全サブシステム）+ 型付き属性（`Yatagarasu.Api`） |
| `Yatagarasu.SourceGen` | 型付きグラフモデル向け Roslyn ソースジェネレータ（`Yatagarasu` に analyzer として同梱） |
| `Yatagarasu.Rag` | ローカル RAG レイヤ（Document/Chunk スキーマ、取り込み、ハイブリッド検索 + グラフ展開） |
| `Yatagarasu.Hosting` | `Microsoft.Extensions.Hosting` 連携（DI） |
| `Yatagarasu.OpenTelemetry` | OpenTelemetry エクスポート |

## RAG 利用者契約 {#rag-contract}

`Yatagarasu.Rag` は Document と Chunk の取込、BM25 と vector の融合検索、graph expansion を提供する。
`MetadataEquals` は一致文書の Chunk を scorer の候補集合へ渡し、全文と vector の top-k を候補集合内で確定する。
後段 filter と oversampling を正しさの前提にしない。

取込はコーパス単位の `RagIngestionProfile` を持つ。chunking profile と数値設定、embedding profile、
normalization profile、embedding input template、vector 次元・距離尺度・index 名を決定的な fingerprint にし、
全文 token filter 構成も同じ fingerprint に含める。
初回初期化時に `RagIngestionProfile` Vertex へ記録する。同じ DB を異なる profile で開くと
`RagIngestionProfileMismatchException` を schema 変更前に送出し、異なる embedding semantics を同じ index へ混在させない。
profile marker のない旧 RAG コーパスは既定で拒否し、使用済み profile を呼び出し側が特定できる場合だけ
`AdoptLegacyIngestionProfile` で明示採用する。この採用は metadata property を backfill しないため、
`MetadataIndexes` との同時指定は拒否する。

Document の `ingestionFingerprint` は profile fingerprint、Blocks の `contentHash`、title、キー順を正規化した metadata、
任意の `ContentRevision` から算出する。全 fingerprint が一致すれば no-op、`contentHash` だけが一致して
title、metadata、revision が変わった場合は同じ Document ID と Chunk / vector を保ったまま属性だけを更新する。
Blocks が変わった場合だけ旧 Document と Chunk を置き換えて再埋め込みする。

metadata は既定では `metadataJson` の Document label scan で絞る。`RagStoreOptions.MetadataIndexes` に利用者が
選んだ string key を登録すると、その値を独立 property と `StringEquality` scalar index へ昇格し、
`MetadataEquals` は該当 index の積集合から候補文書を始める。昇格しないキーと最終一致確認は
`metadataJson` を正本として扱う。

全文 token filter は既定で空である。日本語異字体を機械的に展開する場合は
`RagStoreOptions.FullTextFilters` に `JapaneseOrthographicVariantFilter` を明示し、既定の原文保持と
利用者が要求した検索時展開を索引単位で切り替える。設定変更は既存 corpus へ暗黙適用せず profile 不一致として拒否する。

`RagHit.Score` は BM25 score、vector similarity、融合後 score、融合方式、RRF の rank 定数を返す。
片方の検索チャンネルだけを使う場合は、使わない側の生 score を `null` にする。

Blocks の内容変更による upsert は Document ID を維持しない。
旧 Document、Chunk、旧 ID に接続した Edge、旧 ID が参加した Nexus を同じ logical delete 境界で削除する。
`UpsertResult.Disposition` は `Created`、`Unchanged`、`AttributesUpdated`、`Replaced` を区別する。
`Replaced` は旧`VertexKey`と新`VertexKey`の対応を返し、利用者が所有する関係だけを明示的に再アンカーできるようにする。
旧 ID の利用者関係を新 ID へ暗黙継承しない。

`Chunker`のサイズ設定と`CharStart` / `CharEnd`はUTF-16コード単位である。
段落には`TargetSize`を使い、表とコードの強制分割には`MaxChunkSize`を使う。
対象範囲内に空白境界があれば、その位置まで戻って分割する。句読点や文境界への特別な調整は行わない。
有効なサロゲートペアは分割しない。サイズが1でも、1つのスカラー値が2コード単位なら長さ2の単独チャンクを許す。
段落で重なりを持たせる場合、開始位置を語境界・スカラー値の境界まで前進させ、重なり幅を設定値以下に保つ。
結合文字、異体字セレクタ、ゼロ幅接合子でつながる文字列などの書記素クラスタ全体の保持は保証しない。
不正なUTF-16入力の孤立サロゲートは置換せず保持し、入力中の有効なペアを新たに分断しない。

## ファイルレイアウト {#file-layout}

primary state は `*.yata` に格納する。
稼働中は WAL サイドカー `*.yata-wal` が並び、クリーンシャットダウン時には空になるか存在しない。
全文索引を使う場合は、manifest が参照する immutable artifact を `*.yata-ftseg/` ディレクトリへ個別ファイルとして格納する。
snapshot は primary file、WAL、参照可能な artifact directory を一組として複製する。

## フォーマットバージョン {#format-version}

現行のデータファイルと WAL は `QUIVER-SW` family version 2 である。
v0.5.0も同じfamily version 2であり、同releaseが作成した固定fixtureは現行buildでopen、更新、再openできる。
旧フォーマットを読み替える decoder と自動マイグレーションは提供しない。
旧データベースは `StorageFormatMismatchException`、旧 WAL は `WalFormatMismatchException` で拒否する。
page / WAL headerの予約extensionと未知WAL recordは読み飛ばさず、元のdatabaseとWALを書き換える前に拒否する。

## 非目標 {#non-goals}

- サーバプロセス / ネットワークプロトコル
- 分散 / シャーディング構成
- SQL クエリ言語
- オンディスクフォーマット変更に対する自動スキーママイグレーション（1.0 以前）
