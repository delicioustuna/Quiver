# HYP トラック実装タスク

> 親計画書は `plans/hyperedge-track.md`。
> 本書は HYP-1 から HYP-7 を、単独でビルドと検証ができる増分へ分割する。
> 実装中に確定した仕様は本書ではなく `docs/spec/` へ反映する。

## 実装契約

親計画書の決定に加え、実装タスクは次の契約を前提とする。

| 項目 | 契約 |
|---|---|
| メンバー | v1 は `NodeId` のみを許可する |
| アリティ | 2 以上を要求する |
| 重複 | 同じ `Role` と `NodeId` の組は拒否する |
| 同一ノードの複数ロール | 許可する |
| 同一ロールの複数ノード | 許可する |
| メンバー順序 | 順序を保証しない |
| メンバー変更 | 作成後は不変とし、変更は削除後の再作成で表す |
| ノード削除 | そのノードを含む生存ハイパーエッジを同じトランザクションで削除する |
| 可視性 | `HyperedgeStore` の版を可視性の正本とし、incidence は独立した MVCC エンティティにしない |
| `OtherMembers` | 直前の node から hyperedge への展開元ノードを除外する |
| `EntityKind` | `Hyperedge` を追加し、`Incidence` は追加しない |
| バックエンド | binary と in-memory を同じストア実装で対応する |
| 互換性 | `FormatVersion` を V3 へ上げ、旧 DB の読み替え処理は実装しない |

`RoleId` と `IncidenceId` は internal 型にする。
公開 API はロールを文字列で受け取り、`HyperedgeMember` は `Role` と `NodeId` を保持する。

## 実験ループ

spike は本実装の前提を決めるためのコードであり、そのまま製品コードへ昇格させない。

各 spike は次の順序で実行する。

1. 比較対象、データセット、採否基準を先にコミットする。
2. `benchmarks/Quiver.Benchmarks/Experimental/` に最小実装を置く。
3. Release 構成で同一マシン上の交互実行を行い、中央値と割り当て量を記録する。
4. 結果と決定を本書の「決定記録」へ追記する。
5. 不採用コードを削除し、採用した契約だけを次の本実装へ渡す。

ベンチマーク結果のばらつきが 5% を超える場合は、プロセス再起動を含む 3 回以上の再実行で判定する。

## 依存順

```text
HYP-0
  └─ HYP-S1
       └─ HYP-1a → HYP-1b → HYP-1c → HYP-1d
                                  └─ HYP-2a → HYP-2b → HYP-2c
                                                       └─ HYP-3a → HYP-3b → HYP-3c
                                                                    ├─ HYP-4
                                                                    └─ HYP-S2 → HYP-5a → HYP-5b
       HYP-1c + HYP-2b ────────────────────────────────────────────────→ HYP-6a
       HYP-3c ─────────────────────────────────────────────────────────→ HYP-6b
       HYP-1d + HYP-2c + HYP-3c ──────────────────────────────────────→ HYP-6c
       HYP-6c の判定で必要な場合のみ ──────────────────────────────────→ HYP-6d
       HYP-4 + HYP-5b + HYP-6a + HYP-6b + HYP-6c (+ HYP-6d) ─────────→ HYP-7
```

HYP-4 と HYP-S2 以降は、HYP-3c 完了後に独立して進められる。

## 共通完了条件

各増分は次の条件を満たしてから後続へ進む。

- `dotnet build Quiver.slnx` が 0 errors で完了する。
- 変更箇所に対応するテストプロジェクトが成功する。
- 公開 API を変更した増分は `Quiver.PublicApi.Tests` の承認ファイルを更新する。
- 永続レイアウトを変更した増分は、再オープン、rollback、savepoint、クラッシュ復旧を検証する。
- spike は計測値と採否理由を記録し、不採用コードを残さない。

## HYP-0 トラック配線

### 目的

実装前にタスク管理と設計参照先を現行リポジトリへ接続する。

### 変更

- `.agents/skills/quiver-implement/tasks/hyperedge.md` を作成し、本書の各増分、読むべきファイル、完了条件を登録する。
- `.claude/skills/quiver-implement/tasks/hyperedge.md` から同じタスク定義を参照できるようにする。
- `.agents/skills/quiver-implement/SKILL.md` と `.claude/skills/quiver-implement/SKILL.md` に HYP 分類、依存関係、進捗行を追加する。
- `docs/design/roadmap.md` に HYP-1 から HYP-7 の epic と本書へのリンクを追加する。
- roadmap の既存 `.claude` 参照を維持し、`.agents` 参照を追記する。

### 完了条件

- HYP タスクを skill から一意に特定できる。
- 親計画、実装タスク、roadmap の ID と依存が一致する。

## HYP-S1 ノード incidence head 配置 spike

### 仮説

`VersionedNodeStore` の payload を 15 バイトから 21 バイトへ広げる案は、hyperedge を使わない binary ワークロードにも読み取り帯域の増加を課す。
別テナントの `NodeIncidenceHeadStore` に 6 バイト head を置けば、binary ホットパスを変えずに node から incidence を O(1) で引ける。

### 比較案

- **案 A**：`VersionedNodeStore` payload に `FirstIncidenceId` を追加する。
- **案 B**：node sequence を添字とする 6 バイト固定長の `NodeIncidenceHeadStore` を別テナントに置く。

### 計測

- node 数は 100,000 と 1,000,000 の二通りとする。
- incidence を持つ node の割合は 1%、10%、100% とする。
- binary `Read` と binary 1-hop expand の degree は 10、100、1,000 とする。
- hyperedge head lookup の p50、binary 経路の p50、データサイズ、1 操作あたりの割り当て量を測る。

### 採否基準

- 案 A は binary `Read` と binary expand の p50 回帰がともに 3% 以下の場合だけ採用できる。
- 案 B は hyperedge head lookup が案 A の 1.5 倍以内なら採用する。
- 両方を満たす場合は、binary 経路を変えない案 B を採用する。

### 完了条件

- 「決定記録」に計測環境、数値、採用案を記録する。
- 後続タスクの node head 読み書き先が一つに確定している。

## HYP-1a ID、トークン、永続テナント

### 目的

レコード実装を置ける ID 空間、トークン空間、単一ファイル内テナントを確保する。

### 主な変更先

- `src/Quiver/Core/Ids.cs`
- `src/Quiver/Core/EntityId.cs`
- `src/Quiver/Core/FormatVersion.cs`
- `src/Quiver/Stores/HyperedgeTypeTokenStore.cs`
- `src/Quiver/Stores/RoleTokenStore.cs`
- `src/Quiver/Backend/BinaryGraphStorageBackendFactory.cs`
- `tests/Quiver.Stores.Tests/TokenStoreTests.cs`
- `tests/Quiver.Tests/FormatVersionTests.cs`

### 実装

- 公開 `HyperedgeId` と `HyperedgeTypeId` を追加する。
- internal `RoleId` と `IncidenceId` を追加する。
- `EntityKind.Hyperedge`、`EntityId.FromHyperedge`、`EntityId.AsHyperedge` を追加する。
- hyperedge type と role の独立トークンストアを追加する。
- 固定テナント 18 から 24 を hyperedge heap、map、version、incidence heap、incidence map、hyperedge type token、role token に割り当てる。
- HYP-S1 で案 B を採用した場合は、固定テナント 25 を node incidence head に割り当てる。
- `FormatVersion.Current` を V3 へ上げる。

### テスト

- ID の sentinel、sequence、generation、equality を既存 ID と同じ契約で検証する。
- type と role が別空間で同名を保持でき、再オープン後も同じ ID を返すことを検証する。
- V2 DB を V3 実装で開くと `FormatVersionMismatchException` になることを検証する。

## HYP-1b HyperedgeStore と IncidenceStore

### 目的

MVCC 可視な hyperedge header と、二方向に辿れる incidence を実装する。

### 主な新規ファイル

- `src/Quiver/Stores/IHyperedgeStore.cs`
- `src/Quiver/Stores/VersionedHyperedgeStore.cs`
- `src/Quiver/Stores/IIncidenceStore.cs`
- `src/Quiver/Stores/IncidenceStore.cs`
- `tests/Quiver.Stores.Tests/HyperedgeStoreTests.cs`
- `tests/Quiver.Stores.Tests/IncidenceStoreTests.cs`

HYP-S1 で案 B を採用した場合は、`NodeIncidenceHeadStore.cs` と対応テストも追加する。

### レコード契約

`HyperedgeStore` の固定領域は 15 バイトとする。

```text
Flags(1) | TypeId(2) | FirstIncidenceId(6) | FirstPropertyId(6)
```

`IncidenceStore` の固定領域は 33 バイトとする。

```text
Flags(1) | HyperedgeId(6) | NodeId(6) | RoleId(2)
| PrevInNode(6) | NextInNode(6) | NextInHyperedge(6)
```

`PrevInNode` は vacuum 時の unlink を O(arity) に保つために持つ。
メンバー集合は不変なので `PrevInHyperedge` は持たない。

### 実装

- `VersionedRecordHeap` と `ItemPointerMap` を使って hyperedge header を管理する。
- hyperedge の xmin と xmax は version header、generation と SSN stamp は version sidecar に置く。
- incidence は header の可視性に従い、独立した xmin と xmax を持たない。
- node からの列挙は物理 chain 上の incidence を読み、対応する hyperedge header が不可視なら skip して次へ進む。
- hyperedge からの列挙は `NextInHyperedge` を辿る。
- 作成時は全メンバーを検証してから header、incidence、node head を書く。
- node chain は head insert とし、旧 head の `PrevInNode` を更新する。
- `Scan()` は可視な `HyperedgeId` だけを sequence 順に返す。
- `Read`、`Scan`、incidence 列挙で可視な header を観測したときは `MvccContext.RecordRead(EntityKind.Hyperedge, sequence)` を呼ぶ。

### テスト

- arity 2、4、16 の作成と両方向列挙を検証する。
- 同じ node が別 role で参加する場合と、同じ role に複数 node が参加する場合を検証する。
- 不可視な新規 header と論理削除済み headerを、異なる snapshot から検証する。
- node chain と hyperedge chain の終端、head 更新、prev 更新を検証する。
- 読み取り中に不可視 incidence が chain の途中にあっても後続を失わないことを検証する。

## HYP-1c トランザクション、MVCC、WAL

### 目的

hyperedge ストアを通常の transaction、locking、SSN、rollback、recovery に接続する。

### 主な変更先

- `src/Quiver/Transactions/ITransaction.cs`
- `src/Quiver/Transactions/Transaction.cs`
- `src/Quiver/Transactions/TransactionManager.cs`
- `src/Quiver/Transactions/TxHyperedgeStore.cs`
- `src/Quiver/Backend/BinaryGraphStorageBackend.cs`
- `src/Quiver/Backend/BinaryGraphStorageBackendFactory.cs`
- `src/Quiver/Transactions/RecoveryManager.cs`
- `tests/Quiver.Transactions.Tests/`
- `tests/Quiver.Backend.Tests/`

### 実装

- `ITransaction.Hyperedges` と `TxHyperedgeStore` を追加する。
- hyperedge 用 `LockManager` と version sidecar を `TransactionManager` へ渡す。
- deadlock detector の監視対象に hyperedge lock を追加する。
- `Transaction.StoreFor` と SSN の read set、write set を `EntityKind.Hyperedge` に対応させる。
- create 時の node lock は node sequence 昇順で取得する。
- delete と property 更新は hyperedge sequence の排他 lock を取得する。
- store metadata の reload、abort undo、savepoint rollback に hyperedge と incidence のテナントを含める。
- PageImage WAL は既存の tenant page 経路を再利用し、hyperedge 専用 WAL record は追加しない。
- recovery 後に hyperedge header、incidence chain、node head が同じ commit 境界へ戻ることを保証する。

### テスト

- commit、明示 rollback、例外 dispose、nested savepoint を検証する。
- 作成途中、node head 更新後、commit record 前の crash injection を検証する。
- Serializable で hyperedge の read-write dependency が SSN に記録されることを検証する。
- reader-writer locking で node head と hyperedge header の読書きが破綻しないことを検証する。
- binary と in-memory の backend contract に同じ基本 CRUD を追加する。

## HYP-1d incidence 走査性能 spike

### 仮説

role で絞った co-membership 1-hop は、binary relationship 1-hop の p50 の 3 倍以内に収まる。

### 計測

- degree は 10、100、1,000 とする。
- hyperedge arity は 4 とし、`subject` から `object` への 1 件出力と binary 1 件出力を比較する。
- ストア enumerator を直接使い、クエリ DSL の構築コストを含めない。
- warm p50、p95、1 操作あたりの logical page read、割り当て量を測る。

### 判定

- 全 degree で p50 が binary の 3 倍以内なら現レイアウトを維持する。
- 一つでも超えた場合は HYP-6d を必須化する。
- 走査中の managed allocation が発生した場合は、比率にかかわらず HYP-3a 着手前に列挙子を修正する。

## HYP-2a 公開 CRUD と削除カスケード

### 目的

アプリケーションが transaction API から hyperedge を作成、取得、削除できるようにする。

### 公開 API

```csharp
public readonly record struct HyperedgeMember(string Role, NodeId NodeId);

HyperedgeId CreateHyperedge(
    string type,
    ReadOnlySpan<HyperedgeMember> members);

HyperedgeId CreateHyperedge(
    HyperedgeTypeId typeId,
    ReadOnlySpan<HyperedgeMember> members);

void DeleteHyperedge(HyperedgeId hyperedgeId);

HyperedgeMemberEnumerator GetMembers(
    HyperedgeId hyperedgeId,
    string? role = null);

HyperedgeIdEnumerator GetHyperedges(
    NodeId nodeId,
    string? type = null,
    string? role = null);
```

### 実装

- `ISchemaApi` に hyperedge type と role の get、try-get、name lookup、list API を追加する。
- role 名と type 名は null、空文字、空白だけの文字列を拒否する。
- create は arity、重複、全 node の可視性を mutation 前に検証する。
- `DeleteHyperedge` はプロパティを論理削除してから header を論理削除する。
- `DeleteNode` は relationship と hyperedge を先に収集し、重複を除いてから削除する。
- `GetMembers` と `GetHyperedges` は read-your-own-writes と snapshot visibility を保つ。
- 存在しない ID の delete は no-op とし、get は空列挙を返す。

### テスト

- 入力検証が失敗したときに header、incidence、token 以外のデータ変更が残らないことを検証する。
- node 削除が複数の hyperedge を一度ずつ削除することを検証する。
- 古い snapshot は node と hyperedge の両方を引き続き観測できることを検証する。
- role と type の全フィルタ組み合わせを検証する。

## HYP-2b プロパティと logical mutation

### 目的

hyperedge を node と relationship と同じ property entity として扱い、論理変更ストリームを再生できるようにする。

### 主な変更先

- `src/Quiver/IGraphTransaction.cs`
- `src/Quiver/GraphTransaction.cs`
- `src/Quiver/Logical/LogicalMutationKind.cs`
- `src/Quiver/Logical/LogicalMutation.cs`
- `src/Quiver/Logical/LogicalMutationReplay.cs`
- `src/Quiver/Stores/InlinePropertyCodec.cs`
- `tests/Quiver.Tests/LogicalMutationTests.cs`
- `tests/Quiver.Tests/ColumnWriteIntegrationTests.cs`

### 実装

- hyperedge 用の `SetProperty`、`GetProperty`、`HasProperty`、`RemoveProperty`、`EnumerateProperties` を追加する。
- Set cardinality 用の add、remove、get-values overload も追加する。
- 15 バイト固定領域の後ろに既存 `InlinePropertyCodec` を適用する。
- overflow property は `FirstPropertyId` から既存 `PropertyStore` を使う。
- `CreateHyperedge` mutation は type 名と role 名付き member 配列を保持する。
- delete、set、remove、multi-value mutation を追加し、replay 時に node ID と hyperedge ID を remap する。
- column 対応は `EntityKind.Hyperedge` を受理できるようにするが、列作成の公開糖衣は HYP-6b まで保留する。

### テスト

- scalar、string、bytes、float array、Set cardinality を検証する。
- inline から overflow、overflow から inline の更新を検証する。
- logical mutation の capture、commit 後通知、rollback 無通知、別 DB への replay を検証する。
- property 更新を含む savepoint rollback と crash recovery を検証する。

## HYP-2c WAL 増幅 spike

### 仮説

arity `A` の hyperedge 作成 WAL は、binary relationship 1 件の WAL の `(1 + A / 2)` 倍以内に収まる。

### 計測

- `A` は 2、4、8、16 とする。
- 1 transaction 1 件と、1 transaction 1,000 件の二通りを測る。
- checkpoint 直後の `BytesWritten` 差分を使い、token 作成は warm-up で除外する。
- 同じ durability と buffer pool 設定で binary relationship と比較する。

### 判定

- 各 `A` で増幅が閾値以内かつ `A` に対して線形なら合格とする。
- 閾値超過でも PageImage の固定費で説明できる場合は batch 側を主判定とし、単件側の運用上の注意を記録する。
- batch 側が超線形なら、node head 更新と incidence page 分散を計測してレコード配置を見直す。

## HYP-3a Tuple、Logical IR、物理オペレータ

### 目的

hyperedge scan と node、hyperedge 間の展開を Volcano pipeline に追加する。

### 主な変更先

- `src/Quiver/Operators/IPhysicalOperator.cs`
- `src/Quiver/QueryResult.cs`
- `src/Quiver/Query/Logical/LogicalOp.cs`
- `src/Quiver/Query/PhysicalPlanner.cs`
- `src/Quiver/Operators/AllHyperedgesScanOperator.cs`
- `src/Quiver/Operators/ExpandToHyperedgeOperator.cs`
- `src/Quiver/Operators/ExpandMembersOperator.cs`
- `tests/Quiver.Operators.Tests/`

### 実装

- `TupleSlotType.HyperedgeId` と `QueryRow.GetHyperedgeId` を追加する。
- `ScanOp(EntityKind.Hyperedge)` を `AllHyperedgesScanOperator` へ lower する。
- `ExpandToHyperedgeOp` は source node 列を保持し、現在列を hyperedge にする。
- `ExpandMembersOp` は hyperedge 列、任意の role、任意の除外 node 列を受け取る。
- role は planner で `RoleId` へ解決し、未知 role は空結果にする。
- operator は `ref struct` enumerator を使い、1 行ごとの managed allocation を発生させない。
- optimizer の tree rewrite と shape metadata が新しい logical op を失わないようにする。

### テスト

- scan、node から hyperedge、hyperedge から member の各単独 operator を検証する。
- type、role、除外 node、carry 列、空結果、snapshot visibility を検証する。
- logical optimizer を通した後も列番号と current entity が一致することを検証する。

## HYP-3b Traversal DSL と builder

### 目的

親計画で固定した fluent API を logical IR へ接続する。

### 主な変更先

- `src/Quiver/Client/GraphTraversalSource.cs`
- `src/Quiver/Client/GraphTraversal.cs`
- `src/Quiver/Client/HyperedgeBuilder.cs`
- `tests/Quiver.Client.Tests/HyperedgeTraversalTests.cs`
- `tests/Quiver.Client.Tests/HyperedgeBuilderTests.cs`

### 実装

- `g.Hyperedges()` と `g.Hyperedge(id)` を scan と seed の起点として追加する。
- node traversal の `.Hyperedges(type?, role?)` を追加する。
- hyperedge traversal の `.Members(role?)` と `.OtherMembers(role?)` を追加する。
- `.Hyperedges()` は展開元 node 列を hidden origin として保持する。
- `.OtherMembers()` は hidden origin が無い起点では `InvalidOperationException` を投げる。
- `.Members()` 後は origin を破棄し、別の `.Hyperedges()` で新しい origin を設定する。
- `AddHyperedge(type)`、複数回の `.Member(role, nodeId)`、`.P(...)`、`.Next()` を追加する。
- builder の `Next()` は member を snapshot し、再利用時の前回状態混入を防ぐ。

### テスト

- 親計画の API 例をコンパイルして実行する。
- 同一 node の複数 role と同一 role の複数 node を DSL から作成する。
- `OtherMembers` が origin node を全 role から除外することを検証する。
- builder の未設定 type、arity 不足、重複 member、再利用を検証する。

## HYP-3c RAG クエリ表現力 spike

### 仮説

汎用 DSL の合成だけで、RAG の n 項 fact をクライアント側 materialize なしに取得できる。

### 固定シナリオ

`Fact` hyperedge は `subject`、`object`、`source`、`asOf` の四つの role を持つ。
`source` は `Chunk` node、`asOf` は時点 node とする。

次のクエリを実行可能にする。

1. subject から Fact を辿り、object と source を取得する。
2. Chunk から所属 Fact を辿り、subject と object を取得する。
3. Fact property で絞ってから role 別 member を取得する。
4. 同じ role に複数 member がいる Fact を列挙する。

### 判定

- `ToList()` を途中に挟まず、一つの operator tree で実行できれば合格とする。
- 書けない形がある場合は、`HasMember` のような汎用 primitive を一つだけ候補に加えて再検証する。
- RAG 固有名の糖衣は `Quiver.Rag` 以外へ追加しない。

## HYP-4 Match 星型パターン

### 目的

一つの hyperedge と複数の role member を同じ Match row に束縛する。

### 主な変更先

- `src/Quiver/Client/Match/GraphPattern.cs`
- `src/Quiver/Client/Match/MatchCompiler.cs`
- `src/Quiver/Client/Match/MatchQuery.cs`
- `src/Quiver/Client/MatchTuple.cs`
- `tests/Quiver.Client.Tests/MatchPatternTests.cs`

### 実装

- `GraphPattern.Hyperedge(variable, type)` と不変 `HyperedgePattern` を追加する。
- `.Member(role, NodePattern)` は複数回呼べるようにする。
- hyperedge variable と member variable の重複、未定義 variable、空 role を構築時に拒否する。
- 最初の member を label scan の anchor とし、node から hyperedge、残り role member の順に logical op を組む。
- 各 member の label と `Where` predicate を対応する列へ適用する。
- hyperedge variable の property predicate は `EntityKind.Hyperedge` として評価する。
- 同一 role の複数 member は組み合わせを放出し、同じ variable を二度束縛しない。
- `MatchTuple.Hyperedge(alias)` を追加する。

### テスト

- 2、4、可変個の member pattern を検証する。
- role、label、node property、hyperedge property の組み合わせを検証する。
- 一つの hyperedge に同じ role の候補が複数ある場合の行数を検証する。
- binary `GraphPattern.Node(...).Out(...)` の既存結果が変わらないことを検証する。

## HYP-S2 SourceGenerator API spike

### 問題

`[Role("buyer")] public Person Buyer` だけでは、生成した insert API が保存すべき `NodeId` を取得できない。
既存 `IGraphNode<T>` のインスタンスは ID を保持しないため、型付き node オブジェクトを role property に置くだけでは CRUD 契約が閉じない。

### 比較案

- **案 A**：`GraphNodeRef<TNode>` が `NodeId` を保持し、role property は `GraphNodeRef<TNode>` または `IReadOnlyList<GraphNodeRef<TNode>>` にする。
- **案 B**：role property は型宣言専用とし、生成した `Insert` が role 名ごとの `NodeId` 引数を受ける。
- **案 C**：式で role property を指定する型付き member builder を生成する。

### spike

- arity 2、arity 4、同一 role 複数 member の三つの利用例だけを生成する。
- insert、load、update、delete、typed traversal の呼び出しコードを用意する。
- nullable、`IReadOnlyList<T>`、別 namespace、同名 node 型を含めてコンパイルする。

### 採否基準

- role と node CLR 型の不一致をコンパイル時に検出できる。
- 利用者が role 名文字列を typed API で再入力しない。
- 保存対象の `NodeId` が暗黙の object identity に依存しない。
- arity に比例した生成コードで済み、組み合わせ overload を手書きしない。
- public 型の追加が最少の案を採用する。

## HYP-5a SourceGenerator CRUD

### 目的

HYP-S2 で確定した role binding と hyperedge property の CRUD を生成する。

### 主な変更先

- `src/Quiver/Client/HyperedgeAttribute.cs`
- `src/Quiver/Client/RoleAttribute.cs`
- `src/Quiver/Client/IGraphHyperedge.cs`
- `src/Quiver.SourceGen/GraphHyperedgeGenerator.cs`
- `src/Quiver.SourceGen/GraphHyperedgeModel.cs`
- `src/Quiver.SourceGen/GraphHyperedgeEmitter.cs`
- `tests/Quiver.SourceGen.Tests/HyperedgeGeneratorTests.cs`

### 実装

- 非ジェネリック `[Hyperedge]` と property 用 `[Role]` を追加する。
- `IGraphHyperedge<TSelf>.GraphType` と、可変アリティに依存しない CRUD の最小契約を追加する。
- single role と `IReadOnlyList<T>` role を区別する。
- `[Property]` と `[Role]` の同時指定、非 node 型 role、書き込み不能 property を診断する。
- generated insert は全 member を一度組み立てて `CreateHyperedge` を一回だけ呼ぶ。
- load と update は hyperedge property と role binding を別の責務として扱い、HYP-S2 の決定をそのまま実装する。

### テスト

- generated source の snapshot とコンパイル診断を検証する。
- 別 namespace、partial class、nullable、single role、multi role を検証する。
- 生成コードを実 DB に接続した round-trip test を追加する。

## HYP-5b 型付き走査

### 目的

型付き node traversal と型付き hyperedge traversal の間で role 型を保存する。

### 実装

- `Hyperedges<THyperedge>(role selector)` と `Members<TNode>(role selector)` 相当の生成糖衣を追加する。
- role selector は HYP-S2 で採用した property metadata から role 名と node 型を解決する。
- multi role は node traversal を返し、collection 自体を row に載せない。
- untyped DSL と同じ logical op を使い、別の実行経路を作らない。

### テスト

- 誤った node 型への role 展開がコンパイルエラーになることを generator compile test で検証する。
- typed と untyped の結果集合が一致することを integration test で検証する。

## HYP-6a Vacuum と物理回収

### 目的

visibility horizon を越えた hyperedge、incidence、property を回収し、ID を安全に再利用する。

### 主な変更先

- `src/Quiver/Maintenance/Vacuum.cs`
- `src/Quiver/Maintenance/VacuumOptions.cs`
- `src/Quiver/Stores/VersionedHyperedgeStore.cs`
- `src/Quiver/Stores/IncidenceStore.cs`
- `src/Quiver/Wal/WalFileKind.cs`
- `tests/Quiver.Tests/VacuumTests.cs`

### 実装

- `VacuumTarget.Hyperedges` と report の回収件数を追加する。
- dead hyperedge の property、incidence、header の順に回収する。
- incidence を node chain から `PrevInNode` と `NextInNode` で unlink する。
- hyperedge chain は header ごと消えるため、個別の prev 修復を行わない。
- incidence slot は vacuum 中に active transaction が無い場合だけ free list へ戻す。
- hyperedge sequence の再利用時は generation を上げる。
- node incidence head が回収対象を指す場合は次の生存 incidence へ進める。
- tenant truncate と reopen 後の map metadata を既存 store と同じ契約に揃える。

### テスト

- 長い node chain の先頭、中間、末尾にある dead incidence を回収する。
- hyperedge ID の再利用後に古い ID と vector binding が別 entity を指さないことを検証する。
- active snapshot がある場合は回収しないことを検証する。
- vacuum 後の reopen と crash recovery を検証する。

## HYP-6b 診断と統計

### 目的

二本の incidence chain と header の不整合を検出し、クエリ計画に必要な基礎統計を公開する。

### 実装

- `DatabaseStatistics` に `HyperedgeCount` と `IncidenceCount` を追加する。
- `GraphStats` に全 hyperedge 数、type 別件数、type 別 arity histogram を追加する。
- `CheckConsistency` で次の不整合を検出する。
  - live hyperedge の arity が 2 未満。
  - incidence の hyperedge、node、role が無効。
  - node chain または hyperedge chain の cycle。
  - `PrevInNode` と `NextInNode` の不一致。
  - live incidence が node chain または hyperedge chain の片方から到達不能。
  - 同じ hyperedge に同じ role と node の組が重複。
- public column API が entity kind を受ける場合は `EntityKind.Hyperedge` を許可する。

### テスト

- 正常 DB で issue が 0 件になることを検証する。
- テスト用 raw mutation で各破損を一種類ずつ作り、対応 issue が出ることを検証する。
- type count と arity histogram が作成、削除、vacuum 後に一致することを検証する。

## HYP-6c 統合性能ゲート

### 目的

HYP-1d、HYP-2c、HYP-3c の三つの仮説を製品 API 経由で再測定する。

### 計測

- `HyperedgeTraversalBenchmarks` で binary 1-hop と role 指定 co-membership を比較する。
- `HyperedgeWriteBenchmarks` で arity 別 create、delete、property write、WAL bytes を測る。
- `HyperedgeMatchBenchmarks` で四 role の星型 Match と同等の reified graph pattern を比較する。
- 結果を `docs/benchmarks/YYYY-MM-DD_HYP-6c_Hyperedge.md` へ記録し、要点を `docs/design/development.md` へ集約する。

### 判定

- incidence 走査 p50 が binary の 3 倍以内である。
- WAL 増幅が HYP-2c の線形閾値以内である。
- 固定 RAG シナリオがクライアント側 materialize 無しで完走する。
- 一つでも不合格なら HYP-7 へ進まず、該当設計を修正する。
- 走査性能だけが不合格なら HYP-6d を必須化する。

## HYP-6d 物理 co-membership view spike と実装

このタスクは HYP-1d または HYP-6c の走査性能が不合格の場合だけ実行する。

### 比較案

- **案 A**：node ごとの incidence を連続配置する `IncidenceBlockStore`。
- **案 B**：明示設定された role pair だけを物理化する co-membership block。

自動 clique 展開は arity の二乗で増えるため候補にしない。

### 採否基準

- degree 10、100、1,000 の全てで binary p50 の 3 倍以内に入る。
- 追加ストレージは base incidence の 2 倍以内に収まる。
- create WAL が HYP-2c の閾値を超えない。
- 条件を満たす最小の案を採用し、どちらも満たさなければレコード配置へ戻って再設計する。

### 実装条件

- view は導出データとし、header と incidence を正本にする。
- mutation 後の delta、vacuum、rebuild、crash recovery を `AdjacencyBlockStore` と同じ契約で持つ。
- view が無い DB では linked incidence chain へフォールバックする。

## HYP-7 as-built 仕様、サンプル、公開面の確定

### 目的

実装済みの契約だけを仕様へ移し、RAG n 項 fact の end-to-end 例を提供する。

### 変更

- `docs/spec/04_records_index.md` に header、incidence、token、ID、vacuum を追記する。
- `docs/spec/05_query.md` に operator、DSL、Match、`OtherMembers` の origin 契約を追記する。
- `docs/spec/08_known_limits.md` に immutable member、node-only member、順序非保証、変換 API 対象外を追記する。
- `docs/design/development.md` に tenant ID、実装マップ、テスト、ベンチマーク結果を追記する。
- `samples/Quiver.Samples.Hyperedges/` に untyped DSL、Match、SourceGenerator の一連の例を追加する。
- `Quiver.Rag` の Fact 例は subject、object、source、asOf を使い、取込から source Chunk 回収までを実行する。
- Public API approval と XML documentation を最終確認する。

### 完了条件

- サンプルが新規 DB に取込み、DSL、Match、typed API の同じ fact を読み戻す。
- HYP-1d、HYP-2c、HYP-3c、必要なら HYP-6d の計測値と決定が記録されている。
- `dotnet build Quiver.slnx` と全テストが成功する。
- `docs/spec/` が実装と一致し、親計画書の未確定表現を残していない。

## 決定記録

| 日付 | Gate | 結果 | 決定 | 根拠 |
|---|---|---|---|---|
| 未実施 | HYP-S1 | 未計測 | 未決定 | node incidence head の配置を比較する |
| 未実施 | HYP-1d | 未計測 | 未決定 | linked incidence の走査性能を判定する |
| 未実施 | HYP-2c | 未計測 | 未決定 | WAL 増幅の線形性を判定する |
| 未実施 | HYP-3c | 未検証 | 未決定 | RAG query の表現力を判定する |
| 未実施 | HYP-S2 | 未検証 | 未決定 | SourceGenerator の role binding API を選ぶ |
| 未実施 | HYP-6c | 未計測 | 未決定 | 統合性能ゲートを判定する |
