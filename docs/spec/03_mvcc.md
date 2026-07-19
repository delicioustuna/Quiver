# MVCC とトランザクション

> as-built 仕様（QUIVER-SW family version 2、2026-07-18）

## 公開トランザクション能力 {#public-capabilities}

公開 API は読み取り能力を `IReadTransaction`、書き込み能力を `IWriteTransaction` として分離する。
`BeginReadTransaction()` は `IReadTransaction` を返し、query、スキーマ参照、エンティティ参照だけを公開する。
`BeginWriteTransaction()` は `IWriteTransaction` を返し、読み取り能力に加えて mutation、スキーマ編集、commit、rollback、savepoint を公開する。
トラバーサルは `Query`、mutation DSL は書き込みハンドルの `Mutate` から開始する。
読み取りハンドルに書き込みメソッドを持たせて実行時に拒否する設計は採用しない。

## 分離レベル {#isolation}

Quiver は snapshot isolation を提供する。
データベースインスタンスごとの `TransactionManager` は、一つの `WriterLease` と `SnapshotRegistry` を所有する。
書き込みトランザクションは同じ lease で直列化し、読み取りトランザクションは writer を待たずに開始する。
読み取り開始時の状態は `Snapshot(CommittedHighWater, AbortedGaps, ActiveWriterId?)` として固定する。
読み取りは自分の snapshot より後に commit した version を参照しない。
開始時に active だった writer の version も、その reader からは commit 後まで不可視のままである。
writer 自身の `xmin` と `xmax` は自己可視性として扱う。

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
`EnforceExclusiveWriter` を有効にした場合は、二本目の writer を待たずに拒否する。
facade、backend、manager、bulk、schema、maintenance の mutation 入口は同じ lease を使う。

## commit {#commit}

書き込みトランザクションは変更ページを transaction-owned write set に保持する。
commit は最終 `PageImage` を WAL へ出力し、`Commit` を追記して、その LSN まで fsync する。
この fsync が成功した時点が durability の境界である。

durable commit 後に checkpoint や通知処理が失敗しても、トランザクションを abort 状態へ戻さない。
`Commit` の後へ `Abort` を追記しない。
durable commit 後の導出 view publish が失敗したインスタンスは faulted となり、新しい operation を拒否する。

active writer の dirty page は commit fsync 前にデータファイルへ書かない。
commit はデータページの flush を待たず、dirty page は後続 checkpoint または退避で書く。
バッファプール内に退避可能な frame がなくなると `TransactionTooLargeException` を送出し、その writer を自動 abort して lease を解放する。

## abort と savepoint {#abort-savepoint}

各 write pin は変更前のページを transaction-owned write set に保存する。
明示 abort と commit なしの dispose は before-image を LIFO 順に適用してプロセス内の変更を復元し、`Abort` を WAL へ記録する。

`Savepoint` は現在の before-image 境界を記録する。
`RollbackTo` は指定境界より後の before-image を逆順に適用し、後から作られた savepoint を無効にする。
`ReleaseSavepoint` は境界だけを解放し、変更を親スコープへ残す。

全文 definition catalog の更新は同じ page write set を使う。
全文 delta segment は commit hook で manifest generationへ反映し、abort時は transaction-local overlay を破棄する。
全文専用の論理 undo stack や補償 WAL record は持たない。

## スキーマのスナップショット {#schema-snapshot}

ラベル、Edge 型、Nexus 型、ロール、プロパティキー、索引定義はデータと同じ書き込みトランザクションに属する。
書き込みトランザクションは `EditSchema` で未コミットの変更を参照できる。
別の reader と `QuiverDatabase.Schema` は、その変更が commit されるまで参照できない。
reader の `Schema` は開始時の不変な `ISchemaCatalog` を保持し、後続 commit によって変化しない。
rollback と `RollbackTo` はスキーマページと索引定義を同じ before-image 境界まで戻す。
未知の名前を読み取り API に渡した場合は空結果または missing を返し、トークンを作成しない。

## crash recovery {#crash-recovery}

crash recovery の winner は checksum が有効な明示的な `Commit` record だけで決める。
winner の `PageImage` は redo し、commit record を持たない transaction の image は適用しない。
page LSN が image LSN 以上なら適用済みとして読み飛ばし、loser undo pass は実行しない。

詳細は [WAL とリカバリ](02_wal_recovery.md) を参照する。

## 読み取り専用トランザクション {#read-only}

読み取り専用トランザクションは snapshot を取得するが、WAL record と page before-image を生成しない。
読み取り専用 transaction から write API を呼び出すことはできない。
`SnapshotRegistry` は active reader 数、最古 reader の経過時間、開始位置、高水位を保持する。
長時間 reader は警告対象にできるが、強制失効しない。
