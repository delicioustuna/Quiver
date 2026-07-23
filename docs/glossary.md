# Quiver 用語辞書

Quiver の API やドキュメントに登場する用語を定義する。
内部実装の用語と責務は [as-built 仕様](spec/00_overview.md) と [開発者向け実装 map](design/development.md) を参照。

---

## グラフの基本概念

| 用語 | 定義 |
|---|---|
| **有向プロパティグラフ** | Vertexとエッジにプロパティ（属性）を持つ有向グラフモデル。Neo4j や JanusGraph と同じ基本モデルであり、Quiver もこれを採用する |
| **Vertex（Vertex）** | グラフの頂点。1 つのラベルと複数のプロパティを持つ。`VertexId` で識別される |
| **Edge（Edge）** | グラフの有向辺。1 つの型名と両端Vertex（Source、Target）、複数のプロパティを持つ。`EdgeId` で識別される |
| **Label（ラベル）** | Vertexの分類名（例: `"Person"`）。内部では `LabelId` にトークン化される |
| **Edge Type** | Edgeの分類名（例: `"KNOWS"`）。内部では `EdgeTypeId` にトークン化される |
| **Property（プロパティ）** | Vertex、Edge、またはNexusに付与されるキーバリュー対。値の型は `Bool`、`Int32`、`Int64`、`Double`、`String`、`Bytes`、`FloatArray` |
| **Nexus（Nexus）** | 2 つ以上の任意個のVertexを 1 つの関係として束ねる第一級エンティティ。1 つの型名とロール付きメンバー集合、複数のプロパティを持つ。`NexusId` で識別される。Edgeとは別のエンティティ種別 |
| **Role（ロール）** | Nexusにおけるメンバーの位置づけ（例: `"buyer"`、`"subject"`）。無向のNexusでは方向（Out/In）の代わりにロールフィルタが方向の一般化になる |
| **Member（メンバー）** | Nexusに属すVertex。ロールと `VertexId` の組（`NexusMember`）で表す。メンバー集合は作成時に確定し、以後変更できない（変更は削除 + 再作成） |
| **Arity（アリティ）** | Nexusのメンバー数。2 以上を要求する。Edgeは数学的にはアリティ 2 のNexusの特殊化にあたる |
| **Co-membership** | 同じNexusに属すVertex同士の関係。「起点Vertex → 所属Nexus → 別ロールのメンバー」の 1 論理ホップで辿る。`QuiverDatabaseOptions.CoMembershipRolePairs` にロール対を登録すると物理ビューで高速化される |

## 識別子

| 用語 | 定義 |
|---|---|
| **VertexId** | Vertexの識別子（`readonly record struct`）。`Value` は generation と sequence のパック値で、スロット再利用後も一貫性を保つ |
| **EdgeId** | Edgeの識別子 |
| **NexusId** | Nexusの識別子。VertexId と同じ generation + sequence のパック値 |
| **NexusTypeId** | インターンされたNexus型の識別子。ロール名も同様に独立空間でインターンされる |
| **EntityId** | Vertex、Edge、Nexusを統一的に扱うための ID。`EntityId.FromVertex(id)` で変換する |

## データベースとトランザクション

| 用語 | 定義 |
|---|---|
| **QuiverDatabase** | エンジンのエントリポイント。`QuiverDatabase.Open(path)` で `*.quiver` ファイルを開く。スレッドセーフであり、プロセスのライフタイムを通じて 1 インスタンスを共有する |
| **IReadTransaction** | snapshot 固定の読み取り、`Query`、スキーマ参照を提供する公開インタフェース |
| **IWriteTransaction** | 読み取り能力に加えて mutation、`Mutate`、スキーマ編集、commit、rollback を提供する公開インタフェース |
| **Commit** | WAL を `fsync` した時点で永続化が確定する。返った後はプロセスの kill や電源喪失を生き延びる |
| **Snapshot Isolation** | Quiver の分離レベル。各トランザクションは開始時の一貫したスナップショットを見る。リーダはライタをブロックせず、ライタもリーダをブロックしない |
| **Savepoint** | トランザクション内の中間地点。`RollbackTo(SavepointId)` でセーブポイント以降の変更だけを巻き戻せる |
| **単一ライタ** | 書き込みトランザクションは同時に 1 つだけ進行できる。データベース内の writer gate が直列化する |
| **同時使用不可** | 同じトランザクションハンドル、カーソル、列挙子は複数の操作フローから同時に使用できない |

## クエリと走査

| 用語 | 定義 |
|---|---|
| **Traversal** | Gremlin 風の Fluent API でグラフを辿る操作。`tx.Query` を起点に論理プランを組み立て、終端ステップで実行する |
| **Hop（ホップ）** | トラバーサルにおける 1 段階の隣接Vertex移動 |
| **Expand** | あるVertexから隣接Edgeを辿って隣接Vertexを列挙する操作。`Out()`、`In()`、`Both()` に対応する |
| **Match DSL** | Cypher の `MATCH` に相当する宣言的パターンマッチ構文。`g.Match(GraphPattern.Vertex(...).Out(...))` のように使う |
| **P（述語）** | フィルタ述語のファクトリクラス。`P.Eq(v)`、`P.Gt(v)`、`P.Lt(v)`、`P.Between(a,b)`、`P.StartsWith(s)` 等を提供する |
| **AsCursor / AsEnumerable** | ストリーミング実行の終端ステップ。大量結果をメモリを抑えて逐次処理する |
| **MERGE** | 既存Vertexがあれば取得、なければ新規作成する冪等操作。`tx.MergeVertex(label, matchKey, matchValue)` で使う |
| **Nexuses / Members / OtherMembers** | Nexus走査のトラバーサルステップ。`Nexuses(type?, role?)` はVertexから所属Nexusへ、`Members(role?)` はメンバーVertexへ展開する。`OtherMembers(role?)` は起点Vertex自身を除外する co-membership |
| **NexusBuilder** | `g.AddNexus(type)` が返す作成ビルダ。`.Member(role, vertexId)` を複数回呼び、`.P(...)` でプロパティを積み、`.Next()` で確定する |
| **NexusPattern** | Match DSL の星型パターン。`GraphPattern.Nexus("f", "Fact").Member("subject", ...)` のように 1 つのNexusと複数のロール付きメンバーを同じ結果行へ束縛する |

## Source Generator

| 用語 | 定義 |
|---|---|
| **[Vertex]** | Vertexモデルクラスに付与する属性。`Insert`、`Load`、`Update`、`Delete` メソッドが自動生成される |
| **[Edge]** | Edgeモデルクラスに付与する属性。`Edge<TSource, TTarget>` でエンドポイント型を指定する |
| **[Property]** | グラフプロパティとして永続化するメンバに付与する属性 |
| **[Indexed]** | B+Tree インデックスを自動作成する属性。`[Property]` と併用すると `FindBy{PropName}` メソッドが生成される |
| **[Nexus]** | Nexusモデルクラスに付与する属性。`Insert`、`Load`、`Update`（プロパティのみ）、`Delete` と、ロールごとの型保存トラバーサル糖衣が自動生成される |
| **[Role]** | Nexusのロールを宣言するプロパティ属性。型は `GraphVertexRef<TVertex>`（複数メンバーは `IReadOnlyList<GraphVertexRef<TVertex>>`、省略可能ロールは nullable）で参照先Vertex型を表す |
| **GraphVertexRef&lt;TVertex&gt;** | Vertex CLR 型を保ったまま `VertexId` を保持する参照。`VertexId` からの暗黙変換を持ち、ロールへの型不一致の代入はコンパイルエラーになる |

## インデックス

| 用語 | 定義 |
|---|---|
| **B+Tree インデックス** | プロパティの完全一致検索と範囲検索に使う導出索引。`[Indexed]` 属性または `ISchemaEditor.CreateIndex` で作成する |
| **FullTextIndexDefinition（全文インデックス定義）** | `PropertyTarget`、tokenizer/filter、BM25 parameter、segment policyを統一schema catalogへ保存する定義 |
| **全文 segment** | full typed owner identity、`PropertyVersionRef`、posting、norm、stats、tombstoneを保持するimmutable derived artifact |

## 全文検索

| 用語 | 定義 |
|---|---|
| **BM25** | Okapi BM25。TF-IDF の改良版として広く使われるスコアリング関数（k1=1.2, b=0.75） |
| **Postings** | term から文書 ID へのマッピング。転置インデックスの本体 |
| **Norms** | 文書長の正規化値。BM25 の長さ正規化に使用する |
| **WAND (Weighted AND)** | Top-k 検索の早期終了アルゴリズム。term ごとの寄与上限を用いて候補をスキップする |
| **MixedBigramTokenizer** | Quiver のデフォルトトークナイザ。CJK 文字は bigram 分解し、Latin 文字は空白区切りで小文字化する。2 つのモードを持つ（下記参照） |
| **ユニグラム併用モード** | デフォルト（`mixed-bigram-unigram-v1`）。CJK ランのバイグラムに加えて各文字のユニグラムも放出する。1 文字の CJK 検索クエリが隣接文字に関わらずヒットする |
| **バイグラム専用モード** | `mixed-bigram-v1`。CJK はバイグラムのみ。インデックスサイズが小さい代わりに 1 文字検索はプレフィクス展開（`粉*`）で代替する。`FullTextIndexDefinition.TokenizerId` で明示指定する |
| **RRF (Reciprocal Rank Fusion)** | 複数のランク付きリストをマージするスコア融合手法。全文検索とベクトル検索のハイブリッド結合に使用する |

## ベクトル検索

| 用語 | 定義 |
|---|---|
| **KNN (K-Nearest Neighbors)** | クエリベクトルに最も近い k 件を返す近傍探索 |
| **HNSW (Hierarchical Navigable Small World)** | 近似最近傍探索のためのグラフベースインデックス。Quiver では read snapshot から immutable artifact を構築する |
| **VectorMetric** | 距離関数の種類。`Euclidean`、`Cosine`、`Dot` から選択する |
| **VectorIndexDefinition** | ベクトルインデックスの定義。対象 property、scope、次元数、metric、segment policy を指定する |
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
| **RagStore** | `Quiver.Rag` のエントリポイント。文書取込（`UpsertDocumentAsync`）とハイブリッド検索（`SearchAsync`）を提供する |
| **IngestedDocument** | 取込契約。`SourceId`（一意キー）、`Title`、`Metadata`、`Blocks`（正規化ブロック列）を持つ |
| **IChunkEmbedder** | チャンクテキストからベクトル埋め込みを生成するインタフェース。実装はアプリケーション側が注入する |
| **Graph Expansion** | ヒットしたチャンクから `NEXT_CHUNK`、`HAS_CHUNK` を辿って前後文脈や親文書を復元する機能。ベクトル DB が返せるのはヒット単体だけだが、Quiver はグラフ走査で文脈を復元できる |
| **n 項ファクト（Fact パターン）** | 主体（subject）、客体（object）、出典（source = Chunk）、時点（asOf）などのロールを持つNexusで知識を表す利用パターン。出典がファクトのメンバーとして構造的に付随するため、回答生成時の出典引用（grounded citation）を join なしで取れる。実例は `samples/Quiver.Samples.Nexuses/` |

## パッケージ

| パッケージ | 役割 |
|---|---|
| **Quiver** | エンジン中核。全サブシステムと型付き属性（`Quiver.Api`）、Source Generator を内包する。これ 1 つの参照で型安全 CRUD まで使える |
| **Quiver.Rag** | RAG レイヤ。Document/Chunk スキーマ、文書取込、ハイブリッド検索 + graph expansion |
| **Quiver.Hosting** | `Microsoft.Extensions.Hosting` / DI 統合 |
| **Quiver.OpenTelemetry** | OpenTelemetry 計装登録 |

## ファイルとフォーマット

| 用語 | 定義 |
|---|---|
| **\*.quiver** | Quiver のデータファイル。静止時は単一ファイルにすべてのデータが格納される |
| **\*.quiver-wal** | WAL（Write-Ahead Log）サイドカー。稼働中にのみ存在し、クリーンシャットダウン後は空か不在になる |
| **WAL** | データファイルへの書き込みに先立ってログを書くことで、クラッシュリカバリを保証する仕組み |
| **QUIVER-SW family version** | データファイルと WAL が共有するオンディスク形式の世代。現在は version 2。旧 DB は `StorageFormatMismatchException`、旧 WAL は `WalFormatMismatchException` で拒否する |

## メンテナンス

| 用語 | 定義 |
|---|---|
| **Vacuum** | 論理削除されたレコードのスロットを回収し、再利用可能にする操作 |
| **Migration** | スキーマやデータの変更を段階的に適用する仕組み。`IMigration` を実装して `Migrator` に登録する |
| **ConsistencyCheck** | `IDiagnosticsApi` が提供するデータベース整合性検証。孤立レコードやインデックス不整合を検出し修復する |
| **BulkLoader** | 大量データの一括挿入に特化した経路。通常 TX 比 ~12× 高速。隣接インデックスの構築オプション付き |
