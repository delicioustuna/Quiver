# ARCH-2 / ARCH-3 指示書 — API 露出修正＋型名修正＋インデックス Generation 導入

> 作成: 2026-05-31 / 対象ブランチ: develop / 前提: ARCH-1 Phase 0 完了 (commit 014973e, c09cd24)
> 位置づけ: 抜本再設計 (docs/design/11_rearchitecture_master_plan.md) の **後続フェーズ最初の 2 タスク**。
> 実行: **別スレッド (新規セッション) で `quiver-implement` スキル経由で着手する**。起動方法は本文末尾「§5 別スレッドでの起動方法」を参照。
> 互換性: develop 段階につき public API・オンディスクフォーマットの破壊的変更を許容 (マイグレーション不要、`FormatVersion` を進めてよい)。

この指示書は **2 つの独立タスク**を定義する。依存関係は **ARCH-2 → ARCH-3**（ARCH-2 で API 面と型名を固めてから、ARCH-3 で Generation を載せると手戻りが少ない）。ただし ARCH-3 を先行しても破綻はしない（接続点は §3 に明記）。

---

## §1. 背景 (なぜ今やるか)

ARCH-1 でエンジン中核 9 プロジェクトを単一アセンブリ `Quiver` に統合した結果:

1. **別アセンブリ時代に `public` だった多数の実装型が、単一アセンブリの公開 API 面にそのまま露出している。**
   PublicApi baseline (`tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt`) は **242 個の public 型**を含み、`Quiver.Storage.Records` / `Quiver.Query.Physical` / `Quiver.Storage.Wal` / `Quiver.Index` / `Quiver.Storage` といった**本来は内部実装である名前空間の型まで公開扱い**になっている。統合前は「別アセンブリの public」=「実質内部」で済んでいたものが、統合後は利用者に対する API 約束になってしまう。これは保守の足枷であり、再設計フェーズの破壊変更を妨げる。

2. **NodeId / RelationshipId が sequence (`long`) のみで Generation を持たない。**
   vacuum (OP-3/OP-5) が free list で slot を物理再利用するため、削除→回収された slot に別エンティティが割り当たると、外部保持の古い ID や `long` キーのインデックスエントリ・ベクトル binding が**別の生存エンティティを指す** (ABA / stale 参照)。インデックスはこの危険の最前線：B+Tree は値として裸の `long` (= `NodeId.Value`) を格納し ([IBTreeIndex.Insert(key, long value)](../src/Quiver/Index/IBTreeIndex.cs))、FT-22 orphan GC は `Func<long,bool> isLive` で生存判定している ([IIndexManager.CollectOrphans](../src/Quiver/Index/IBTreeIndex.cs))。Generation が無いため「slot は生きているが世代が違う (= 別物)」を判別できない。

本指示書はこの 2 点を **ARCH-2 (露出 + 型名)** と **ARCH-3 (インデックス Generation)** として扱う。

---

## §2. ARCH-2 — API 露出修正 + それに合わせた型名修正

### 目的
単一アセンブリの公開 API を「利用者が実際に使うべき表層 (facade / DSL / 安定 ID / スキーマ / オプション)」に絞り込み、実装レイヤーを `internal` 化する。露出を絞る過程で、公開に残す型・内部化する型の**命名を再設計後の役割に合わせて整える**（型名修正）。

### 読むべきファイル (着手前に必ず Read)
- `docs/design/11_rearchitecture_master_plan.md` §1.3 (目標名前空間ツリー) / §6.2 (層と「グラフ意味の認識」)
- `tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt` (現 242 型の全リスト = 露出の現状)
- `tests/Quiver.PublicApi.Tests/PublicApiApprovalTests.cs` (承認テストの仕組み)
- `docs/api-stability.md` (DOC-2。SemVer 約束と experimental マーキングの正本)
- `src/Quiver/AssemblyAttributes.cs` (InternalsVisibleTo 集約先)

### 公開 API の方針 (どこまで残すか)
**「公開に残す」= 利用者が直接触る facade と、その引数・戻り値に現れる型**。下表を初期分類とし、実装で精査する。

| 区分 | 名前空間 / 型 | 方針 |
|---|---|---|
| **公開維持** | `Quiver` ルート: `GraphDatabase`, `GraphDatabaseOptions`, `IGraphTransaction`, `QueryResult`, `QueryRow`, `IQueryCursor`, `ISchemaApi`, `IDiagnosticsApi`, `GraphStats`, 例外型 | facade。維持 |
| **公開維持** | `Quiver.Api` (旧 Client): `GraphTraversalSource`, `GraphTraversal<T>`, `TypedGraphTraversal<T>`, `IGraphNode<T>`, `IGraphRelationship<T>`, `P`, `PropertyPredicate`, `[GraphNode]`/`[GraphRelationship]`/`[GraphProperty]`/`[GraphIndexed]` 属性 | DSL。維持 |
| **公開維持** | `Quiver.Core`: `NodeId`, `RelationshipId`, `LabelId`, `RelationshipTypeId`, `PropertyKeyId`, `TransactionId`, `Direction`, `IVectorStore` 系の利用者向け契約 | 安定 ID / 値型。維持 |
| **公開維持** | `Quiver.Storage.Records`: `PropertyValue`, `PropertyValueType` (facade のシグネチャに現れる) | 利用者が値を作るのに必要。維持 |
| **内部化候補** | `Quiver.Query.Physical` (旧 Operators) のオペレータ実装群 (`IPhysicalOperator`, `*Operator`, `TupleSchema`, `TupleRef`, `OperatorStatistics` 等) | DSL の裏側。`internal` へ。ただし `IGraphTransaction.Execute(IPhysicalOperator)` が公開シグネチャに使う場合は、その口を見直す (§2「facade の口」参照) |
| **内部化候補** | `Quiver.Storage.Records` の store 実装 (`NodeStore`, `RelationshipStore`, `PropertyStore`, `BlobStore`, `AdjacencyBlockStore*`, `EntityVersionStore`, `BulkLoader` 等) | `internal`。`BulkLoader`/`StreamingBulkLoader` は facade 経由なら口だけ公開 |
| **内部化候補** | `Quiver.Storage` (Pager), `Quiver.Storage.Wal`, `Quiver.Index`, `Quiver.Transactions` の実装型 | ほぼ全て `internal` |
| **要判断** | `Quiver.Logical`, `Quiver.Maintenance`, `Quiver.Migrations` | facade から使う最小限のみ公開 (`IMigration`, `MigrationResult`, `VacuumOptions`, `VacuumReport`) |

> **判断基準**: 「利用者がこの型を**名前で書く**必要があるか？」が Yes なら公開、No なら `internal`。
> 例: `db.BeginTransaction()` の戻り値 `IGraphTransaction` は公開、その内部で動く `Transaction` は internal。

### facade の口の見直し (型名修正と連動)
内部化に伴い、公開シグネチャが内部型を漏らしている箇所を是正する。代表例:
- `IGraphTransaction.Execute(IPhysicalOperator plan)` / `ExecuteCursor(...)` は `IPhysicalOperator` (内部化したい型) を公開引数に取る。
  → 物理プランを利用者が直接組む API を**公開面から外す** (DSL の `GraphTraversal<T>` 終端のみを公開経路にする)。物理プラン実行は `internal` メソッド + `InternalsVisibleTo` 経由でテストから叩く。
- 同様に `IGraphTransaction.Access` (`IGraphAccessMethods`)、`AdjacencyBlocks` (`IAdjacencyBlockStore`) など内部抽象を返すプロパティを公開面から除く。

### 型名修正 (露出整理に「合わせて」行う)
公開に残す型は再設計後の役割に沿った名前に整える。**この指示書では具体的な新名を断定しない**（公開面を絞り込んだ後の残存型を見て、実装スレッドが下記の原則で命名し、着手前にユーザへ新旧対応表を提示して承認を得る）:
- 原則 1: facade 公開型は実装詳細を含意しない名前 (例: `BinaryGraphStorageBackendFactory` のような実装名は公開面に出さない or `internal` 化)。
- 原則 2: `Quiver.Storage.Records` に残す公開値型 (`PropertyValue` 等) は名前変更しない (利用頻度が高く破壊コストが見合わない)。
- 原則 3: ID 系 (`NodeId` 等) は ARCH-3 で内部表現が変わるが**型名は変えない** (公開シンボルの安定性優先)。
- **新旧対応表をユーザに提示して承認を得てから rename を適用する** (機械リネームは ARCH-1 で確立した perl ベースのバイト安全手順を踏襲。PS 5.1 の Set-Content は UTF-8 を壊すため禁止)。

### 実装手順
1. `Quiver.approved.txt` を分類表 (§2) に従って「公開維持 / 内部化 / 要判断」へ仕分けし、**仕分け結果の一覧をユーザに提示して承認を得る** (大量の破壊変更のため、着手前合意を必須とする)。
2. 内部化対象の型・メンバーを `public` → `internal` に変更。テストは `InternalsVisibleTo` で引き続きアクセス可能 ([AssemblyAttributes.cs](../src/Quiver/AssemblyAttributes.cs) に必要なテストアセンブリが登録済み)。
3. facade の口から内部型が漏れている箇所を是正 (§2「facade の口」)。
4. 承認済みの型名修正を適用 (perl ベースのバイト安全置換)。
5. satellite (Storage.Sqlite / Hosting / OpenTelemetry / Embedding) が内部化で壊れないか確認。壊れる場合は (a) 必要 API を公開に戻す か (b) satellite を `InternalsVisibleTo` に追加する かを型ごとに判断 (原則は (b) を避け、satellite が真に必要とする最小 API のみ公開)。
6. PublicApi baseline (`Quiver.approved.txt`) を**絞り込み後のサーフェスで再生成・再承認**。差分が「意図した内部化 + 承認済み rename のみ」であることを diff で確認する。

### 完了条件
- `dotnet build Quiver.slnx` 0 errors。
- 全テスト緑 (`dotnet test Quiver.slnx`、現状ベースライン 1133 passed)。
- PublicApi の公開型数が大幅に減少し (目安: 242 → facade + DSL + ID + 値型の数十程度)、baseline が新サーフェスで承認済み。
- 公開シグネチャに内部実装型 (`IPhysicalOperator` 等) が漏れていない。
- 型名修正の新旧対応がユーザ承認済みで、PublicApi diff と一致する。

---

## §3. ARCH-3 — インデックスへの Generation 導入

### 目的
B+Tree インデックスが格納する「値」を、裸の `long` (sequence のみ) から **Generation を含む形**へ拡張し、slot 再利用に伴う stale 索引エントリが別の生存エンティティを指す問題 (ABA) を検出可能にする。これは docs/design/11 §3 の「Kind+Generation+Sequence ID」の**インデックス側の先行実装**であり、ID 全体の再設計 (Phase 2) のうち**索引が触れる範囲に限定したスコープ**とする。

### スコープの明確化 (重要)
- **やる**: インデックスの値レーンに Generation を載せ、Seek/Range 結果の解決時に「現在の slot 世代」と突き合わせて stale を弾く。FT-22 orphan GC の生存判定を世代対応にする。
- **やらない (Phase 2 へ)**: NodeId 物理表現そのものの全面変更 (record レイアウト・WAL・ベクトル binding 全体)。本タスクは**インデックス値の表現と解決経路に限定**する。
- ベクトル binding ((EntityKind, long) キー) への Generation 適用は Phase 3 (vector 永続化) と一緒に行うため、本タスクの対象外。

### 設計判断 (実装スレッドが決め、着手前にユーザへ提示)
以下を `docs/design/11` §3 と整合する形で具体化し、**着手前にユーザへ設計案を提示して承認を得る**:

1. **Generation の供給元**: slot ごとの世代カウンタをどこが持つか。
   候補 A: `NodeStore` / `RelationshipStore` の version sidecar ([EntityVersionStore](../src/Quiver/Stores/EntityVersionStore.cs)) に Generation レーンを追加し、Allocate 時に発番、Free→再 Allocate で +1。
   候補 B: 専用の generation 配列を別管理。
   → 既存 sidecar (FT-31/32) に相乗りする候補 A を第一候補とする (cache line・WAL 連動を流用できる)。
2. **インデックス値のエンコード**: `IBTreeIndex<TKey>.Insert(key, long value)` の `long` に Generation を packing するか (例: 上位 16bit = generation, 下位 48bit = sequence)、それとも値レーンを拡張 (8B→可変) するか。
   → packing 案を第一候補とする (B+Tree ページレイアウト・codec を変えずに済む。docs/design/11 §3 の 64bit パック案と整合)。48bit sequence で 281 兆エンティティ、16bit generation で slot あたり 65,536 回再利用まで。
3. **解決経路 (stale 検出のタイミング)**: Seek/Range が返す値を呼び出し側 (`GraphTransaction.SeekIndex` / `MergeNode` の index 経路 / FilteredKnn 等) で `NodeId` に解決する際、packing された generation と「現在の slot generation」を比較し、不一致なら**「存在しない」として skip** (FT-30 の defensive read 契約と同じ挙動)。
4. **orphan GC の世代対応**: [IIndexManager.CollectOrphans](../src/Quiver/Index/IBTreeIndex.cs) の `Func<long,bool> isLive` を「世代込みの値」を受け取る形に拡張し、「slot は生きているが世代が違う」エントリも orphan として回収できるようにする。
5. **フォーマットバージョン**: 索引値レイアウトが変わるため `FormatVersion` を進める ([FormatVersion.cs](../src/Quiver/Core/FormatVersion.cs))。develop ゆえマイグレーション不要。

### 読むべきファイル (着手前に必ず Read)
- `docs/design/11_rearchitecture_master_plan.md` §3 (ID 設計) / §7.4 (MVCC bloat と Generation)
- `docs/design/05_btree_index.md` (B+Tree 設計の正本)
- `src/Quiver/Index/IBTreeIndex.cs` (値レーン `long` の契約、orphan GC の生存判定)
- `src/Quiver/Index/IndexManager.cs` / `src/Quiver/Index/BTreeIndex.cs`
- `src/Quiver/Stores/NodeStore.cs` (Allocate/Free と slot 再利用、sidecar Write)
- `src/Quiver/Stores/EntityVersionStore.cs` / `IEntityVersionStore.cs` (Generation 相乗り先の候補)
- `src/Quiver/GraphTransaction.cs` (SeekIndex / RangeIndex / MergeNode の index 解決経路)
- `src/Quiver/Core/Ids.cs` (NodeId 等の現定義) / `src/Quiver/Core/FormatVersion.cs`

### 実装手順
1. 上記「設計判断」5 点を具体化し、**設計案 (Generation 供給元・エンコード・解決経路・GC・format bump) をユーザへ提示して承認を得る**。
2. Generation 供給元を実装 (候補 A なら sidecar にレーン追加 + Allocate/Free で発番)。
3. インデックス値の packing/unpacking ヘルパを追加 (`Quiver.Core` か `Quiver.Index` の internal)。Insert 時に generation を載せ、Seek/Range 解決時に取り出す。
4. `GraphTransaction` の index 解決経路で世代照合を入れ、不一致を skip。
5. FT-22 orphan GC を世代対応に拡張。
6. `FormatVersion` を進める。
7. テスト追加 (§3 完了条件参照)。

### 完了条件
- `dotnet build Quiver.slnx` 0 errors / 全テスト緑。
- **新規テスト**: 「ノード作成 → 索引登録 → 削除 → vacuum で slot 回収 → 同 slot に別ノード作成 → 古い索引キーで Seek すると stale を検出して空を返す (別ノードを誤って返さない)」を検証する回帰テストが緑。
- FT-22 orphan GC が「世代違いエントリ」を回収できることのテストが緑。
- `FormatVersion` が進み、旧フォーマット DB を開くと `FormatVersionMismatchException` (develop の既定挙動)。
- ベンチ (`Quiver.Benchmarks`) で索引 Seek/Insert のスループットが許容範囲 (packing のオーバーヘッドが有意な退行を生まない。回帰 sentinel TS-6 の 20% 閾値内)。

---

## §4. 共通の進め方 (両タスク)

- **着手前合意必須**: ARCH-2 の内部化仕分けと型名対応表、ARCH-3 の設計案は、いずれも破壊変更が大きいため**実装前にユーザへ提示して承認を得る** (quiver-implement スキルの「完了承認」とは別に、本 2 タスクは「着手承認」も要する)。
- **ビルド/テスト**: 変更ごとに `dotnet build Quiver.slnx`、節目で `dotnet test Quiver.slnx`。CLAUDE.md の「各タスクで dotnet build 成功を確認」を厳守。
- **一括テキスト変換**: rename 等は **perl / sed などバイト安全ツール**を使う。**PowerShell 5.1 の `Set-Content` は UTF-8 日本語コメントを mojibake 化するため使用禁止** (ARCH-1 で実際に事故ったため厳守)。
- **コミット**: ARCH-2 と ARCH-3 は別コミット。内部化 (機械的) と型名修正 (意味的) も可能なら別コミットにして review しやすくする。コミットメッセージ末尾に Co-Authored-By 行を付ける (CLAUDE.md / 環境規約準拠)。
- **PublicApi baseline**: 公開面が変わるため、意図した変更であることを diff で確認してから `Quiver.approved.txt` を再承認する。

---

## §5. 別スレッドでの起動方法

本指示書は `quiver-implement` スキルの新タスク **ARCH-2 / ARCH-3** として登録済み (SKILL.md 分類 K)。新規セッション (別スレッド) で以下のいずれかを入力すると、スキルがトリガされ本指示書が読まれる。

### 起動トリガ例 (どれか 1 つを新スレッドで入力)
- ARCH-2: 「**ARCH-2 を実施して**」 / 「**API 露出を絞って (public→internal)**」 / 「**公開サーフェスを facade に絞る**」 / 「**型名修正と内部化**」
- ARCH-3: 「**ARCH-3 を実施して**」 / 「**インデックスに Generation を導入**」 / 「**索引値に世代を載せて stale 参照を検出**」 / 「**generational index**」

### 起動後にスキルが行うこと (期待動作)
1. `quiver-implement` SKILL.md の分類 K で ARCH-2/ARCH-3 を特定。
2. **本指示書 (`plans/arch2-api-surface-and-arch3-index-generation.md`) を Read**。
3. §2 / §3 の「読むべきファイル」を Read し、「設計案 / 仕分け」をユーザへ提示して**着手承認**を得る。
4. 「実装手順」に沿って実装 → build/test → 「完了条件」充足を確認。
5. ユーザの**完了承認**後に SKILL.md の該当行を `✅` へ更新。

### 推奨順序
**ARCH-2 を先に**完了させてから ARCH-3 に着手する (API 面と型名が固まってから Generation を載せる方が手戻りが少ない)。ただし独立着手も可能 (§ 冒頭の依存注記参照)。

### 明示的にエージェントへ渡す場合のプロンプト雛形
新スレッドで以下をそのまま貼ってもよい:

```
Quiver の ARCH-2 (API 露出修正 + 型名修正) を実施したい。
plans/arch2-api-surface-and-arch3-index-generation.md を読み、§2 の手順に従って、
まず公開/内部化の仕分けと型名対応表を提示して着手承認を求めてから実装して。
```

```
Quiver の ARCH-3 (インデックスへの Generation 導入) を実施したい。
plans/arch2-api-surface-and-arch3-index-generation.md を読み、§3 の手順に従って、
まず設計案 (Generation 供給元/値エンコード/解決経路/GC/format bump) を提示して
着手承認を求めてから実装して。
```
