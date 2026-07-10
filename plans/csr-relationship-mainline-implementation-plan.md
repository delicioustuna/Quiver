# CSR relationship mainline 実装計画

## 目的

この文書は、CSR relationship を現行 ARIES カーネル上の本流実装へ進めるための作業計画である。

`plans/csr-relationship-contract.md` は公開契約を定める。
本書は、その契約を既存コードへ実装する順序、ファイル境界、テスト、完了条件を定める。

後続エージェントは本書を最初に読み、ここで定義した slice 順に着手する。

## 現在地

COW shadow paging 案は不採用である。
本流カーネルは現行 ARIES を継続する。

CSR relationship の既知 Blocker は検証済みである。
locator sidecar、snapshot-aware delta、external process-kill と torn-write recovery matrix、統合 storage 上の 1,000,000 delta merge gate は PASS している。

残っている作業は、Blocker の除去ではなく、検証済み slice を本流の永続 layout へ畳み込む作業である。

## 実装判断

最初の本流 slice では、`VersionedRelationshipStore` を正本の relationship store として残す。
CRUD、MVCC、generation、property chain、inline property、vacuum は既存 store が引き続き担当する。

CSR 側は読み取り最適化 view として段階投入する。
base view は既存の `AdjacencyBlockStoreV2` を使う。
post-base insert は dedicated persistent delta store へも記録する。
traversal は base view と persistent delta store を同じ snapshot で合成する。

この判断により、最初の slice では公開 API と relationship property の正本を動かさない。
後続の専用 CSR segment format は、この slice が安定してから導入する。

## 非目標

この slice では、relationship row store を削除しない。

この slice では、relationship property column の完全な永続 format を確定しない。

この slice では、自動 background merge を導入しない。
merge は既存の `GraphDatabase.CompactAdjacency()` から明示実行する。

この slice では、旧 format からの自動 migration を実装しない。
on-disk layout を増やす場合は `FormatVersion` を bump し、旧 DB は fail-fast で拒否する。

## 追加する永続 layout

dedicated delta は二つの固定 tenant を使う。

| tenant | 用途 |
|---:|---|
| 27 | relationship delta head sidecar |
| 28 | relationship delta page store |

tenant 27 は固定スロット直接アドレス方式とする。
slot index は `nodeSeq * 2 + direction` とする。
direction は `0 = outgoing`、`1 = incoming` とする。

head slot は少なくとも `firstPageId`、`lastPageId`、`entryCount`、`formatVersion` を保持する。
`entryCount` は診断と merge policy の判断に使う。

tenant 28 は append-only delta page store とする。
page header は `nextPageId`、`entryCount`、`reserved` を持つ。
entry は少なくとも `relSeq`、`otherNodeSeq`、`typeId`、`flags` を持つ。
`relSeq` と `otherNodeSeq` は 48 bit little-endian で保持する。
`typeId` は 16 bit little-endian で保持する。

delta entry は visibility の正本を持たない。
visibility は `tx.Relationships.Read(relId)` の MVCC 判定で決める。
これにより、既存の relationship version chain と committed registry を流用できる。

payload lane は最初の slice では delta entry に持たせない。
delta 側の property predicate と payload read は row property 経路へ fallback する。
base へ compact された後は `AdjacencyBlockStoreV2` の payload lane を使う。

## 追加する型

最初の slice で追加する型は次のとおり。

| 型 | 配置 | 役割 |
|---|---|---|
| `RelationshipDeltaHeadStore` | `src/Quiver/Stores/` | node と direction から delta chain の head を引く固定スロット sidecar |
| `PersistentRelationshipDeltaStore` | `src/Quiver/Stores/` | delta page の append、cursor、reset、reload を担当する |
| `RelationshipDeltaEntry` | `src/Quiver/Stores/` | delta page entry の内部表現 |
| `PersistentRelationshipDeltaCursor` | `src/Quiver/Stores/` | page chain を走査し、row store の MVCC 判定で snapshot 合成する |

既存の `RelationshipDeltaStore` は facade として残してよい。
ただし、実体が linked-list だけを読む状態は本流 slice の完了条件を満たさない。

推奨は、`RelationshipDeltaStore` を interface ではなく小さな coordinator として残し、内部で persistent store を優先する構成である。
in-memory backend やテストで persistent store が無い場合だけ、現在の linked-list cursor を fallback として使う。

## 書き込み経路

`GraphTransaction.CreateRelationship` から `VersionedRelationshipStore.Create` が呼ばれる既存経路は維持する。

relationship 作成が成功したら、同じ transaction の dirty page として delta store に二つの entry を append する。
source 側には outgoing entry を追加する。
source と target が異なる場合、target 側には incoming entry を追加する。
self-loop では entry を一つだけ追加する。

append は row store の作成と同じ ARIES page WAL に乗る。
そのため、新しい WAL record type は追加しない。
`SingleFileContainer` の tenant page として通常の page image recovery に任せる。

delete 時に delta entry を物理更新する必要はない。
delta cursor は `tx.Relationships.Read(relId)` を呼び、削除済みまたは snapshot 非可視の relationship を返さない。

`Vacuum` は最初の slice では delta page を回収しない。
delta page の物理回収は `CompactAdjacency()` の reset でまとめて行う。

## 読み取り経路

`BinaryExpandCursor` は base view を先に読む。
base view では既存どおり `IAdjacencyBlockStore.OpenCursor` と tombstone 判定を使う。

base view の後で persistent delta cursor を読む。
delta cursor は `baseRelHwm` 未満の `relSeq` を必ず捨てる。
この条件により、compact 後に古い delta page が残っていても重複しない。

delta cursor は entry の `typeId` と direction を先に判定する。
その後で `tx.Relationships.Read(new RelationshipId(relSeq))` を呼び、MVCC visibility、generation、source、target を確認する。

row store から読んだ source と target が delta entry と一致しない場合、その entry は返さない。
この検査は sequence reuse や破損に対する防御である。

## compact と merge policy

最初の slice では merge policy を明示 compact のみとする。
`GraphDatabase.CompactAdjacency()` は、active transaction が無いときだけ実行できる既存契約を維持する。

compact の順序は次のとおりにする。

1. adjacency descriptor を `KindNone` にして flush する。
2. relationship row store を scan し、生存 relationship だけから V2 base view を再構築する。
3. epoch の `BaseRelHwm` を新しい高水位へ進める。
4. persistent delta head と delta page store を reset する。
5. adjacency descriptor を V2 として書き戻し、flush する。
6. in-memory store 参照を新しい base view と空 delta store に差し替える。

reset と descriptor write の間で crash しても、descriptor が `KindNone` なら reopen は row path fallback になる。
descriptor write 後に crash した場合でも、`BaseRelHwm` により古い delta entry は無視される。

自動 merge policy は後続 slice に送る。
後続 slice で自動化する場合の候補条件は、delta entry count が base entry count の 10% を超える場合、または delta page count が 1024 pages を超える場合である。
この候補値は初期値であり、性能計測で調整する。

## recovery 契約

新 tenant はすべて `SingleFileContainer` の DATA fileKind に載せる。
専用 WAL fileKind は追加しない。

`BinaryGraphStorageBackendFactory.OpenCore` は recovery と `container.ReloadAll()` の後に delta store を open する。
`ReloadStoreMeta` は `RelationshipDeltaHeadStore.ReloadMeta()` と `PersistentRelationshipDeltaStore.ReloadMeta()` を呼ぶ。

abort の before-image undo 後は、delta head cache と delta page meta を必ず再読込する。
これを怠ると、abort 済み append が cursor から見える可能性がある。

external process-kill matrix は既存の compact recovery matrix に delta tenant を含める。
検証 phase は descriptor invalidation 後、rebuild 後、final descriptor flush 後を維持する。
各 phase で WAL tail zero を組み合わせる。

## 変更する主なファイル

| ファイル | 変更内容 |
|---|---|
| `src/Quiver/Storage/FormatVersion.cs` | on-disk layout 追加時に `Current` を次へ進める |
| `src/Quiver/Backend/BinaryGraphStorageBackendFactory.cs` | tenant 27 と 28 を開き、delta store を transaction manager と access methods へ渡す |
| `src/Quiver/Transactions/TransactionManager.cs` | relationship create 経路から delta append できるよう store 参照を保持する |
| `src/Quiver/GraphTransaction.cs` | create 成功後の delta append 境界を確認する。実際の append は transaction 層に寄せる |
| `src/Quiver/Stores/RelationshipDeltaStore.cs` | linked-list view から persistent delta 優先の coordinator へ変更する |
| `src/Quiver/Stores/RelationshipDeltaHeadStore.cs` | 新規追加 |
| `src/Quiver/Stores/PersistentRelationshipDeltaStore.cs` | 新規追加 |
| `src/Quiver/Backend/BinaryExpandCursor.cs` | persistent delta cursor を使う |
| `src/Quiver/Backend/BinaryGraphStorageBackend.cs` | `CompactAdjacency()` で delta reset と reload を行う |
| `tests/Quiver.Stores.Tests/` | delta store の unit test を追加する |
| `tests/Quiver.Tests/AdjacencyEpochTests.cs` | snapshot delta regression を persistent delta 経路へ拡張する |
| `tests/Quiver.Backend.Tests/BinaryGraphStorageBackendCrashContractTests.cs` | delta tenant を含む crash contract を追加する |
| `benchmarks/Quiver.Benchmarks/` | 1M merge gate と recovery matrix の runner を persistent delta 経路で再測定する |

## 実装順序

### Slice 1: 永続 delta store 単体

`RelationshipDeltaHeadStore` と `PersistentRelationshipDeltaStore` を追加する。
append、cursor、page rollover、reset、reopen、reload を store test で固定する。

この slice では production write path に接続しない。
完了条件は `tests/Quiver.Stores.Tests` の新規 delta store tests が PASS することである。

### Slice 2: create path への接続

relationship create commit の dirty page として outgoing と incoming の delta entry を append する。
self-loop は一件だけ append する。

この slice では `BinaryExpandCursor` をまだ切り替えず、書き込みの永続化だけを確認する。
完了条件は reopen 後に delta store の entry count と entry 内容が一致することである。

### Slice 3: expand cursor の切り替え

`RelationshipDeltaStore.OpenCursor` を persistent delta 優先へ変更する。
既存 linked-list cursor は fallback として残す。

既存の snapshot regression を persistent delta 経路で通す。
特に read-only transaction 開始後の delta insert、delta head 更新、delta delete を確認する。

### Slice 4: compact reset

`CompactAdjacency()` に delta reset を入れる。
reset 後も `BaseRelHwm` によって重複が起きないことを確認する。

この slice で 1,000,000 delta merge gate を再実行する。
compact elapsed は既存 gate と同じ 2,000 ms を上限にする。

### Slice 5: recovery matrix

external process-kill と WAL tail zero の recovery matrix を persistent delta tenant 込みで再実行する。
descriptor invalidation 後と rebuild 後は row path fallback になることを確認する。
final descriptor flush 後は V2 view reopen になり、delta relationship が重複しないことを確認する。

## テスト計画

最小テストは次のとおりである。

| 対象 | テスト |
|---|---|
| delta head | node と direction から head slot を再オープン後に読める |
| delta page | 1 page 超過後も page chain を順に読める |
| self-loop | outgoing と incoming に重複登録しない |
| snapshot insert | read-only transaction 開始後の insert を見ない |
| snapshot head update | 後続 insert が head になっても既存 delta を落とさない |
| snapshot delete | read-only transaction 開始後の delete は開始時 snapshot から見える |
| sequence reuse | stale generation の relationship を delta cursor が返さない |
| compact | base と delta の重複が起きない |
| reopen | reopen 後も delta cursor が同じ結果を返す |
| recovery | process-kill と WAL tail zero の matrix が PASS する |

実行コマンドは次を基準にする。

```powershell
dotnet test tests\Quiver.Stores.Tests\Quiver.Stores.Tests.csproj --filter FullyQualifiedName~RelationshipDelta
dotnet test tests\Quiver.Tests\Quiver.Tests.csproj --filter "FullyQualifiedName~AdjacencyEpochTests|FullyQualifiedName~GraphDatabaseTests"
dotnet test tests\Quiver.Backend.Tests\Quiver.Backend.Tests.csproj --no-build --filter "FullyQualifiedName~BinaryGraphStorageBackendCrashContractTests"
dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --clean-slate-csr-compact-recovery-matrix
dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --clean-slate-csr-product-merge-gate
dotnet build Quiver.slnx -v minimal
```

## 完了条件

本流 slice の完了条件は次のとおりである。

- public `RelationshipId` の意味を変更しない。
- `RelationshipId.Create(sequence, oldGeneration)` が sequence reuse 後の新 relationship を指さない。
- base view と persistent delta view の合成が snapshot isolation を守る。
- compact 後に base と delta の重複が起きない。
- external process-kill と WAL tail zero の recovery matrix が PASS する。
- 1,000,000 delta merge gate が 2,000 ms 以内に収まる。
- `dotnet build Quiver.slnx -v minimal` が 0 errors で通る。

## 後続エージェントへの注意

最初に `plans/csr-relationship-contract.md` と本書を読む。
次に `RelationshipDeltaStore.cs`、`BinaryExpandCursor.cs`、`BinaryGraphStorageBackend.CompactAdjacency()`、`BinaryGraphStorageBackendFactory.OpenCore()` を読む。

`RelationshipId` を ordinal として扱ってはならない。
`RelationshipId.Sequence` は公開 stable ID の sequence であり、CSR segment 内 ordinal ではない。

delta cursor は row store の visibility 判定を必ず通す。
delta page に載っていることだけを理由に relationship を返してはならない。

compact は正本ではなく導出 view の再構築である。
途中で失敗した場合は row path に戻れる状態を優先する。

docs と plans 以外に内部タスク番号や代替案表記を残してはならない。
