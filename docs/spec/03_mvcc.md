# MVCC とトランザクション

> as-built 仕様（QUIVER-SW family version 2、2026-09-05）

## 公開トランザクション能力 {#public-capabilities}

公開APIは`GraphStore`を既定入口とし、読み取り能力を`GraphReadAccess`、書き込み能力を`GraphWriteAccess`として型で分離する。
通常操作は`Read` / `Write`の同期callbackで実行する。read callbackは開始時snapshotを読み、write callbackは正常終了時だけ自動commitし、例外時はrollbackする。callback scopeは終了時に失効し、同じstoreのcallbackへの再入と`Task` / `ValueTask`の返却を拒否する。

長時間snapshotと明示transactionは`GraphStore.Advanced.BeginRead()` / `BeginWrite()`からsessionとして開く。`GraphWriteSession`は`Commit`または`Rollback`を明示し、未完了の`Dispose`はrollbackする。`GraphWorkspace`は同じcallback能力上にSource Generatorのtyped mapperを重ねる。

公開境界は`VertexKey`、`EdgeKey`、`NexusKey`と所有権付き`GraphValue`を返す。内部transaction interface、`Yatagarasu.Storage.*`、借用`ref struct`、物理ID、backend SPIは公開しない。queryは各accessの`Query`から開始する。

### 公開する論理識別子

`VertexKey`、`EdgeKey`、`NexusKey`は、Generationが1以上の論理識別子だけを有効とする。
各型の`default`は無効であり、Sequenceが0の現行エンティティを表さない。
公開する読み取り・存在確認・検索・走査では、既定値、範囲外、古い世代、現在のスナップショットで削除済みのキーを
対象なしとして扱い、各APIの規約に従って`false`、`null`、空の結果または既定値を返す。
検索の起点は、物理Sequenceへ変換する前にスナップショット上の世代を含む識別情報を検証し、再利用後の世代を誤って参照しない。

公開する書き込みでは、対象のヘッダを読み、スナップショット上で`InUse`であり、読み戻した論理IDが指定IDと一致することを検証する。
この検証は、プロパティ・関係型・ロールのトークン、レコード、索引、論理データを変更する前に行う。
存在しない対象、古い世代、既定値、削除済みの対象は`KeyNotFoundException`で拒否する。
必須値を要求する`Get`も、対象が存在しなければ`KeyNotFoundException`を送出する。
`GraphStore.Write`のコールバックの外側では、既存の例外変換に従い`GraphOperationException`の内部例外となる。
Edgeの両端点とNexusの全メンバーは一括検証し、一部だけが不正な場合もスキーマ・トークン・正本レコードを増やさない。

Generationが0の参照は、ストレージ走査・復旧・連鎖削除がSequenceを渡すための内部物理参照としてだけ残す。
内部のVertex更新と連鎖削除では、正本レコードがスナップショットから可視であることを検証してから、現在の論理IDへ変換する。
Edge・Nexusの通常の更新ではGenerationが0の参照を拒否する。
`PropertyOwner`や論理データの更新処理へ、未検証の物理IDを渡さない。
古い読み取り側が削除前の世代を読む間は`Vacuum`がSequenceを再利用しないため、スナップショット分離を維持できる。

## 分離レベル {#isolation}

Yatagarasu は snapshot isolation を提供する。
データベースインスタンスごとの `TransactionManager` は、一つの `WriterLease` と `SnapshotRegistry` を所有する。
書き込みトランザクションは同じ lease で直列化し、読み取りトランザクションは writer を待たずに開始する。
読み取り開始時の状態は `Snapshot(CommittedHighWater, AbortedGaps, ActiveWriterId?)` として固定する。
読み取りは自分の snapshot より後に commit した version を参照しない。
開始時に active だった writer の version も、その reader からは commit 後まで不可視のままである。
writer 自身の `xmin` と `xmax` は自己可視性として扱う。

状態は`CommittedTxRegistry`が不変オブジェクトとして一括公開する。中止したトランザクションIDも変更不能な集合に保持する。
書き込み・保守処理では同じ書き込み権限の下で状態を構築し、復旧では通常操作の受け付け前に構築する。
内部の公開用ロックは状態変更側だけが取得し、`Volatile.Write`を公開の線形化点とする。
書き込み権限の取得はページ更新前、コミットはWALの同期書き込み後、中止は変更の取り消しと中止IDの登録後に公開する。
コミットの公開時には、その書き込みを実行中として除外する状態も解除する。
権限解放時の実行中状態の消去は、次の書き込み側が権限を取得する前に完了する。
チェックポイントからの復元、再適用後のコミット済み・未コミット状態の復元、
`Vacuum`による回収済み境界の更新と中止履歴の削除も、同じ公開処理を通る。

読み取り側は、高水位0の保留登録を追加してから、1回の`Volatile.Read`で状態を取得する。
この取得が読み取りスナップショットの線形化点であり、取得した高水位を保留登録へ確定する。
読み取りの開始・終了は公開用ロックを取得せず、開始失敗時と終了時には登録を解除する。
`Vacuum`は、基準となる公開済み高水位を先に取得してから、`ConcurrentDictionary.Values`で読み取り登録の一覧を一括取得する。
基準値と各登録の高水位の最小値を使い、走査中に保留登録が確定しても基準値を超えて回収境界を進めない。
走査前から存在する保留登録は回収境界を1に抑え、回収を延期する。
走査後に追加された読み取り側は基準値以上の状態を取得するため、必要な古い版を見落とさない。
走査中に読み取りが終了しても、取得済みの登録は安全側の回収境界を保持する。
保留登録中は診断に表示する高水位が0になる場合がある。
書き込みがない境界で行うチェックポイントは、読み取り側のためのWAL変更前イメージを必要としないため、
この登録手順にかかわらず、既存のページ書き出しとWAL切り詰めの順序を維持する。

## entity と property {#entities-and-properties}

グラフ entity は `Vertex`、`Edge`、`Nexus` の三種類である。
`Property` は独立した entity ではない。
Property は owner の identity と property key に束縛された versioned value である。
永続 identity は `PropertyAddress(Owner, Key)` であり、public な property ID は持たない。
各 owner header は `PropertyVersionRef` の先頭を保持し、列挙 API は ID を公開しない `PropertyCursor` を返す。

Single cardinality の更新は、現在の可視 version に `xmax` を設定して新しい version を chain の先頭へ追加する。
Set cardinality の追加は既存 version を終了せずに新しい version を追加し、削除は一致する version に `xmax` を設定する。
property record に保存した owner と読み取り側の owner が一致しない chain は corruption として拒否する。

Vertex、Edge、Nexus の Generation は各 entity version sidecar を正本とする。
Property version は `xmin`、`xmax`、Generation を 84 バイトの version record に保持する。
同じ Sequence でも Generation が異なる参照は別 incarnation として扱い、stale な参照を返さない。
Vertex、Edge、Nexus の `EntityVersionMeta` は `xmin`、`xmax`、Generation だけを持つ 24 バイト record である。
sidecar format version は 4 であり、旧 40 バイト record は読み替えない。

## ライフサイクル {#lifecycle}

```text
Active -> Preparing -> Committed
  |
  +-----------------> Aborted
```

| 状態 | 値 | 意味 |
|---|---:|---|
| `Active` | 1 | 読み書きを受け付ける |
| `Preparing` | 2 | commit を準備している |
| `Committed` | 3 | `Commit` が WAL へ永続化された |
| `Aborted` | 4 | プロセス内の変更を巻き戻した |

`TransactionId` は単調に増える識別子である。
`CommittedTxRegistry` は durable commit の高水位と、高水位以下で中止した writer の gap を記録する。
可視性は高水位、gap、開始時の active writer、自己 transaction ID だけで判定する。

writer lease の取得は既定で最大 5 秒待機する。
`WriterContentionMode.Wait` は `WriterWaitTimeout` まで待ち、取得できなければ `WriterBusyException` を送出する。
`WriterContentionMode.FailFast` は二本目の writer を待たずに同じ例外を送出する。
callback、session、backend、manager、bulk、schema、maintenanceのmutation入口は同じleaseを使う。
ただし、コミットの永続化後にしきい値を超えて起動するチェックポイントは、延期可能な保守処理である。
書き込み権限を即時取得できない場合は競合を例外にせず、次の安全な機会へ延期する。

## commit {#commit}

書き込みトランザクションは変更ページを transaction-owned write set に保持する。
commit は最終 `PageImage` を WAL へ出力し、`Commit` を追記して、その LSN まで fsync する。
この fsync が成功した時点が durability の境界である。

durable commit 後に checkpoint や通知処理が失敗しても、トランザクションを abort 状態へ戻さない。
`Commit` の後へ `Abort` を追記しない。
しきい値によるチェックポイントの書き込み競合は延期として扱い、成功済みのコミットやインスタンスの健全性を変更しない。
durable commit 後の導出 view publish が失敗したインスタンスは faulted となり、新しい operation を拒否する。

active writer の dirty page は commit fsync 前にデータファイルへ書かない。
commit はデータページの flush を待たず、dirty page は後続 checkpoint または退避で書く。
バッファプール内に退避可能な frame がなくなると `TransactionTooLargeException` を送出し、その writer を自動 abort して lease を解放する。

## 内部abortとsavepoint {#abort-savepoint}

各 write pin は変更前のページを transaction-owned write set に保存する。
明示 abort と commit なしの dispose は before-image を LIFO 順に適用してプロセス内の変更を復元し、`Abort` を WAL へ記録する。

`Savepoint` は現在の before-image 境界を記録する。
`RollbackTo` は指定境界より後の before-image を逆順に適用し、後から作られた savepoint を無効にする。
`ReleaseSavepoint` は境界だけを解放し、変更を親スコープへ残す。

全文 definition catalog の更新は同じ page write set を使う。
全文 delta segment body は commit 前に fsyncし、artifact参照を持つmanifest pageをprimary propertyと同じwrite setへ追加する。
commit hook は durable manifest generation を in-memory snapshotへ反映し、abort時はtransaction-local overlayを破棄する。
`RollbackTo` は savepoint 後の全文 mutation buffer も同じ境界まで切り戻す。
全文専用の論理 undo stack や補償 WAL record は持たない。

## スキーマのスナップショット {#schema-snapshot}

ラベル、Edge 型、Nexus 型、ロール、プロパティキー、索引定義はデータと同じ書き込みトランザクションに属する。
書き込みトランザクションは `EditSchema` で未コミットの変更を参照できる。
別のreaderは、その変更がcommitされるまで参照できない。
内部readerのschema catalogは開始時の不変snapshotを保持し、後続commitによって変化しない。
rollback と `RollbackTo` はスキーマページと索引定義を同じ before-image 境界まで戻す。
未知の名前を読み取り API に渡した場合は空結果または missing を返し、トークンを作成しない。

## crash recovery {#crash-recovery}

crash recovery の winner は checksum が有効な明示的な `Commit` record だけで決める。
winner の `PageImage` は redo し、commit record を持たない transaction の image は適用しない。
page LSN が image LSN 以上なら適用済みとして読み飛ばし、loser undo pass は実行しない。

詳細は [WAL とリカバリ](02_wal_recovery.md) を参照する。

## 読み取り専用トランザクション {#read-only}

読み取りcallbackと`GraphReadSession`の内部transactionはsnapshotを取得するが、WAL recordとpage before-imageを生成しない。
読み取りaccessにはwrite APIが存在しない。
`SnapshotRegistry` は active reader 数、最古 reader の経過時間、開始位置、高水位を保持する。
長時間 reader は警告対象にできるが、強制失効しない。
登録時の警告用走査は単調時計で250ミリ秒に一度へ制限し、同時登録では一つだけが走査を行う。
警告のしきい値を超えた読み取りは、次に走査できる登録時に報告する。明示的な診断取得はこの制限を受けない。
中止履歴が空のスナップショット取得では変更不能な空集合を共有し、中止IDの集合を新たに割り当てない。
OpenTelemetryとEventSourceはactive snapshot数と最古snapshot ageを同じregistryから観測する。
