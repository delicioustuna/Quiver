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
                                  └─ HYP-2a → HYP-2b → HYP-2c → HYP-2d
                                                       └─ HYP-3a → HYP-3b → HYP-3c
                                                                    ├─ HYP-4
                                                                    └─ HYP-S2 → HYP-5a → HYP-5b
       HYP-1c + HYP-2b + HYP-2d ──────────────────────────────────────→ HYP-6a
       HYP-3c ─────────────────────────────────────────────────────────→ HYP-6b
       HYP-1d + HYP-2c + HYP-2d + HYP-3c ─────────────────────────────→ HYP-6c
       HYP-2d (+ HYP-1d 不合格判定で必須化済み) ──────────────────────→ HYP-6d
       HYP-4 + HYP-5b + HYP-6a + HYP-6b + HYP-6c (+ HYP-6d) ─────────→ HYP-7
```

HYP-4 と HYP-S2 以降は、HYP-3c 完了後に独立して進められる。
HYP-2d は storage 層で閉じるため、HYP-3c・HYP-4・HYP-S2 と並行できる。
HYP-6a と HYP-6d は HYP-2d のレイアウト確定後に着手する。理由は二つ:
HYP-6d の採否基準にある WAL 条件は base の incidence 書込みが HYP-2c 閾値を超えたままでは
満たしようがなく (循環)、HYP-6a の unlink 設計は `PrevInNode` の有無に依存するため。

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

### 計測プロトコル

- baseline は現行の 15 バイト node fixed payload とし、案 A は同じレコードの末尾へ
  6 バイト head を加えた 21 バイト payload、案 B は 15 バイト node payload と
  node sequence で直接引く 6 バイト固定長 sidecar とする。
- experimental runner は 8 KiB page、24 バイト MVCC header、slotted-page の slot directory、
  6 バイト `Int48` を製品レイアウトと同じ寸法で再現する。案 B の sidecar は
  page body に `Int48` を密配置し、未所属 node は `-1` を保持する。
- node は sequence 全域を deterministic seed で疑似ランダムに読む。incidence 所属 node は
  同じ seed で一様に選び、head lookup は所属 node だけを対象にする。
- binary 1-hop は node record から binary head を読み、degree 10、100、1,000 の連続した
  6 バイト relationship sequence を走査する。hyperedge head は binary 経路では読まない。
- 各ケースは tiered compilation と ReadyToRun を無効化し、JIT と working set を warm-up 後、
  baseline / 案 A / 案 B の開始順を巡回しながら 32,768 operation の sample を 31 本採る。
  各 sample の elapsed time / operation を並べた
  中央値を p50 とし、同じ loop の前後で thread allocation 差分を取る。
- データサイズは node heap、node map、案 B の sidecar が占める割当済み page bytes の合計を
  記録する。計測環境は OS、runtime、CPU、GC mode とともに記録する。
- 同一ケースの sample p25 と p75 の差が p50 の 5% を超える場合は、プロセスを再起動して
  3 回計測し、各 run の p50 の中央値で判定する。

### 採否基準

- 案 A は binary `Read` と binary expand の p50 回帰がともに 3% 以下の場合だけ採用できる。
- 案 B は hyperedge head lookup が案 A の 1.5 倍以内なら採用する。
- 両方を満たす場合は、binary 経路を変えない案 B を採用する。

### 実測結果 (2026-07-03)

計測環境は Windows 10.0.26200、AMD64 Family 25 Model 33、.NET 10.0.9 x64、
workstation GC とした。
`DOTNET_TieredCompilation=0`、`DOTNET_ReadyToRun=0` で Release runner を実行した。
sample 内 IQR が 5% を超えたため別プロセスで 3 回実行し、各 run の p50 の中央値を採用した。

binary 経路は baseline 15 バイト node payload と案 A の 21 バイト payload を比較した。
案 B の binary 経路は baseline と同じ 15 バイトレイアウトであり、sidecar を読まない。

| node 数 | 経路 | degree | baseline p50 | 案 A p50 | 回帰 |
|---:|---|---:|---:|---:|---:|
| 100,000 | Read | — | 24.463 ns | 24.377 ns | -0.35% |
| 100,000 | expand | 10 | 96.671 ns | 101.187 ns | **+4.67%** |
| 100,000 | expand | 100 | 364.761 ns | 370.505 ns | +1.57% |
| 100,000 | expand | 1,000 | 2,679.578 ns | 2,673.950 ns | -0.21% |
| 1,000,000 | Read | — | 73.624 ns | 83.948 ns | **+14.02%** |
| 1,000,000 | expand | 10 | 192.264 ns | 190.741 ns | -0.79% |
| 1,000,000 | expand | 100 | 439.523 ns | 441.409 ns | +0.43% |
| 1,000,000 | expand | 1,000 | 2,737.320 ns | 2,748.389 ns | +0.40% |

head lookup は incidence を持つ node だけを対象にした。

| node 数 | incidence 率 | 案 A inline p50 | 案 B sidecar p50 | B / A |
|---:|---:|---:|---:|---:|
| 100,000 | 1% | 10.803 ns | 6.024 ns | 0.558x |
| 100,000 | 10% | 15.231 ns | 6.610 ns | 0.434x |
| 100,000 | 100% | 15.939 ns | 6.860 ns | 0.430x |
| 1,000,000 | 1% | 27.292 ns | 7.849 ns | 0.288x |
| 1,000,000 | 10% | 28.598 ns | 8.240 ns | 0.288x |
| 1,000,000 | 100% | 29.568 ns | 8.762 ns | 0.296x |

| node 数 | baseline | 案 A inline | 案 B sidecar | A 増加 | B 増加 |
|---:|---:|---:|---:|---:|---:|
| 100,000 | 5,275,648 B | 5,873,664 B | 5,898,240 B | +11.34% | +11.80% |
| 1,000,000 | 52,355,072 B | 58,327,040 B | 58,400,768 B | +11.41% | +11.55% |

全経路の managed allocation は 0 B/op だった。
案 A は binary expand degree 10 と 1,000,000 node の Read で 3% gate を超えたため不採用とする。
案 B は全 head lookup で 1.5x gate を満たし、binary payload を変えないため採用する。
後続実装は固定 tenant 25 の 6 バイト `NodeIncidenceHeadStore` を node sequence 直引きで使用する。

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

> 2026-07-05 追記: この 33 バイト契約は HYP-2c の不合格を受けて HYP-2d で再設計する。
> HYP-2d 完了後は HYP-2d の決定記録を正とする。

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

### 実測結果（2026-07-04）

`HyperedgeWalAmplificationBenchmarks` を `--hyperedge-wal` で実行した。
buffer pool と durability は既定値、checkpoint threshold は計測中の truncate を防ぐため 0 とし、token と初回 page allocation を warm-up した直後に明示 checkpoint を実行した。
以降の WAL file length 差分は自動 checkpoint が無いため `BytesWritten` 差分と一致する。

| tx 内件数 | arity | WAL bytes | bytes/item | binary 比 | 上限 | 判定 |
|---:|---:|---:|---:|---:|---:|---|
| 1 | 2 | 1,534 | 1,534.00 | 0.996x | 2.000x | 合格 |
| 1 | 4 | 1,850 | 1,850.00 | 1.201x | 3.000x | 合格 |
| 1 | 8 | 2,483 | 2,483.00 | 1.612x | 5.000x | 合格 |
| 1 | 16 | 3,749 | 3,749.00 | 2.434x | 9.000x | 合格 |
| 1,000 | 2 | 157,237 | 157.24 | 1.819x | 2.000x | 合格 |
| 1,000 | 4 | 261,717 | 261.72 | 3.027x | 3.000x | **不合格** |
| 1,000 | 8 | 470,703 | 470.70 | 5.444x | 5.000x | **不合格** |
| 1,000 | 16 | 888,670 | 888.67 | 10.279x | 9.000x | **不合格** |

binary relationship は単件 1,540 bytes、1,000 件 86,456 bytes（86.46 bytes/item）だった。
arity に対する線形回帰は単件、batch とも `R² = 1.000000` であり、page 分散による超線形増幅は観測されなかった。
batch の近似式は `bytes/item = 52.74 + 52.25 × arity` である。

**決定**：線形性は合格だが、batch の arity 4、8、16 が上限を超えるため HYP-2c の倍率仮説は棄却する。
上限から許される限界費用は binary の半分である 43.23 bytes/member なので、現状から約 9.02 bytes/member（17.3%）の削減が必要である。
超線形ではないため page 分散の変更は行わず、再設計箇所を 33-byte incidence payload と node-head / incidence-link 更新の WAL 表現に限定する。
是正タスクとして HYP-2d (incidence レイアウト再設計) を新設した。レコード圧縮案の比較と採否は HYP-2d で行い、製品 API 経由の再測定は HYP-6c で行う。

## HYP-2d incidence レイアウト再設計 (WAL 増幅の是正)

### 背景と費用分解

HYP-2c で batch 増幅が arity 4/8/16 で上限を超えた
(限界費用 52.25 B/member、許容 43.23 B/member = binary 86.46 B/item の半分)。
WAL は `WalPageImageCodec` v3 の full after-image (末尾ゼロ trim + RLE、run ≥ 8B) を
dirty page ごとに 1 回書く方式なので、batch の限界費用はページ上のレコード実費にほぼ一致する。
現行の member 1 件あたりの内訳:

- incidence レコード: `VersionedRecordHeap` の version ヘッダ 24B + payload 33B +
  slot directory ≈ 61B
- `ItemPointerMap`: 8B/entry (別テナントページ)
- node head (6B sidecar) と旧 head の `PrevInNode` backlink 書込み: HYP-2c ベンチは
  16 node を共有するためホットページに乗って償却されるが、実ワークロードでは
  node が散るぶん cold page image を追加しうる (ベンチが隠している費用)

計 69B/件が RLE (version ヘッダ内 xmax=0 の 8B run 等) で 52.25 B/member まで縮んでいる、
という整合の取れた分解になる。

削減の設計自由度は次の実装事実に支えられる。

- incidence の可視性は hyperedge header が正本で、incidence 自身の xmin/xmax は
  可視性判定に使っていない → version ヘッダ 24B は情報として遊んでいる。
- undo (abort / savepoint) と crash recovery は物理 page image
  (before-image の CompensationLogRecord) でレイアウト非依存 → ヒープ形式を差し替えても
  rollback / recovery の機構再設計は不要 (テストによる再検証は必要)。
- `IncidenceId` は internal で公開されず、参照は常に node head / hyperedge header 経由の
  chain のみ → 世代照合は hyperedge header 側で完結し、incidence 自身は generation を持たなくてよい。

### 比較案

- **案 A**: `PrevInNode` (6B) 削除のみ (payload 33→27B)。見積は 52.25 × 63/69 ≈ 47.7 B/member で
  **単独では 43.23 に届かない**。旧 head backlink 書込みの消滅 (実ワークロードの cold page 削減) は
  価値が大きいが主策にならないため、案 B に包含して評価する。
- **案 B (第一候補)**: 専用 fixed-slot 直接アドレスストア。`NodeIncidenceHeadStore` と同じ
  「sequence → page/offset 直引き」イディオムを 27B slot に適用し、version ヘッダ・
  slot directory・`ItemPointerMap` を全廃する。
  - slot 契約 (27B、302 slots/page):

    ```text
    Flags(1) | HyperedgeId(6) | NodeId(6) | RoleId(2) | NextInNode(6) | NextInHyperedge(6)
    ```

  - 見積: 27〜30 B/member → 全 arity で閾値内に余裕を持って入る。
  - free list は空 slot の `NextInNode` を free chain に転用し、head をストアの
    ヘッダページに置く。slot を free へ戻せるのは「全 live chain から unlink 済み +
    active transaction なし」(HYP-6a 契約) のときのみ。
  - 副次効果: chain 1 step の間接参照が map lookup + slot directory の 2 段から
    直接アドレス 1 段になり、HYP-1d で不合格だった走査の固定費側 (degree 10 の 5.49x) にも効く。
- **案 C (比較対象から除外)**: logical incidence WAL。PageImage 経路の一般性を壊す特殊化で、
  FTS-9 (logical SMO) を「正当な設計でも複雑度に見合わない」と停止した前例と同型。
  案 B が不合格の場合のみ再浮上させる。

### 実装 (案 B 採用時)

- `IncidenceStore` の内部を fixed-slot 直接アドレスへ置換する。`IIncidenceStore` 契約と
  enumerator の意味論 (header 不可視なら skip) は変えない。
- incidence map テナントは廃止する (テナント番号は欠番のまま詰めない)。
- `PrevInNode` を落とすため、vacuum の unlink は「dead incidence を node 別にグループ化し、
  影響 node chain を head から 1 回だけ走査して running prev で一括 unlink する sweep」
  (合計 O(影響 chain 長)) へ変更する。設計は HYP-6a に引き継ぐ。
- on-disk レイアウト変更なので `FormatVersion` を V4 へ上げる (クリーンブレイク、読み替えなし)。
- HYP-1b のレコード契約と HYP-6b の `PrevInNode` 整合性チェック項目は本タスクの決定で置き換える。

### 採否基準

- `HyperedgeWalAmplificationBenchmarks` 再走で、batch (1,000 件/tx) の arity 2/4/8/16 全てが
  binary 比 `(1 + arity/2)` 倍以内に入る。
- 単件 tx の WAL bytes が現行実測から +10% を超えて悪化しない。
- HYP-1d ハーネス再走で走査 p50 を記録する。3 倍以内は合格条件にしない (HYP-6d が控える) が、
  全 degree で 3 倍以内に入った場合は HYP-6d の中止を判定する。
- rollback / savepoint / crash recovery テスト (HYP-1c 分) を含む全スイートが緑。

### 実測結果 (2026-07-05)

案 B (27B fixed-slot 直接アドレス、`PrevInNode` 廃止、version ヘッダ / slot directory /
`ItemPointerMap` 全廃、free chain は空 slot の `NextInNode` 重畳) を実装し再測定した。
`FormatVersion` は V4、incidence 間接マップのテナント 22 は欠番。

| tx 内件数 | arity | WAL bytes | bytes/item | binary 比 | 上限 | 判定 |
|---:|---:|---:|---:|---:|---:|---|
| 1 | 2 | 1,268 | 1,268.00 | 0.823x | 2.000x | 合格 |
| 1 | 4 | 1,454 | 1,454.00 | 0.944x | 3.000x | 合格 |
| 1 | 8 | 1,829 | 1,829.00 | 1.188x | 5.000x | 合格 |
| 1 | 16 | 2,573 | 2,573.00 | 1.671x | 9.000x | 合格 |
| 1,000 | 2 | 107,258 | 107.26 | 1.241x | 2.000x | 合格 |
| 1,000 | 4 | 162,334 | 162.33 | 1.878x | 3.000x | 合格 |
| 1,000 | 8 | 273,346 | 273.35 | 3.163x | 5.000x | 合格 |
| 1,000 | 16 | 493,543 | 493.54 | 5.711x | 9.000x | 合格 |

binary は単件 1,540 / batch 86.42 bytes/item。線形性 `R² = 0.999997`。
batch の限界費用は差分フィットで **約 27.6 B/member** (許容 43.23、置換前 52.25) となり、
設計見積 27〜30 と一致する。単件も全 arity で 17〜31% 減少 (+10% 制限に対し悪化なし)。

走査再測定 (`--incidence-traversal`、arity 4、置換前 = 5.49x / 2.52x / 7.97x):

| degree | binary 比 p50 | alloc |
|---:|---:|---:|
| 10 | 4.81–4.94x | 0 B |
| 100 | 1.65–1.70x | 0 B |
| 1,000 | 5.39–5.46x | 0 B |

**決定**: WAL の採否基準は全て合格し、案 B を採用する。走査は全 degree で改善したが
degree 10 と 1,000 が 3 倍を超えたままなので **HYP-6d の中止条件は満たさず、HYP-6d は継続**。
degree 10 の残差は 2 段展開の固定費、1,000 はページ局所性という仮説が残るため、
HYP-6d の前段分解計測をそのまま実施する。

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

### 実装・検証結果 (2026-07-05)

**合格**。既存の alias は node へしか戻れず、一つの Fact から複数 role を展開できなかったため、
汎用 primitive `Select<TEntity>(alias)` を追加した。alias の `EntityKind` を保持し、
`NodeId` / `RelationshipId` / `HyperedgeId` の型不一致は operator tree 構築時に拒否する。

`HyperedgeRagQueryTests` で固定シナリオ四つを検証した。subject 起点の object + source、
Chunk 起点の subject + object、Fact property 絞込み後の四 role、同一 role の複数 member は、
いずれも途中の materialize なしに一つの operator tree で取得できる。
`HasMember` と RAG 固有の糖衣は不要と判断する。

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

### 検証結果と決定 (2026-07-05)

三案の as-generated サンプルと呼び出し側コードを Roslyn インメモリコンパイル
(実 Quiver アセンブリ参照、正例 = 診断 0 件、負例 = 特定診断 ID) で検証した。
利用例は arity 2 / arity 4 / 同一 role 複数 member × insert・load・update・delete・
typed traversal、型系は nullable・`IReadOnlyList<T>`・別 namespace・同名別 namespace を網羅。

| 基準 | 案 A (`GraphNodeRef<TNode>`) | 案 B (NodeId 引数) | 案 C (member builder) |
|---|---|---|---|
| 1. 型不一致のコンパイル時検出 | 合 (単一 CS0029 / 同名別 ns CS0029 / リスト要素 CS0266 / 走査推論) | **否** — buyer/item の NodeId 取り違えがエラー 0 件で通ることを実証 | 合 (式の TNode 衝突 CS0411/CS1503) |
| 2. role 名文字列を再入力しない | 合 (プロパティ代入) | 合 | 合 (式で指定) |
| 3. object identity 非依存 | 合 (明示 NodeId 保持) | 合 | 合 |
| 4. arity 比例の生成コード | 合 | 合 | 合 |
| 5. public 型追加 | **2 型固定** (`GraphNodeRef<TNode>` + factory、統合すれば 1 型) | 0 型 | 1 型 + hyperedge ごとに Builder 型 (線形増加) |

**決定: 案 A を採用。** 案 B は基準 1 を落とし typed API の本義を失うため候補外。
基準 1〜4 を満たす案 A と案 C の比較では、hyperedge 型数に比例して public 型が増える
案 C より固定 2 型の案 A が基準 5 に適合する。role プロパティは
`GraphNodeRef<TNode>` / `IReadOnlyList<GraphNodeRef<TNode>>` (nullable = optional role)、
load は `GraphNodeRef.To<TNode>(NodeId)` で対称に復元、typed traversal は
role プロパティ式 selector から `TNode` を推論する。

HYP-5a への引き継ぎ:

- generator は `[Node]` 属性で node 型を判定し、node generator の生成物
  (`IGraphNode<TSelf>` 実装) への semantic model 依存を持たない。制約
  `where TNode : IGraphNode<TNode>` は最終コンパイルで両 generator の出力が
  合流した時点で解決される。
- member 集合は作成時不変の契約に従い、生成 `Update` は property のみを書き
  role member を再束縛しない。
- factory (`GraphNodeRef.To<TNode>`) は非ジェネリック静的クラスに置いたが、
  public 面最少化のため ctor 直接使用へ畳む余地を HYP-5a で判断する。
- `HyperedgeMember` は managed 型 (role が `string`) のため `stackalloc` 不可
  (CS0208)。生成 insert 本体は配列または `List<T>` + `CollectionsMarshal.AsSpan` を使う。

spike コード (`tests/Quiver.SourceGen.Tests/RoleBindingSpike/`、9 テスト全緑) は
実験ループの契約に従い削除済みで、リポジトリには本決定記録のみを残す。

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

### 実装結果 (2026-07-05, commit aaf38f4)

- HYP-S2 から委任された factory の扱いは **`GraphNodeRef.To<TNode>` を廃し ctor へ畳む**と決定。
  `GraphNodeRef<TNode>` は `readonly record struct` (NodeId ctor + `NodeId` からの implicit
  変換) となり、public 追加は 1 型で収まった (spike 時の見立て「統合すれば 1 型」を実現)。
  利用側は `fact.Buyer = personId;` と書け、load は `new GraphNodeRef<TNode>(nodeId)` で復元する。
- `RoleAttribute` は計画の別ファイル案でなく、既存 Client 属性の同居規約に合わせ
  `HyperedgeAttribute.cs` へ同居 (物理ファイル分割のみの差異、型と公開サーフェスは計画どおり)。
- 診断は QVRHE001 (Role+Property 併用) / QVRHE002 (非 GraphNodeRef 型 role) /
  QVRHE003 (setter 無し) の 3 件。診断を出したメンバーは skip して残りを生成する。
- generator は role property の宣言型 `GraphNodeRef<TNode>` の構文から node 型を解決し、
  node generator の生成物への semantic model 依存を持たない。`where TNode : IGraphNode<TNode>`
  制約が両 generator の出力合流時に解決されることは、両生成器同時実行 + 実 Quiver
  アセンブリ参照のフルコンパイルテストで実証済み。
- テスト: generator 8 件 (snapshot / 診断 / 別 namespace / nullable / multi role /
  CreateHyperedge 一回呼び) + 実 DB round-trip 3 件。SourceGen 14 / Quiver.Tests 838 /
  PublicApi 1 全緑。

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

### 実装結果 (2026-07-05, commit b866fa5)

- 公開 API に `TypedGraphHyperedgeTraversal<THyperedge>` と
  `TypedGraphTraversal<T>.Hyperedges<THyperedge>(string? role = null)` を追加。
  式ツリー `Has` (等値 / `PropertyPredicate`)、`MembersOf<TNode>` / `OtherMembersOf<TNode>`、
  型なし降格 (`Members` / `OtherMembers` / `Values`)、終端
  (`ToList` / `ToListWithIds` / `ToIdList` / `First` / `Count`) を提供。
- role selector は式木でなく**生成済み名前付き拡張メソッド**で実現 (計画の「role selector 相当」の
  具体化)。role 名 (`[Role("Attendee")]`) はプロパティ名 (`Attendees`) と異なり得るため、
  実行時に式木からプロパティ名を取るとロール名を誤る。generator が `GraphHyperedgeModel.Roles`
  から role 名とハイパーエッジ型をリテラルで畳み込む — relationship 糖衣 (`.Knows()`) と同型。
- 生成は `{Class}TraversalExtensions` にロールごと 3 メソッド:
  `{Class}As{Prop}` (node→hyperedge)、`{Prop}` (member 展開)、`Other{Prop}` (起点除外
  co-membership。計画の明示要求外だが、無いと typed API で role 文字列を再入力する羽目になるため追加)。
  multi role は node traversal を返し、collection を行に載せない。
- 実行経路は型なし DSL への 2 段委譲のみで `ExpandToHyperedgeOp` / `ExpandMembersOp` に必ず到達する。
  別の物理経路・planner 分岐は追加していない。hidden origin 契約も untyped 実装をそのまま通る。
- internal な `[Hyperedge]` クラスには拡張クラスを internal で emit する
  (`GraphHyperedgeModel.IsPublic`。public だと CS0050)。
- 既知の命名エッジケース: role プロパティ名が `Members` / `OtherMembers` / `Values` と同名の場合、
  インスタンスメソッドが拡張メソッドより優先され untyped 版が呼ばれる (コンパイルエラーにはならない)。
  実害の薄い命名衝突として未対応。
- テスト: generator 3 件 (生成 snapshot / call-site 正例フルコンパイル /
  `Nodes<Place>().FactAsSubject()` が CS1929 になる負例) + 実 DB 統合 5 件
  (単一・複数・optional・同一 node 型複数 role・property filter で typed = untyped)。
  SourceGen 17 / Quiver.Tests 843 / Client 216 / PublicApi 1 (approved.txt 更新) 全緑、build 0 errors。

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
- incidence の unlink は HYP-2d のレイアウト決定に従う。`PrevInNode` を廃止した場合は、
  dead incidence を node 別にグループ化し、影響 node chain を head から 1 回だけ走査して
  running prev で一括 unlink する sweep (合計 O(影響 chain 長)) にする。
  保持した場合は `PrevInNode` と `NextInNode` で個別 unlink する。
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

### 実装結果 (2026-07-06, commit a1076d0)

- `VacuumTarget.Hyperedges` と `VacuumReport.ReclaimedHyperedges` / `ReclaimedIncidences` を追加。
  回収は property → incidence → header の順。全 header slot を 1 パス走査し、horizon 未満で
  commit 済みの xmax を持つ dead header を集め、その overflow property chain を `PropertyStore`
  経由で解放しつつ hyperedge chain から所属 incidence を node 別に集約する。
- HYP-2d で `PrevInNode` を廃止済みのため、unlink は計画どおり **node 別 sweep**:
  影響 node の chain を head から 1 回だけ走査し、running prev で dead incidence を一括 unlink
  (head 位置なら node incidence head を次の生存へ前進)。合計 O(影響 chain 長)。
- slot を free list へ返すのは `Run` が active tx 0 を保証した後のみ。header は heap から物理回収し
  sequence を free list へ返す。再利用時に `NextSequence` が generation を +1 するため、古い
  hyperedge ID と、`(EntityKind.Hyperedge, sequence)` キーの永続 vector payload は世代照合で弾く
  (factory の vector 世代 resolver に `Hyperedge` 分岐を追加)。
- `WalFileKind` は新設せず、既存の `FileTruncate` / `PageImage` 経路のみで crash recovery を担保する。
  計画の変更先候補から `WalFileKind.cs` は外れた。
- 設計上の妥協: hyperedge heap は node heap と同じく空 slot の tombstone + free-list 再利用に留め、
  散在ページの物理 tenant truncate は行わない。
- テスト: `VacuumTests` 20 件 (node chain 先頭/中間/末尾の dead incidence 回収、ID/vector 世代照合、
  active snapshot 非回収、reopen、残存 WAL replay による crash recovery)。
  build 0 errors、Quiver.Tests / Quiver.Stores.Tests / PublicApi 緑。

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
  - `PrevInNode` と `NextInNode` の不一致 (HYP-2d で `PrevInNode` を保持した場合のみ)。
  - live incidence が node chain または hyperedge chain の片方から到達不能。
  - 同じ hyperedge に同じ role と node の組が重複。
- public column API が entity kind を受ける場合は `EntityKind.Hyperedge` を許可する。

### テスト

- 正常 DB で issue が 0 件になることを検証する。
- テスト用 raw mutation で各破損を一種類ずつ作り、対応 issue が出ることを検証する。
- type count と arity histogram が作成、削除、vacuum 後に一致することを検証する。

### 実装結果 (2026-07-06, commit 12540fa)

- `CheckConsistency` は node chain と hyperedge chain の到達 incidence 集合を独立に構築して照合する。
  検出項目: live hyperedge の arity < 2、incidence の hyperedge/node/role 参照無効、
  chain cycle、node/hyperedge どちらか片方から到達不能な live incidence、同一 hyperedge 内の
  role+node 重複。論理削除済み header 配下の incidence は正常な vacuum 待ちとして扱う。
  `PrevInNode` 不一致項目は HYP-2d で `PrevInNode` を廃止したため対象外。
- `DatabaseStatistics` に `HyperedgeCount` (可視数) と `IncidenceCount` を追加。IncidenceCount は
  物理生存数なので delete 後〜vacuum 前の incidence を含む。
- `GraphStats` に `TotalHyperedges`、`HyperedgeTypeFrequency`、`HyperedgeArityByType` を追加。
  arity は区間バケットでなく `ArityHistogram` の**正確な値別分布** (オプティマイザの fan-out 見積り用)。
  メンバー集合は作成後不変なので header ごとに chain を 1 回走査して型別件数・arity・property を同時収集。
- public column API (`ColumnManager` / `GraphDatabase.CreateColumn`) が `EntityKind.Hyperedge` を受理。
- テスト: `HyperedgeDiagnosticsTests` (破損種別ごとに raw mutation で 1 種ずつ注入) +
  `GraphStatsTests` (作成/削除/vacuum 後の一致) + `ColumnRegistrationTests` (構築/更新/reopen)。
  診断/統計/列 38 件、hyperedge 回帰 71 件、PublicApi 1 件緑、build 0 errors。
- 既知の限界: `CheckConsistency` は複数ストアをロックなしで走査するため、同時更新中は一時的な
  不整合を観測しうる。運用上は書き込み停止時の診断を想定する。

## HYP-6c 統合性能ゲート

### 目的

HYP-1d、HYP-2c、HYP-3c の三つの仮説を製品 API 経由で再測定する。
HYP-2d のレイアウト確定後に実施する。

### 計測

- `HyperedgeTraversalBenchmarks` で binary 1-hop と role 指定 co-membership を比較する。
- `HyperedgeWriteBenchmarks` で arity 別 create、delete、property write、WAL bytes を測る。
- `HyperedgeMatchBenchmarks` で四 role の星型 Match と同等の reified graph pattern を比較する。
- 高次数カスケード: 1 node が 10^3〜10^4 hyperedge のメンバーである状態の `DeleteNode` を測る
  (tx 時間、WAL bytes、deadlock を起こさないこと、削除後の `CheckConsistency` が 0 件)。
  RAG では Chunk や頻出エンティティの node が高次数になる想定で、
  数値ゲートは置かず実測値と挙動を記録して HYP-7 の known limits へ反映する。
- 結果を `docs/benchmarks/YYYY-MM-DD_HYP-6c_Hyperedge.md` へ記録し、要点を `docs/design/development.md` へ集約する。

### 判定

- incidence 走査 p50 が binary の 3 倍以内である。
- WAL 増幅が HYP-2c の線形閾値以内である。
- 固定 RAG シナリオがクライアント側 materialize 無しで完走する。
- 一つでも不合格なら HYP-7 へ進まず、該当設計を修正する。
- 走査性能だけが不合格なら HYP-6d を必須化する。

## HYP-6d 物理 co-membership view spike と実装

このタスクは HYP-1d または HYP-6c の走査性能が不合格の場合だけ実行する。
HYP-1d の不合格 (2026-07-04) により必須化済み。ただし着手は HYP-2d 完了後とする —
採否基準の WAL 条件は base が HYP-2c 閾値超過のままでは満たせず、
HYP-2d の直接アドレス化は走査固定費そのものを変えるため。
HYP-2d 後の再測定で全 degree が 3 倍以内に入った場合は本タスクを中止する。

### 前段の分解計測

案を選ぶ前に、co-membership 1-hop の 2 段 —
(1) node → incidence chain 走査、(2) hyperedge → member 展開 — の時間内訳を
degree 10 / 100 / 1,000 で測る。HYP-1d の非単調な形状 (degree 100 のみ合格) は
degree 10 = 固定費支配、degree 1,000 = ページ局所性支配という仮説であり、
これを確認してから案を選ぶ。案 A (node ごとの連続配置) が改善するのは 1 段目だけなので、
2 段目支配なら案 B を第一候補にする。

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

### 実測結果と決定 (2026-07-06)

前段分解では degree 10 / 100 / 1,000 の node chain が 5.2 / 16.2 / 164.1 µs、
member 展開が 12.0 / 41.7 / 448.0 µs だった。高次数では第 2 段が支配するため、
node chain だけを連続化する案 A ではなく、明示ロール対だけを物理化する案 B を採用した。

採用実装はプロセス内の連続 block を導出ビューとして持ち、open と vacuum 後に
header / incidence から再構築する。create 差分は durable commit 後に公開し、
同一 transaction に未コミット差分がある間は linked incidence chain へフォールバックする。
このため rollback / savepoint は未コミット block を公開せず、crash recovery 後も
正本の recovery 完了後にビューを再生成できる。ビュー未設定 DB と未指定ロール対も
従来 chain を使う。

- block p50 / binary p50: degree 10 = 0.88x、100 = 0.33x、1,000 = 1.01x。
- managed allocation: chain 計測と block 読み取りはいずれも 0 B/op。
- 追加永続ストレージ: 0 B。メモリ payload は 16 B / 物理化 member pair
  (arity 4・一意ロールの基準 workload では base incidence 108 B / hyperedge の 14.8%)。
- create WAL: arity 4 の単件 / 1,000 件ともビュー無効時比 1.000。

全 degree の 3x、追加ストレージ 2x、create WAL の各基準を満たすため案 B を確定する。

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
| 2026-07-03 | HYP-S1 | 案 A は binary p50 3% gate 不合格、案 B は head lookup 0.288–0.558x | **案 B: tenant 25 の 6B sidecar** | binary 15B payload を維持し、全経路 0 B/op |
| 2026-07-04 | HYP-1d | degree 10: 5.49x, 100: 2.52x, 1000: 7.97x (alloc 0B) | **HYP-6d 必須化** | 2/3 degree で 3x 超過。managed alloc なし → HYP-3a 前の修正不要 |
| 2026-07-04 | HYP-2c | batch `R²=1.000000`、A=2/4/8/16 は 1.819x/3.027x/5.444x/10.279x | **線形性合格、倍率仮説は棄却** | A=4/8/16 が上限超過。限界費用を約 9.02 B/member 削減する是正を HYP-2d へ切り出し、HYP-6c で再測定 |
| 2026-07-05 | HYP-2d | batch A=2/4/8/16 が 1.241x/1.878x/3.163x/5.711x (上限 2/3/5/9)、単件 −17〜−31%、走査 4.8x/1.7x/5.4x | **案 B (fixed-slot 直接アドレス) 採用、FormatVersion V4** | 限界費用 ≈27.6 B/member (許容 43.23)。走査は改善したが degree 10/1000 が 3x 超のため HYP-6d は継続 |
| 2026-07-05 | HYP-3c | 固定 4 シナリオを単一 operator tree で取得 | **合格。`Select<TEntity>(alias)` を採用** | hyperedge alias へ型安全に戻る汎用 primitive だけを追加。`HasMember` / RAG 固有糖衣は不要 |
| 2026-07-05 | HYP-S2 | 案 B は role 取り違えを検出できず基準 1 不合格、案 A/C は全基準合格 (Roslyn 実コンパイル検証) | **案 A: `GraphNodeRef<TNode>` 採用** | public 型追加が固定 2 型で最少 (案 C は hyperedge 数に比例して Builder 型が増える)。詳細は HYP-S2 節の検証結果 |
| 2026-07-06 | HYP-6d | block / binary p50 は degree 10/100/1,000 で 0.88x/0.33x/1.01x、追加永続 0 B、create WAL 比 1.000 | **案 B: 明示 role pair のメモリ内 co-membership block 採用** | member 展開支配を直接除去し全基準合格。open/vacuum 後 rebuild、commit 後 delta、未設定時 chain fallback |
| 2026-07-06 | HYP-6c | 走査 view/binary p50 = 1.15x/0.95x/2.14x (degree 10/100/1000)、create WAL = 1.249x/1.890x/3.182x/5.746x (arity 2/4/8/16、上限 2/3/5/9)、RAG 4 役割は client 側 materialize 無しで完走 | **三ゲート合格。HYP-7 着手可** | 製品 API 経由で HYP-1d/2c/3c を再判定。走査は HYP-6d の co-membership view で全次数 3x 以内、WAL は HYP-2d バイトを再現。高次数 DeleteNode (10^3/10^4) は tx 7.84/47.69ms・WAL 61KB/608KB・CheckConsistency 0 件・no deadlock。計測は docs/benchmarks/2026-07-06_HYP-6c_Hyperedge.md |
