# Quiver 用語辞書

Quiver の API やドキュメントに登場する用語を定義する。
内部実装の用語は [内部実装の用語](design/internals-glossary.md) を参照。

---

## グラフの基本概念

| 用語 | 定義 |
|---|---|
| **有向プロパティグラフ** | ノードとエッジにプロパティ（属性）を持つ有向グラフモデル。Neo4j や JanusGraph と同じ基本モデルであり、Quiver もこれを採用する |
| **Node（ノード）** | グラフの頂点。1 つのラベルと複数のプロパティを持つ。`NodeId` で識別される |
| **Relationship（リレーションシップ）** | グラフの有向辺。1 つの型名と両端ノード（Source、Target）、複数のプロパティを持つ。`RelationshipId` で識別される |
| **Label（ラベル）** | ノードの分類名（例: `"Person"`）。内部では `LabelId` にトークン化される |
| **Relationship Type** | リレーションシップの分類名（例: `"KNOWS"`）。内部では `RelationshipTypeId` にトークン化される |
| **Property（プロパティ）** | ノードまたはリレーションシップに付与されるキーバリュー対。値の型は `Bool`、`Int32`、`Int64`、`Double`、`String`、`Bytes`、`FloatArray` |

## 識別子

| 用語 | 定義 |
|---|---|
| **NodeId** | ノードの識別子（`readonly record struct`）。`Value` は generation と sequence のパック値で、スロット再利用後も一貫性を保つ |
| **RelationshipId** | リレーションシップの識別子 |
| **EntityId** | ノードとリレーションシップを統一的に扱うための ID。`EntityId.FromNode(id)` で変換する |

## データベースとトランザクション

| 用語 | 定義 |
|---|---|
| **GraphDatabase** | エンジンのエントリポイント。`GraphDatabase.Open(path)` で `*.quiver` ファイルを開く。スレッドセーフであり、プロセスのライフタイムを通じて 1 インスタンスを共有する |
| **IGraphTransaction** | 読み書きトランザクションの公開インタフェース。`CreateNode`、`SetProperty`、`Commit`、`CommitAsync` 等を提供し、`IDisposable` と `IAsyncDisposable` の両方に対応する |
| **Commit / CommitAsync** | WAL を `fsync` した時点で永続化が確定する。どちらも完了後はプロセスの kill や電源喪失を生き延びる。`CommitAsync` は fsync の待機中に呼び出し元スレッドをブロックしない |
| **非同期トランザクション境界** | `BeginTransactionAsync`、`BeginReadOnlyTransactionAsync`、`CommitAsync`、`DisposeAsync` で開始・commit・破棄を async パイプラインへ接続する API。CRUD や走査そのものを非同期 I/O へ変えるものではない |
| **Snapshot Isolation** | Quiver の分離レベル。各トランザクションは開始時の一貫したスナップショットを見る。リーダはライタをブロックせず、ライタもリーダをブロックしない |
| **Savepoint** | トランザクション内の中間地点。`RollbackTo(SavepointId)` でセーブポイント以降の変更だけを巻き戻せる |
| **単一ライタ** | 書き込みトランザクションは同時に 1 つだけ進行できる。`EnforceExclusiveWriter` と `BeginTransactionAsync`、またはアプリケーション側のゲートで直列化する |
| **スレッドアフィン** | トランザクション操作は単一スレッド上で実行する。非同期 API が定義する開始・commit・破棄の境界は await できるが、CRUD の途中に外部 API 等の任意の await を挟んではならない |

## クエリと走査

| 用語 | 定義 |
|---|---|
| **Traversal** | Gremlin 風の Fluent API でグラフを辿る操作。`tx.G(db.Schema)` を起点にメソッドチェーンで論理プランを組み立て、終端ステップで実行する |
| **非同期 traversal 終端** | `ToListAsync`、`CountAsync`、`NextAsync`、`AsAsyncEnumerable` 等。同期 query engine の結果を async パイプラインから扱い、`CancellationToken` による中断と `await foreach` を提供する |
| **Hop（ホップ）** | トラバーサルにおける 1 段階の隣接ノード移動 |
| **Expand** | あるノードから隣接リレーションシップを辿って隣接ノードを列挙する操作。`Out()`、`In()`、`Both()` に対応する |
| **Match DSL** | Cypher の `MATCH` に相当する宣言的パターンマッチ構文。`g.Match(GraphPattern.Node(...).Out(...))` のように使う |
| **P（述語）** | フィルタ述語のファクトリクラス。`P.Eq(v)`、`P.Gt(v)`、`P.Lt(v)`、`P.Between(a,b)`、`P.StartsWith(s)` 等を提供する |
| **AsCursor / AsEnumerable** | ストリーミング実行の終端ステップ。大量結果をメモリを抑えて逐次処理する |
| **MERGE** | 既存ノードがあれば取得、なければ新規作成する冪等操作。`tx.MergeNode(label, matchKey, matchValue)` で使う |

## Source Generator

| 用語 | 定義 |
|---|---|
| **[Node]** | ノードモデルクラスに付与する属性。`Insert`、`Load`、`Update`、`Delete` メソッドが自動生成される |
| **[Relationship]** | リレーションシップモデルクラスに付与する属性。`Relationship<TSource, TTarget>` でエンドポイント型を指定する |
| **[Property]** | グラフプロパティとして永続化するメンバに付与する属性 |
| **[Indexed]** | B+Tree インデックスを自動作成する属性。`[Property]` と併用すると `FindBy{PropName}` メソッドが生成される |

## インデックス

| 用語 | 定義 |
|---|---|
| **B+Tree インデックス** | プロパティの完全一致検索と範囲検索に使うインデックス。`[Indexed]` 属性または `ISchemaApi.CreateIndex` で作成する |
| **FullTextIndex（全文インデックス）** | 転置インデックス（Postings B+Tree + Norms B+Tree）による全文検索機能。BM25 スコアリングを提供する |

## 全文検索

| 用語 | 定義 |
|---|---|
| **BM25** | Okapi BM25。TF-IDF の改良版として広く使われるスコアリング関数（k1=1.2, b=0.75） |
| **Postings** | term から文書 ID へのマッピング。転置インデックスの本体 |
| **Norms** | 文書長の正規化値。BM25 の長さ正規化に使用する |
| **WAND (Weighted AND)** | Top-k 検索の早期終了アルゴリズム。term ごとの寄与上限を用いて候補をスキップする |
| **MixedBigramTokenizer** | Quiver のデフォルトトークナイザ。CJK 文字は bigram 分解し、Latin 文字は空白区切りで小文字化する。2 つのモードを持つ（下記参照） |
| **ユニグラム併用モード** | デフォルト（`mixed-bigram-unigram-v1`）。CJK ランのバイグラムに加えて各文字のユニグラムも放出する。1 文字の CJK 検索クエリが隣接文字に関わらずヒットする |
| **バイグラム専用モード** | `mixed-bigram-v1`。CJK はバイグラムのみ。インデックスサイズが小さい代わりに 1 文字検索はプレフィクス展開（`粉*`）で代替する。`FullTextIndexOptions.TokenizerId` で明示指定する |
| **RRF (Reciprocal Rank Fusion)** | 複数のランク付きリストをマージするスコア融合手法。全文検索とベクトル検索のハイブリッド結合に使用する |

## ベクトル検索

| 用語 | 定義 |
|---|---|
| **KNN (K-Nearest Neighbors)** | クエリベクトルに最も近い k 件を返す近傍探索 |
| **HNSW (Hierarchical Navigable Small World)** | 近似最近傍探索のためのグラフベースインデックス。Quiver では `*.quiver` ファイル内にページベースで永続化する |
| **VectorMetric** | 距離関数の種類。`Euclidean`、`Cosine`、`Dot` から選択する |
| **VectorIndexSpec** | ベクトルインデックスの定義。次元数、メトリクス、対象 EntityKind を指定する |
| **graph-first ハイブリッド** | グラフフィルタ（トラバーサル）を先に評価し、絞り込んだ候補集合に対して KNN を実行するパターン |

## ダイアディック演算

| 用語 | 定義 |
|---|---|
| **IDyadicOperator** | ユーザー定義の二項演算インタフェース。2 つの `ReadOnlySpan<float>` を受け取りスカラースコアを返す |
| **ApplyDyadic** | graph-first で候補を絞った後、カスタム二項演算でランク付けするトラバーサルステップ |
| **FloatArray** | `PropertyValueType.FloatArray`。`float[]` をプロパティ値として格納する型。`[Property]` として扱える |

## RAG (Retrieval-Augmented Generation)

| 用語 | 定義 |
|---|---|
| **RAG** | 検索拡張生成。外部知識を検索して LLM のプロンプトに注入する手法 |
| **RagStore** | `Quiver.Rag` のエントリポイント。文書取込（`IngestAsync`）とハイブリッド検索（`SearchAsync`）を提供する |
| **IngestedDocument** | 取込契約。`SourceId`（一意キー）、`Title`、`Metadata`、`Blocks`（正規化ブロック列）を持つ |
| **IChunkEmbedder** | チャンクテキストからベクトル埋め込みを生成するインタフェース。実装はアプリケーション側が注入する |
| **Graph Expansion** | ヒットしたチャンクから `NEXT_CHUNK`、`HAS_CHUNK` を辿って前後文脈や親文書を復元する機能。ベクトル DB が返せるのはヒット単体だけだが、Quiver はグラフ走査で文脈を復元できる |

## パッケージ

| パッケージ | 役割 |
|---|---|
| **Quiver** | エンジン中核。全サブシステムと型付き属性（`Quiver.Api`）、Source Generator を内包する。これ 1 つの参照で型安全 CRUD まで使える |
| **Quiver.Rag** | RAG レイヤ。Document/Chunk スキーマ、文書取込、ハイブリッド検索 + graph expansion |
| **Quiver.Hosting** | `Microsoft.Extensions.Hosting` / DI 統合 |
| **Quiver.OpenTelemetry** | OpenTelemetry 計装登録 |
| **Quiver.Embedding** | テキスト埋め込みパイプライン（incubating、NuGet 非公開） |

## ファイルとフォーマット

| 用語 | 定義 |
|---|---|
| **\*.quiver** | Quiver のデータファイル。静止時は単一ファイルにすべてのデータが格納される |
| **\*.quiver-wal** | WAL（Write-Ahead Log）サイドカー。稼働中にのみ存在し、クリーンシャットダウン後は空か不在になる |
| **WAL** | データファイルへの書き込みに先立ってログを書くことで、クラッシュリカバリを保証する仕組み |
| **FormatVersion** | オンディスクフォーマットのバージョン（現在 V1）。不一致時は `FormatVersionMismatchException` がスローされる |

## メンテナンス

| 用語 | 定義 |
|---|---|
| **Vacuum** | 論理削除されたレコードのスロットを回収し、再利用可能にする操作 |
| **Migration** | スキーマやデータの変更を段階的に適用する仕組み。`IMigration` を実装して `Migrator` に登録する |
| **ConsistencyCheck** | `IDiagnosticsApi` が提供するデータベース整合性検証。孤立レコードやインデックス不整合を検出し修復する |
| **BulkLoader** | 大量データの一括挿入に特化した経路。通常 TX 比 ~12× 高速。隣接インデックスの構築オプション付き |
