# HYP トラック 親計画書 (ハイパーエッジ第一級対応)

> 起案日: 2026-07-02。develop ブランチで仮説検証イテレーションを回す (ロールバック許容、main が NuGet 公開対象)。
> 本ファイルはトラックの経緯・決定事項・命名体系・タスク列を記録する親計画書。
> 実装確定後の as-built 仕様は docs/spec/ に追記する。
> 実装増分、spike、検証ゲート、完了条件の詳細は `plans/hyperedge-implementation-tasks.md` を正本とする。

## 経緯と決定事項

### 実現可能性議論 (2026-07-02)

binary edge (2 項) を前提に焼き付いているのはトポロジー層 3 箇所のみ
(RelationshipStore の固定レコード + Src/Tgt チェーン、GraphKernel の
`VisitNeighbor(source, target, relId)` 契約、DSL/Match の Out/In 語彙)。
ページング・WAL/ARIES・MVCC サイドカー・PropertyStore・B+Tree・FTS・
ベクトル (`(EntityKind, entityId)` キー) はすべてエンティティ汎用で流用可能。
新規 DB を起こす理由はなく、Quiver 拡張で実現する。

- **案A (reification: ハイパーエッジをノード + MEMBER エッジで具象化) は採らず、
  案B (incidence 表現の第一級ハイパーエッジ) へ直行** (ユーザ決定)
- **意味論はロール付き incidence モデル** — 無向 (集合)・有向 (head/tail)・
  n 項ロール付きリレーション (TypeDB 風) をロールで包摂する
- **メンバー集合は作成時確定 (immutable-first)** — 変更は delete + 再作成。
  `AddMember` / `RemoveMember` は将来用に名前のみ予約
- **クリーンブレイク** — 未リリースにつきフォーマット互換・マイグレーションは実装しない (既定方針)

### 命名再調査 (2026-07-02、ユーザ承認済み)

主要システム実地調査: XGI (`edges.members()` / `nodes.memberships()`)、
HyperNetX / HIF 交換フォーマット (edges / nodes / **incidences**)、
HypergraphDB (link / arity / 無名タプル位置)、TypeDB (relation / role / plays)。

- **核名詞は `Hyperedge` (1 語、E を大文字にしない)** — 数学文献 (Berge 以来) の 1 語慣行 +
  .NET の「辞書 1 語複合語」規則。BCL 前例: WPF `System.Windows.Documents.Hyperlink`。
  `Relation`/`Link` 系は既存 `Relationship` と衝突するため不採用
- **`Transversal` は不採用** — ハイパーグラフ理論の transversal (= hitting set、
  全ハイパーエッジと交わる頂点集合) と正面衝突し、かつ `Traversal` と編集距離 1 の最小対立ペア
- 位置 = **Role**、所属ノード = **Member**、node-edge 対 (internal のみ) = **Incidence**
  (HIF 標準の incidences と一致)
- 日本語 docs 表記: ハイパーエッジ / ロール / メンバー

### Relationship との概念関係 (2026-07-03、ユーザ合意)

- 数学的には Relationship ≒ **アリティ 2 の Hyperedge の特殊化** (2-uniform hypergraph)。
  「Hyperedge の元が Relationship」ではない (元は Node)
- **ストレージ・DSL では統合しない** — binary の 48B 固定レコード 1 ピン読みは頻度 99% の
  ホットパスであり、hyperedge 表現 (ヘッダ + incidence×2) に載せ替えるとポインタチェイスが
  2〜3 倍化する。方向 (source/target) とロールの語彙も混線する。両者は別 `EntityKind` の兄弟
- **統合はカーネル/ビュー層で行う** — lowering (hyper → binary co-membership view で
  GraphKernel 流用) と lifting (Relationship を source/target ロールのアリティ 2 hyperedge と
  見なす view) の両方向。lifting の公開は実需が出てから
- incidence 表現は Levi グラフ (頂点 ∪ ハイパーエッジの二部グラフ) の物理化であり、
  `IncidenceEntry` は「Role で型付けされた軽量 Relationship」と構造的に同型。
  ただし公開 API では Relationship を名乗らせない (`Relationships()` 走査への混入事故防止)
- メンバーの高階化 (member を `EntityRef` 化して rel/hyperedge をメンバーに許す、
  RDF-star / ubergraph 的拡張) は **v1 対象外**。ID 設計 (`EntityRef` = Kind+id) 上は
  安価だが、可視性カスケード・vacuum・DSL 型付けが複雑化するため実需が出てから。
  クリーンブレイク方針によりフォーマット先取り予約は不要

### 相互変換 API (2026-07-03、命名のみ固定・v1 実装対象外)

.NET 慣行 (`As*` = ゼロコストビュー / `To*` = 実体生成) に従い、実体変換は `To*` とする。
**`AsRelationships` は不採用** — 射影ペアには RelationshipId が存在せず、
`GraphTraversal<RelationshipId>` を返すと下流の `.Has`/`.SourceNode` が破綻する。

- `ToRelationships(relType, fromRole, toRole)` — hyperedge 走査から実 Relationship を生成
  (co-membership の物理化)。binary 専用機構 (AdjacencyBlockStoreV2 / SIG / 重み付き最短経路)
  へ n 項ファクトを供給する用途。HYP-6 kill criteria の「物理 co-membership ビュー前倒し」の明示 API 版
- `ToHyperedges(hyperType, sourceRole, targetRole)` — relationship 走査からアリティ 2 の
  hyperedge を生成 (binary データの n 項化移行、案A reified パターンからの移行)。
  複数 rel の束ね (スター束ね) は引数設計が発散するため v2 以降
- ビュー版が必要になったら `MemberPairs(fromRole?, toRole?)` (ロール対射影、省略時 clique 展開 =
  2-section)。Relationship を名乗らない
- **系譜は同期しない** — `To*` の導出はスナップショット。元 hyperedge の削除は導出 rel に
  波及しない (トリガー機構が無いためカスケードを約束しない)。冪等更新は再実行 + `MergeRelationship`。
  docs に明記すること

## 命名体系 (確定)

| 層 | 命名 |
|---|---|
| Core | `HyperedgeId`, `HyperedgeTypeId`, `EntityKind.Hyperedge`, `HyperedgeMember` (Role + NodeId の struct) |
| Storage (internal) | `HyperedgeStore`, `IncidenceStore`, `IncidenceEntry`, `RoleId` |
| tx API | `CreateHyperedge(type, members)`, `DeleteHyperedge`, `GetMembers(edgeId, role?)`, `GetHyperedges(nodeId, type?, role?)`, `LogicalMutation.CreateHyperedge` 等 |
| DSL 生成 | `AddHyperedge(type)` → `HyperedgeBuilder`: `.Member(role, nodeId)` 複数回 + `.P(...)` + `.Next()` (From/To と同イディオム) |
| DSL 走査 | node 側 `.Hyperedges(type?, role?)` → `GraphTraversal<HyperedgeId>`、edge 側 `.Members(role?)` / `.OtherMembers(role?)` (`OtherNode` の類推) |
| Match | `GraphPattern.Hyperedge("p", "Purchase").Member("buyer", Node("a","Person")).Member("item", Node("b","Book"))` — 星型 `HyperedgePattern` (線形 node-edge-node とは別エントリポイント) |
| SourceGen | `[Hyperedge("Purchase")]` (非ジェネリック) + `[Role("buyer")] public Person Buyer`、`IGraphHyperedge<TSelf>.GraphType` |

命名原則:

- DSL 動詞は「戻り値エンティティの複数形」規約 (`Relationships()` → RelationshipId) に従う。
  `Hyperedges()` はこの規約と一致し、`Members()` は XGI の `edges.members()` と一致
- 無向 + ロールのため方向接頭辞 (Out/In) は使わない。role フィルタが方向の一般化
- co-membership の 1-hop 糖衣 (`Peers` 等) は入れない —
  `.Hyperedges("Purchase", role: "buyer").OtherMembers("item")` の合成で書ける
- `[Hyperedge]` が非ジェネリックなのはアリティ可変のため。ロールはプロパティ宣言で表し、
  参照先ノード型はプロパティの CLR 型から取る (`[Property]` が CLR 型で codec を決めるのと同イディオム)。
  多重ロールは `IReadOnlyList<T>` 検出 (multi-value property の `List<T>` 規約の再利用)

## ストレージ設計 (方針)

RelationshipStore / NodeStore の既存イディオム (固定レコード + Int48 チェーン + MVCC サイドカー) の相似形。

- **HyperedgeStore**: 固定レコード `Flags(1) | TypeId(2) | FirstIncidenceId(6) | FirstPropId(6)` 程度。
  MVCC は `EntityVersionMeta` サイドカー
- **IncidenceStore**: 固定レコード `HyperedgeId(6) | NodeId(6) | RoleId(2) | NextInNode(6) | NextInEdge(6) | Flags(1)` 程度。
  「ノードごとのチェーン」「エッジごとのメンバーチェーン」の 2 本を貫通
  (RelationshipStore の Src/Tgt チェーンと同イディオム)。メンバー集合が作成時確定のため
  NextInEdge チェーンは作成時に一括構築でき、prev ポインタの要否は実装時に判断
- **node incidence head**: HYP-S1 の実測で、`NodeStore` へ `FirstIncidenceId` を追加する 21B 案は
  binary p50 の 3% gate を超えたため不採用。固定 tenant 25 の 6B `NodeIncidenceHeadStore` を採用する
- **RoleId**: トークンストアで intern (RelationshipTypeId と同様、独立空間)
- `EntityKind.Hyperedge` 追加 (internal で Incidence kind の要否も判断)
- FormatVersion は HYP-1a で V3 へ bump する (マイグレーション無し)
- WAL (PageImage + logical mutation)・recovery・vacuum (slot 再利用 + generation) は既存ストア同様に対応

## クエリ層 (方針)

- 新オペレータ: `HyperedgeScanOperator` (全スキャン)、`ExpandToHyperedgeOperator` (node → hyperedge)、
  `ExpandMembersOperator` (hyperedge → nodes、role フィルタ付き)
- co-membership (node → incidence → edge → incidence → node の 2 段展開) を binary view として
  GraphKernel に供給すれば BFS / 最短経路 / Dijkstra は無改造で動く (後続フェーズ)

## タスク列

各 epic の具体的な増分は `plans/hyperedge-implementation-tasks.md` に分割した。
HYP-S1 は HYP-1 前、HYP-S2 は HYP-5 前に実行する。

| ID | 内容 | 依存 | 状態 |
|---|---|---|---|
| HYP-0 | skill / roadmap / 親子計画のタスク配線 | — | 完 (2026-07-03) |
| HYP-S1 | node incidence head の inline / 別テナント比較 spike | HYP-0 | 実測済み (案B採用) |
| HYP-1 | Core ID 型 + `EntityKind.Hyperedge` + HyperedgeStore + IncidenceStore + node incidence head + MVCC サイドカー + WAL + recovery テスト | HYP-S1 | 完 (2026-07-04)。1d 走査 spike は 3x 超過 → HYP-6d 必須化 |
| HYP-2 | tx API (Create/Delete/GetMembers/GetHyperedges) + logical mutation + プロパティ/削除の可視性テスト | HYP-1 | 完 (2026-07-04)。2c WAL spike は倍率仮説棄却 → HYP-2d 新設 |
| HYP-2d | incidence レイアウト再設計 (WAL 限界費用 17.3% 削減。fixed-slot 直接アドレス化が第一候補) | HYP-2 | 完 (2026-07-05)。WAL 全 arity 合格 (限界費用 ≈27.6 B/member)、FormatVersion V4。走査は改善するも 3x 残 → HYP-6d 継続 |
| HYP-3 | 走査オペレータ 3 種 + DSL (`Hyperedges`/`Members`/`OtherMembers`/`AddHyperedge` builder) | HYP-2 | 完 (2026-07-05)。3c は `Select<TEntity>(alias)` 追加で RAG 固定 4 シナリオ合格 |
| HYP-4 | Match (`HyperedgePattern` 星型パターン + コンパイラ拡張) | HYP-3 | 完 (2026-07-05)。星型パターン + `MatchTuple.Hyperedge` |
| HYP-S2 | SourceGenerator の role binding API spike | HYP-3 | 実測済み (案A採用: `GraphNodeRef<TNode>`。2026-07-05) |
| HYP-5 | SourceGenerator (`[Hyperedge]`/`[Role]` + `IGraphHyperedge<TSelf>` + 型付き CRUD/走査糖衣) | HYP-2, HYP-3, HYP-S2 | 未着手 |
| HYP-6 | vacuum + IDiagnosticsApi 整合性チェック + 性能実測 (下記 kill criteria) | HYP-1, HYP-2, HYP-2d, HYP-3 | 6a vacuum・6b 診断/統計 完 (2026-07-06, a1076d0 / 12540fa)。残: 6d 走査改善・6c 性能ゲート |
| HYP-7 | docs/spec as-built 追記 + development.md + サンプル (RAG n 項ファクト) | HYP-4, HYP-5, HYP-6 | 未着手 |

並列性: HYP-4 と HYP-5 は独立並行可。HYP-2d は storage 層で閉じるため
HYP-3c / HYP-4 / HYP-S2 と並行可。HYP-6a / HYP-6d は HYP-2d のレイアウト確定後に着手する。

## 仮説検証イテレーション (kill criteria)

「推論より実地検証」に従い、フェーズごとに数値を先に固定して計測する:

- **HYP-1 後**: incidence チェーン走査 (co-membership 1 論理ホップ = 2 ポインタチェイス) の p50 が
  binary 隣接 1 ホップの **≤3×** (次数別: 10 / 100 / 1000)。超過なら AdjacencyBlockStore 型の
  物理 co-membership ビューを HYP-6 に前倒し
- **HYP-2 後**: WAL 増幅が binary relationship 作成比 **≤ (1 + アリティ/2)×** 程度に収まること
  (incidence レコード数に比例する分は許容、超線形なら設計見直し)
- **HYP-3 後**: 実 RAG シナリオ (n 項ファクト: 主体/客体/出典/時点) のクエリパターンが
  DSL 合成で書き切れること (書けない形が出たら糖衣追加を検討、命名は本計画の原則に従う)

実測状況 (2026-07-05): HYP-1 後ゲートは不合格 (degree 10/1000 が 5.49x/7.97x) → HYP-6d 必須化。
HYP-2 後ゲートは線形性合格・倍率不合格 (batch arity 4/8/16) → 是正タスク HYP-2d を新設し、
**HYP-2d で回収済み** (fixed-slot 直接アドレス化、batch 全 arity 合格、走査も 4.8x/1.7x/5.4x へ
改善したが 3x 残のため HYP-6d は継続)。HYP-3 後ゲート (HYP-3c) は固定 4 シナリオを
単一 operator tree で取得して合格。hyperedge alias へ戻る汎用 `Select<TEntity>(alias)` のみ追加し、
`HasMember` と RAG 固有糖衣は不要と判断した。
数値と決定の正本は `plans/hyperedge-implementation-tasks.md` の決定記録。

## API アイデアバックログ (実装判断は都度)

> HYP-1〜7 のスコープ外。各案は「生えていたらこの価値がある」という目的仮説付きで記録し、
> 実装判断はトラック進行を踏まえて都度行う。命名は本計画の命名原則に従う (Relation/Link/Transversal 禁止、
> `As*` ビュー / `To*` 実体生成、戻り値エンティティの複数形)。

### 取込・冪等性

- **`MergeHyperedge(type, members)`** — MergeNode/MergeRelationship と同列の get-or-create。
  同一性キーは「型 + ロール付きメンバー集合」— メンバー集合が作成時確定である決定のおかげで
  同一性が well-defined になる (可変だったら成立しなかった)。
  **目的**: RAG の再取込 (同一文書の再処理) で n 項ファクトが重複しない冪等パイプライン。
  取込系では Create より先に必要になる可能性が高い。
- **BulkLoader / StreamingBulkLoader の hyperedge 対応** (`AddHyperedge`) — incidence を
  ノード順ソートで一括構築。**目的**: 初期 KG 構築のスループット。FTS-7 で測った WAL 増幅の
  知見がそのまま適用できる。

### クエリ (展開せずに絞る)

- **`HasMember(nodeId)` / `HasMember(role, nodeId)`** — `GraphTraversal<HyperedgeId>` 上の
  Has 系フィルタ。**目的**: 「Alice が buyer である購入」を `Members()` 展開なしで絞る。
  incidence チェーンの存在確認だけで済み、低アリティでは索引不要。
- **`Arity()` / `HasArity(n)` / `HasArity(min, max)`** — `Id()`/`Label()` と同列のアクセサ + フィルタ。
  **目的**: 「参加者 3 人以上のイベント」等のアリティ述語。アリティをヘッダレコードに持てば O(1)。
- **`CommonHyperedges(type?, params NodeId[] nodes)`** — 全指定ノードを含むハイパーエッジ。
  incidence チェーン (最小次数ノード起点) の交差で実装。
  **目的**: 「Alice と Bob が両方参加した会議」— binary では reification + 二重 join になる
  hypergraph 固有クエリを 1 動詞にする。
- **走査アルゴリズムの hyperedge 経由版** — `ShortestPathTo` / `Bfs` に via-hyperedge オプション
  (co-membership lowering ビューを GraphKernel に供給)。
  **目的**: 「イベント共起を辺と見なした到達可能性・最短経路」。カーネル無改造が売りなので
  実装コストはビュー構築のみ。

### 検索統合 (RAG 差別化の本丸)

- **hyperedge ベクトル索引** — `VectorIndexSpec.EntityKind` に `Hyperedge` を許可し、
  `KnnHyperedges(index, query, k)` / `FilterByKnn` を追加。ベクトル層は `(EntityKind, id)`
  キー設計なので追加コストがほぼない。
  **目的**: n 項ファクト自体の類似検索 (「似た出来事を探す」)。ノード埋め込みでは
  表現できないイベント粒度の検索が生える。
- **hyperedge FTS + HybridSearch 拡張** — `CreateFullTextIndex` の対象に hyperedge 型を許可し、
  `SearchHyperedges(index, text, k)`。**目的**: イベント記述・n 項ファクトの叙述テキストへの
  BM25 とハイブリッド検索。
- **SIG 演算の hyperedge 候補対応** — `ApplyDyadic` の候補集合に `EntityKind.Hyperedge` を許可。
  **目的**: n 項ファクトの `float[]` プロパティへのカスタムスコアリング。
  `EntityCandidateSet` が Kind を持つ設計なので原理的障害はない。
- **IVF / PQ 系 ANN 索引 (`VectorIndexKind.IvfFlat` / `IvfPq`)** — FAISS 系静的構造のピュア C# 実装。
  **目的**: hyperedge ベクトルは「作成時確定」により in-place 更新が存在しない
  (node の `SetVector` 上書き + HNSW 再リンク問題と対照的) ため、IVF/PQ の最大弱点 =
  更新脆弱性が構造的に消える。挿入 = posting list append、削除 = tombstone + vacuum
  (既存イディオムと同型)、codebook 訓練のみ初期構築/定期再構築。N=10⁵〜10⁷ の fact 集合で
  PQ 4〜16× 圧縮がページキャッシュ常駐を可能にし、8KB ページ + Clock buffer pool と好相性。
  **spike 先行** (kill criteria 例: N=10⁵ で recall@10 ≥ 0.95 かつ brute 比 ≥5×、HNSW 比メモリ ≤1/4。
  未達なら既存 HNSW で足りるという結論を採る)。
  なお「メンバー集合に絞った KNN」は別問題で、アリティ規模 (10⁰〜10²) では pre-filter + brute
  (`FlatOnly` 経路) が最適 — ANN の crossover は候補 10⁴〜10⁵ 以上。大候補集合のみ VEC-14
  (Filtered HNSW) の領域。
- **hyperedge 重心による overlapping IVF (投機的)** — 各 hyperedge のメンバーベクトル重心を
  セル代表と見なし、「クエリに近いメンバーを持つ hyperedge」検索を重心スキャン → メンバー brute の
  2 段で解く。**目的**: hypergraph 構造そのものを粗量子化器として流用し、汎用 IVF より先に
  RAG 検索の形 (fact 起点の近傍探索) に合う可能性。メンバー集合不変により重心は作成時に
  1 回計算すればよい (メンバー node ベクトル更新時の staleness には注意書きが必要)。

### スキーマ・品質

- **ロールシグネチャ宣言** — `GetOrCreateHyperedgeType(name)` (機械的に必要) に加えて
  `DefineHyperedgeRoles(type, params RoleSpec[])`、`RoleSpec(name, required, multiple)`。
  `CreateHyperedge` 時に検証。TypeDB の relates 宣言に相当。
  **目的**: 取込バグ (buyer 欠落等) の早期検出、SourceGen `[Role]` との整合検証、
  KG スキーマの自己文書化。宣言なしの型は無検証 (現行ラベルと同じ自由度) を既定とする。
- **`GraphStats` 拡張** — HyperedgeCount / 型別カウント / アリティ分布。
  **目的**: expand 系オペレータのコスト見積り (オプティマイザ) と可観測性。
  binary の次数統計と同じ動機。

### 相互運用

- **HIF エクスポート/インポート** — Hypergraph Interchange Format (HyperNetX / XGI / HGX /
  SimpleHypergraphs.jl 共通の JSON フォーマット、nodes/edges/incidences 構造)。
  incidence 表現と構造が一対一対応するため変換は素直。ゼロ依存 (JSON のみ) で書ける。
  **目的**: Python/Julia のハイパーグラフ分析・可視化エコシステムで Quiver の KG を
  検査できる。デバッグ・研究用途の出口として安価に効く。

### Quiver.Rag

- **Fact スキーマ (n 項ファクト + 出典ロール)** — RagSchema に Fact hyperedge 型を追加
  (例: subject / object / source (Chunk) / asOf ロール)。検索 API は「ヒットした Chunk →
  所属 Fact → 共起エンティティ/Chunk」のグラフ展開を返す。
  **目的**: 出典 (provenance) がファクトのメンバーとして構造的に付随するため、
  回答生成時の grounded citation が join なしで取れる。binary KG では出典付与に
  reification が必須になる — hyperedge 化の価値が最も端的に出るユースケース。

## トラック開始時の配線

以下は作業ツリー上の配線状況を示す。
HYP-0 の正式な完了状態は、レビュー承認後のコミット時に両 SKILL.md と同期する。

- [x] `.claude/skills/quiver-implement/` への HYP 系コンポーネント登録
- [x] `.agents/skills/quiver-implement/` への HYP 系コンポーネント登録
- [x] docs/design/roadmap.md への HYP-1〜7 追記
- [ ] VEC 系後続 (HNSW 物理削除等) との順序調整

## 完了の定義 (トラック全体)

- RAG n 項ファクトのサンプル (取込 → `Hyperedges`/`Members` 走査 → Match → SourceGen 型付き) 完走
- kill criteria 3 点の実測値が記録されている
- 全スイート緑 (`dotnet build` + 全テスト)
- docs/spec に as-built 追記済み
