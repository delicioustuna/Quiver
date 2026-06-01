# ARCH-4〜8 指示書 — 抜本再設計 Phase 1〜5

> 作成: 2026-06-01 / 対象ブランチ: develop
> 前提: ARCH-1 (単一アセンブリ化 + 名前空間整理) / ARCH-2 (API 露出修正 + 型名修正) / ARCH-3 (インデックス Generation 導入) 完了。
> 位置づけ: 抜本再設計 (`docs/design/11_rearchitecture_master_plan.md` 第8部ロードマップ) の **Phase 1〜5** を、`quiver-implement` スキルから起動できる **ARCH-4〜8** タスクとして定義する指示書。
> 互換性: develop 段階につき public API・オンディスクフォーマットの破壊的変更を許容 (マイグレーション不要、`FormatVersion` を進めてよい)。

この指示書は **5 つの大型タスク (ARCH-4〜8)** を定義する。いずれも破壊変更が大きいため、ARCH-2/3 と同じく **「着手承認」(設計案提示) と「完了承認」(状態更新ガード) の二段階承認**を要する。各タスクは独立した別セッションで着手してよい。**推奨順序は ARCH-4 → ARCH-5 → ARCH-6 → ARCH-7 / ARCH-8**（ARCH-7/8 は query/DSL 層なので前段と並行可）。

---

## §1. 背景 (docs/design/11 第8部 ロードマップ)

ARCH-1〜3 で「単一アセンブリ化 / 公開 API 整理 / 索引 Generation」までを完了した。残る 5 つの製品目標のうち未達は **(a) SQLite 的ポータビリティ (DB がディレクトリ + 15 以上のファイル) と (b) グラフ + ベクトル統一 (ベクトルがメモリ保持のみ・非トランザクショナル)**。docs/design/11 のロードマップに沿って Phase 1〜5 で解消する。

| ARCH | Phase | 内容 | 主依存 |
|---|---|---|---|
| **ARCH-4** | 1 | L0 単一ファイル Pager + 共有 buffer pool 配線 + WAL 一本化 | (なし — 基盤) |
| **ARCH-5** | 2 | ストアのテナント化 + ID 全面 Kind+Gen+Seq 化 + property 格納再設計 | ARCH-4 |
| **ARCH-6** | 3 | vector/ANN の in-file 永続化 + `SetVector` の tx 統合 | ARCH-4, ARCH-5b |
| **ARCH-7** | 4 | 単一 LogicalPlan IR + optimizer 集約 (KNN 押し下げ移設) | (独立、ARCH-5 後推奨) |
| **ARCH-8** | 5 | Hop 型保存トラバーサル (source-gen 拡張) | (独立) |

継ぎ足しの痕跡 (docs/design/11 §6.1、ARCH-5〜7 で解消): adjacency V1/V2 共存、ベクトル in-memory/persist 二系統、per-store ヘッダ/freelist/format 重複、毎操作 `FlushMeta`、KNN 押し下げが DSL 散在、MVCC sidecar 後付け。

---

## §2. ARCH-4 — Phase 1: L0 単一ファイル Pager + 共有 buffer pool + WAL 一本化

### 目的
DB を「ディレクトリ + 15+ ファイル」から **単一データファイル `*.quiver` (+ 運用中のみ `*.quiver-wal` サイドカー)** に集約し、SQLite 的ポータビリティ (コピー/メールで運べる) を満たす。あわせて dead config の `BufferPoolSize` を実配線する。

### スコープ
- **やる**: ① 単一ファイルのページ空間 (8KB ページ、型付きページ、page 0 = スーパーブロック/カタログ) を確立 ② WAL を案B (単一データ本体 + 単一サイドカー `*.quiver-wal`、checkpoint でマージ、クリーン終了で消去) に一本化 ③ プロセス共有・サイズ可変の単一 buffer pool に統合し `GraphDatabaseOptions.BufferPoolSize` を実配線。
- **やらない (ARCH-5 へ)**: 各ストアを単一ファイルのテナントに載せ替える「テナント化」本体 (node/rel/prop/index/vector の同居)。本タスクは**ページ空間 + WAL + buffer pool の基盤**まで。ストアは当面 per-file のまま新 Pager 上で動かす互換層を許容し、テナント化は ARCH-5 で行う (段階移行)。
  - ※「ページ空間だけ先に作って per-store を載せ替えるのは二度手間」と判断する場合は、ARCH-5a テナント化を本タスクに前倒し統合してよい (着手承認時に範囲を合意する)。

### 読むべきファイル (着手前に必ず Read)
- `docs/design/11_rearchitecture_master_plan.md` §2 (単一ファイルストレージ全体) / §2.3 (ページ空間・テナント化・WAL内包・カタログ・buffer pool) / §8 (ロードマップ)
- `src/Quiver/Storage/PagedFile.cs` (現 MMF + 8KB page + 自前 meta/free list/format。`DefaultPoolCapacity=256` 固定)
- `src/Quiver/Storage/PageManager.cs` (`OpenOrCreate` が `new PagedFile(path)`) / `src/Quiver/Storage/PageKind.cs`
- `src/Quiver/Wal/WriteAheadLog.cs` (segment 群) / `src/Quiver/Wal/WalFileKind.cs` / `src/Quiver/Transactions/Checkpointer.cs` / `src/Quiver/Transactions/RecoveryManager.cs`
- `src/Quiver/Backend/BinaryGraphStorageBackendFactory.cs` (現在生成する物理ファイル群一覧)
- `src/Quiver/GraphDatabase.cs` (`GraphDatabaseOptions.BufferPoolSize` が未配線な箇所)

### 設計判断 (着手承認で提示)
1. **スーパーブロック/カタログページのレイアウト** (page 0): テナント配置 (root page / free list head / record format)、トークン辞書、索引カタログ、ベクトル索引 spec、`FormatVersion`。現状の `*.tok` / `.fileKinds` / `.idxmeta` / `adj.epoch` 非ページサイドカーを全廃する範囲。
2. **共通スペースマネージャ + テナント別 free list** (LiteDB の「コレクション別 free list」相当)。型付きページのエクステント連鎖。
3. **WAL 一本化** (案B): 現 segment 群を `*.quiver-wal` 1 本に。`WalFileKind` / Checkpointer / RecoveryManager の replay 経路をファイル単位から page 単位へ。
4. **共有 buffer pool**: サイズ可変・プロセス共有プールへ統合し `BufferPoolSize` を実配線。`PagedFile.DefaultPoolCapacity` 固定を置換。
5. **FormatVersion bump** (V4→V5)。develop ゆえマイグレーション不要。

### 実装手順
1. 設計案 (1〜5) を提示し**着手承認**を得る。
2. スーパーブロック/カタログページ + 共通スペースマネージャ + テナント別 free list を実装。
3. WAL を案B に一本化 (checkpoint マージ / クリーン終了で WAL 消去)。
4. 共有 buffer pool を実装し `BufferPoolSize` 配線。
5. crash recovery / checkpoint atomicity / WAL replay の既存テスト群を新 Pager で緑にする。
6. `FormatVersion` を進める。

### 完了条件
- `dotnet build Quiver.slnx` 0 errors / 全テスト緑。
- 静止時 `mydb.quiver` 単一ファイル (+ 運用中 `mydb.quiver-wal`) で開閉でき、クリーン終了後は単一ファイルのみ。
- crash recovery / checkpoint atomicity テストが単一ファイル Pager で緑。
- `BufferPoolSize` が実際にプール容量を制御する (option 値変更が観測できるテスト)。
- `FormatVersion` 進行、旧フォーマットは `FormatVersionMismatchException`。

---

## §3. ARCH-5 — Phase 2: テナント化 + ID 全面 Kind+Gen+Seq + property 再設計

> 巨大タスク。**3 サブステップ (5a → 5b → 5c) に分けて段階実装**し、各サブステップで build/test 緑 + 着手承認を取る。

### ARCH-5a: ストアのテナント化
- **目的**: node/rel/prop/blob/version/adjacency/index/vector/token/catalog を ARCH-4 の単一ファイルページ領域に同居させ、per-store ヘッダ/freelist/format の重複 (docs/design/11 §2.1) を廃する。
- **読むべきファイル**: docs/design/11 §2.3.2 / `src/Quiver/Stores/*` (各 store の header/freelist/format パターン) / `src/Quiver/Backend/BinaryGraphStorageBackendFactory.cs`。
- **完了条件**: 全ストアがカタログページ定義のテナント (エクステント連鎖) として 1 ファイルに同居。`*.tok`/`.idxmeta`/`adj.epoch` 全廃。bench 維持。

### ARCH-5b: ID 全面 Kind+Gen+Seq 化
- **目的**: ARCH-3 で索引値レーンに限定した `GenerationalRef` を **物理 ID 全体 (オンディスク record / WAL / ベクトルキー)** に昇格・統一する。論理 API は `NodeId`/`RelationshipId`/`PropertyId` を**型安全な別 struct のまま維持**し、内部に `Generation` と `Sequence` を持たせる (docs/design/11 §3.4)。
- **読むべきファイル**: docs/design/11 §3 (Kind+Generation+Sequence、64bit/128bit パック案、wraparound 退役、MVCC version との別概念性) / `src/Quiver/Core/Ids.cs` / `src/Quiver/Core/EntityId.cs` / `src/Quiver/Core/GenerationalRef.cs` (ARCH-3 の packing) / `src/Quiver/Stores/NodeStore.cs` / `src/Quiver/Stores/EntityVersionStore.cs` (Generation 供給元) / vector の `(EntityKind, long)` キー (`src/Quiver/Core/Vector.cs` / `InMemoryVectorStore.cs`)。
- **設計判断 (着手承認)**: ① 64bit パック (Kind4/Gen16/Seq44、ARCH-3 と同) を維持するか 128bit 安全案 (Kind8/Gen32/Seq64) に拡げるか ② `EntityId` と `GenerationalRef` の統一形 (`EntityId` に Generation を載せ `GenerationalRef` を吸収) ③ `NodeId` 等が `Generation` を内部保持する形と公開シグネチャ安定性 (原則: 型名・公開メンバは不変) ④ ベクトル binding キーへの Generation 適用 (ARCH-6 と協調) ⑤ `FormatVersion` bump。
- **完了条件**: `TryResolve(NodeId) → generation 不一致で not-found`。外部往復 ID の検証が効く。ベクトル binding が slot 再利用に安定 (ARCH-6 と合流)。ARCH-3 の `GenerationalRef` が物理 ID に統一され重複が消える。format bump。

### ARCH-5c: property 格納再設計
- **目的**: entity ごと片方向連結リスト (`FirstPropertyId→Next`、get/set/has が O(#props)) を、小プロパティはレコードへ**インライン**、索引対象は**列指向**、可変長は **slotted ページ**へ再設計。毎操作 `FlushMeta` をコミット時バッチ化。
- **読むべきファイル**: docs/design/11 §7.1 §6.3 / `src/Quiver/Stores/PropertyStore.cs` / `src/Quiver/GraphTransaction.cs` (`GetProperty`/`SetNodeProperty`/`HasProperty` の連結リスト走査)。
- **完了条件**: property get/set/has が O(1)〜O(small)。書き込み増幅減。`FlushMeta` バッチ化。bench 改善。

### ARCH-5 全体の完了条件
- 3 サブステップすべて build/test 緑。`FormatVersion` V5→V6 (bump は 5b/5c でまとめてよい)。bench 維持/改善 (回帰 sentinel TS-6 の 20% 内)。

---

## §4. ARCH-6 — Phase 3: vector/ANN の in-file 永続化 + SetVector の tx 統合

### 目的
現状 binary backend は `InMemoryVectorStore` でベクトルを**永続化していない・非トランザクショナル** (`db.Vectors` 直叩き)。ベクトル payload と ANN 索引 (HNSW/IVF) を**同一ファイルのページに永続化**し、`SetVector` をトランザクション境界に取り込んでグラフ変更と原子整合させる。

### スコープ
- **やる**: ベクトル payload + ANN 索引の in-file ページ永続化、`SetVector` の tx 配下化 (commit/rollback と原子整合)、binding キーへの Generation 適用 (ARCH-3 で Phase 3 送りにした部分)。
- **やらない**: 埋め込み元の原文 (source corpus) の DB 取り込み。原文同一性は `EmbeddingTaskRecord.ContentHash` のハッシュ追跡で足り、原文は `Quiver.Embedding` 側 (DB 外) に留める (docs/design/11 §2.3.5、ファイル肥大 / PII / ライセンス回避)。

### 読むべきファイル (着手前に必ず Read)
- `docs/design/11_rearchitecture_master_plan.md` §2.3.5 (ベクトル in-file 永続化) / §7.3
- `src/Quiver/Core/Vector.cs` (`IVectorStore` 契約) / `src/Quiver/Core/InMemoryVectorStore.cs` / `src/Quiver/Core/VectorScorer.cs` (SIMD 距離) / `src/Quiver/Core/VectorCatalog.cs` (`EmbeddingTaskRecord.ContentHash`)
- `src/Quiver/Backend/BinaryGraphStorageBackendFactory.cs` (現在 `InMemoryVectorStore` を配線している箇所)
- post-commit hook (VEC-3 `OnCommitted`/`OnRolledBack`/`ICommitHookRegistrar`)

### 設計判断 (着手承認で提示)
1. ベクトル payload のページレイアウト (固定次元配列 / 可変) と ANN 索引 (HNSW ノード or IVF リスト) のページ表現。
2. `SetVector` の tx 統合方式 (WAL ロギング / commit 時反映 / rollback 巻き戻し)。
3. binding キー `(EntityKind, Sequence)` → `(EntityKind, Generation, Sequence)` 化 (ARCH-5b の物理 ID と整合)。
4. `FormatVersion` bump。

### 完了条件
- ベクトルが単一ファイルに永続化され、再起動跨ぎで KNN 検索が再現。
- `SetVector` が tx 配下で、abort/crash 後にグラフ変更と原子整合 (hybrid query の耐障害テスト緑)。
- ベクトル binding が slot 再利用に対して安定 (Generation 照合)。
- `InMemoryVectorStore` は test fixture に限定 (docs/design/11 §6.3)。

---

## §5. ARCH-7 — Phase 4: 単一 LogicalPlan IR + optimizer 集約

### 目的
計画層の 3 系統分散 (`IOperatorBuilder` 論理もどき + `Quiver.Query.Physical` 物理 + `Match` コンパイラ) と、表層 DSL に埋め込まれた KNN 最適化 (`PendingKnnBuilder`) を、**単一 `LogicalPlan` 代数**に集約する。

### スコープ
- **やる**: `LogicalPlan` 代数 (`Scan/Filter/Expand/VarLenExpand/Path/Knn/Project/Aggregate/Apply/Limit/Sort/Dedup/Union/Optional`)、フロント (fluent DSL / Match / 将来の Cypher・Gremlin parser) をすべて `LogicalPlan` に lower、optimizer (rule + cost) に KNN graph-first/vector-first 押し下げを **DSL から移設**、physical planner で `LogicalOp → IPhysicalOperator`。実行は現行 pull 型を踏襲。
- **やらない**: 物理オペレータ・実行エンジンの作り直し (LogicalPlan → 既存 physical を選ぶだけ)。

### 読むべきファイル (着手前に必ず Read)
- `docs/design/11_rearchitecture_master_plan.md` §5 (単一論理プラン IR)
- `src/Quiver/Client/GraphTraversal.cs` (`IOperatorBuilder` 論理もどき) / `src/Quiver/Client/Internal/PendingKnnBuilder.cs` (DSL に埋まった KNN 押し下げ)
- `src/Quiver/Client/Match/MatchCompiler.cs` (Match コンパイラ)
- `src/Quiver/Operators/*` (物理オペレータ群) / `QueryOptimizer` / `GraphStats`

### 設計判断 (着手承認で提示)
1. `LogicalOp` ノード型と代数 (上記 14 種を最終確定)。
2. フロント別 lowering 経路 (fluent / Match)。
3. optimizer rule (述語押し下げ、`AllNodesScan→NodeByLabelScan`、KNN 押し下げ移設) + cost (GraphStats) の構成。
4. physical planner の `LogicalOp → IPhysicalOperator` 選択表。

### 完了条件
- fluent DSL / Match が `LogicalPlan` 経由で実行され、既存クエリ結果が不変 (回帰テスト緑)。
- KNN 押し下げが optimizer に集約され、表層 DSL から `PendingKnnBuilder` 特別扱いが消える。
- optimizer 回帰テスト (PW-18 sentinel 流用) 緑。

---

## §6. ARCH-8 — Phase 5: Hop 型保存トラバーサル

### 目的
`TypedGraphTraversal<T>` が `Out`/`In`/`Both` で**型なし `GraphTraversal<NodeId>` に降格**する現状を解消し、`Person -KNOWS-> Person` のようなホップ間の型保存を可能にする。

### スコープ
- **やる**: `IGraphRelationship<TSelf, TSource, TTarget>` 制約の導入、source generator が `[GraphRelationship(Source=typeof(..), Target=typeof(..))]` から `TSource`/`TTarget` を埋める、`Out<TRel, TTarget>()` が `TypedGraphTraversal<TTarget>` を返す。多態ターゲットは共通基底/マーカー interface か、型なし `Out(string)` への明示降格を併存させる。
- **やらない**: 物理層・実行層の変更 (DSL/source-gen の型シグネチャ拡張のみ)。

### 読むべきファイル (着手前に必ず Read)
- `docs/design/11_rearchitecture_master_plan.md` §4 (Hop 型保存トラバーサル)
- `src/Quiver/Client/TypedGraphTraversal.cs` (現状 Out/In/Both で型降格する箇所) / `src/Quiver/Client/IGraphNode.cs` / `src/Quiver/Client/IGraphRelationship.cs`
- source generator (`Quiver.SourceGen`) の relationship 出力

### 設計判断 (着手承認で提示)
1. `IGraphRelationship<TSelf, TSource, TTarget>` の制約形と既存 `IGraphRelationship` との後方互換。
2. source generator の出力拡張 (`Source`/`Target` 型の埋め込み)。
3. 多態ターゲットの扱い (共通基底 / 型なし降格併存)。

### 完了条件
- `g.Nodes<Person>().Out<Knows>().Has(p => p.Age, 30)` が全ホップで型保存 (コンパイル時に `TypedGraphTraversal<Person>` を保つ)。
- 型付き traversal のサンプル/テストが緑。既存の型なし API は併存。

---

## §7. 依存関係まとめ

- **ARCH-4** → ARCH-5 (テナント化はページ空間前提), ARCH-6 (in-file ページ前提)
- **ARCH-5a** (テナント化) → ARCH-4
- **ARCH-5b** (ID) → ARCH-3 (`GenerationalRef` を物理 ID へ昇格・統一), ARCH-6 (vector binding に Generation)
- **ARCH-5c** (property) → ARCH-5a (テナント化済みページ上で slotted/列指向を組む)
- **ARCH-6** → ARCH-4, ARCH-5b
- **ARCH-7** → 独立 (ARCH-5 後推奨。access path が安定してから optimizer を集約)
- **ARCH-8** → 独立 (DSL/source-gen のみ。いつでも着手可)

依存先が未完でも常に「先に完了」とは限らない (interface stub 先行 / scope を絞る等は SKILL.md フロー4の判断指針を参照)。

---

## §8. 共通の進め方 (全タスク)

- **着手前合意必須**: 各タスクの設計案 (ARCH-4 のスーパーブロック設計、ARCH-5b の ID パック案、等) は破壊変更が大きいため**実装前にユーザへ提示して承認を得る** (二段階承認の一段目)。
- **ビルド/テスト**: 変更ごとに `dotnet build Quiver.slnx`、節目で `dotnet test Quiver.slnx`。CLAUDE.md の「各タスクで dotnet build 成功を確認」を厳守。
- **一括テキスト変換**: rename 等は **perl / sed などバイト安全ツール**を使う。**PowerShell 5.1 の `Set-Content` は UTF-8 日本語コメントを mojibake 化するため使用禁止** (ARCH-1 で事故)。
- **format bump**: 各 Phase で `FormatVersion` を進める (develop ゆえマイグレーション不要、旧フォーマットは `FormatVersionMismatchException`)。
- **コミット**: タスク (サブステップ) ごとに別コミット。機械的変更と意味的変更は可能なら分ける。コミットメッセージ末尾に `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`。
- **PublicApi baseline**: 公開面が変わるタスク (ARCH-5b の ID、ARCH-7/8 の DSL) は意図した変更であることを diff で確認してから `Quiver.approved.txt` を再承認。
- **完了承認後**: SKILL.md 分類 K の該当行を `✅` に更新し、本指示書の該当 § に commit hash 付き 1〜2 行の完了済み要約を追記 (ARCH-2/3 と同形式)。

---

## §9. 起動方法

新規セッションで以下のいずれかを入力すると `quiver-implement` スキルがトリガし、本指示書が読まれる。

### 起動トリガ例
- ARCH-4: 「**ARCH-4 を実施して**」 / 「**単一ファイル化 (Phase 1)**」 / 「**単一ファイル Pager + buffer pool 配線**」
- ARCH-5: 「**ARCH-5 を実施して**」 / 「**ストアのテナント化**」 / 「**ID を Kind+Gen+Seq に**」 / 「**property 格納再設計**」
- ARCH-6: 「**ARCH-6 を実施して**」 / 「**ベクトル in-file 永続化**」 / 「**SetVector を tx 配下に**」
- ARCH-7: 「**ARCH-7 を実施して**」 / 「**単一 LogicalPlan IR**」 / 「**optimizer 集約 / KNN 押し下げ移設**」
- ARCH-8: 「**ARCH-8 を実施して**」 / 「**Hop 型保存トラバーサル**」 / 「**Out<Knows>() の型保存**」

### 起動後にスキルが行うこと (期待動作)
1. SKILL.md 分類 K で ARCH-N を特定。
2. **本指示書 (`plans/arch4-to-arch8-rearchitecture-phases.md`) を Read**。
3. 該当 § の「読むべきファイル」を Read し、「設計案」をユーザへ提示して**着手承認**を得る。
4. 「実装手順」に沿って実装 → build/test → 「完了条件」充足を確認。
5. ユーザの**完了承認**後に SKILL.md 該当行を `✅` へ更新し、本指示書に完了済み要約を追記。
