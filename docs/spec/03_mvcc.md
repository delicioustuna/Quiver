# MVCC とトランザクション

> as-built 仕様（QUIVER-SW family version 1、2026-07-15）

## 分離レベル {#isolation}

Quiver は snapshot isolation を提供する。
各トランザクションは開始時の `SnapshotLsn` に対応する一貫した状態を参照する。
読み取りは自分の snapshot より後に commit した version を参照しない。

## entity と property {#entities-and-properties}

グラフ entity は `Vertex`、`Edge`、`Nexus` の三種類である。
`Property` は独立した entity ではない。
Property は owner の identity と property key に束縛された versioned value である。

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
`CommittedTxRegistry` は明示的な durable commit と recovery で確認した winner を記録し、version の可視性判定に使う。

## commit {#commit}

書き込みトランザクションは変更ページを transaction-owned write set に保持する。
commit は最終 `PageImage` を WAL へ出力し、`Commit` を追記して、その LSN まで fsync する。
この fsync が成功した時点が durability の境界である。

durable commit 後に checkpoint や通知処理が失敗しても、トランザクションを abort 状態へ戻さない。
`Commit` の後へ `Abort` を追記しない。

## abort と savepoint {#abort-savepoint}

各 write pin は変更前のページを transaction-owned write set に保存する。
明示 abort と commit なしの dispose は before-image を LIFO 順に適用してプロセス内の変更を復元し、`Abort` を WAL へ記録する。

`Savepoint` は現在の before-image 境界を記録する。
`RollbackTo` は指定境界より後の before-image を逆順に適用し、後から作られた savepoint を無効にする。
`ReleaseSavepoint` は境界だけを解放し、変更を親スコープへ残す。

全文索引を含む B+Tree 更新も同じ page write set を使う。
全文専用の論理 undo stack や補償 WAL record は持たない。

## crash recovery {#crash-recovery}

crash recovery の winner は明示的な `Commit` record だけで決める。
winner の `PageImage` は redo し、commit record を持たない transaction の image は適用しない。
旧 WAL のような loser undo pass は実行しない。

詳細は [WAL とリカバリ](02_wal_recovery.md) を参照する。

## 読み取り専用トランザクション {#read-only}

読み取り専用トランザクションは snapshot を取得するが、WAL record と page before-image を生成しない。
読み取り専用 transaction から write API を呼び出すことはできない。
