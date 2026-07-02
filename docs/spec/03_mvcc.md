# MVCC & トランザクション

> as-built 仕様 (on-disk FormatVersion V2)

## 分離レベル {#isolation}

Quiver は **snapshot isolation** をサポートする。各トランザクションは、その開始時の LSN
(`SnapshotLsn`) 時点におけるデータベースの一貫したスナップショットを見る。ライタはリーダを
ブロックせず、並行するリーダはそれぞれ自分の一貫したスナップショットを見る。

## トランザクションのライフサイクル {#lifecycle}

```
Active → Preparing → Committed
  │
  └──────────────→ Aborted
```

| 状態 | 値 | 意味 |
|---|---|---|
| `Active` | 1 | 進行中、読み書き可能 |
| `Preparing` | 2 | コミット準備フェーズ |
| `Committed` | 3 | 永続的にコミット済み（WAL フラッシュ済み） |
| `Aborted` | 4 | ロールバック済み（明示的、または Commit なしの Dispose 時） |

## トランザクション ID {#tx-id}

`TransactionId` は単調増加する識別子である。`CommittedTxRegistry` は、どのトランザクション ID が
コミット済みかを追跡し、可視性の判断を可能にする。

## スナップショット状態 {#snapshot}

`SnapshotState` は、あるトランザクションから見えるコミット済みトランザクションの集合を捕捉する。
列スキャン集約はこれを直接用いて、オペレータパイプラインを介さずに可視性チェックを行う。

## コミット {#commit}

1. `Commit` レコードを WAL に書き込む
2. WAL をディスクにフラッシュ（同期）
3. `OnCommitted` フックを発火
4. しきい値到達かつアクティブトランザクションが無い場合、チェックポイントを起動

## アボート / ロールバック {#abort}

`AbortUndoHandler` が undo を処理する:

1. **物理 undo**: `_beforeImageStack` から before-image を LIFO 順で復元する
   （CLR レコードによるページ単位の undo）
2. **論理 undo**: `UndoFtLogical` が FT リーフ mutation を LIFO 順で巻き戻す
3. `Abort` レコードを WAL に書き込む
4. `OnRolledBack` フックを発火

Commit なしの Dispose は暗黙のアボートを引き起こす。

## セーブポイント {#savepoints}

`SavepointId` はトランザクション内のセーブポイントを識別する。セーブポイントは SQL のセマンティクスに従う:

- `Savepoint(name?)` → セーブポイントを作成し、`SavepointId` を返す
- `RollbackTo(SavepointId)` → セーブポイント以降の変更を undo し、内側のセーブポイントを無効化する
- `ReleaseSavepoint(SavepointId)` → セーブポイントを消費し、変更を親スコープにマージする

### Before-Image スタック {#before-image-stack}

`WalPageContext` は、セーブポイントごとのバケットからなる `_beforeImageStack` を保持する。各
`PinForWrite` は現在のバケットに before-image を取得する。`RollbackTo` は対象セーブポイントより
新しいバケットから before-image を復元する。

### 全文の論理 Undo {#ft-logical-undo}

全文 postings / norms のリーフは（page-image ではなく）論理 WAL を用いるため、その undo も論理的である。
FT 論理 undo ログ (`_ftUndoStack`) は `_beforeImageStack` と並行して、セーブポイントレベルごとに
バケット化される:

- **完全アボート** は全バケットの逆操作を（LIFO で）ライブ FT ツリーに再生する。
- **`RollbackTo(savepoint)`** は `>= level` のバケットのみを再生し、加えて各逆操作を
  **補償用の `FtLeafMutation`** として WAL に書き込む。FT リーフ mutation は eager にログされるため、
  セーブポイントで破棄された前向きレコードはコミット中のトランザクションの WAL に既に存在する。
  補償レコードはリカバリの redo (Pass 2b) をロールバック後の状態に収束させる。補償レコード自体は
  undo スタックに積まれないため、後の完全アボートで二重に巻き戻されることはない。

## 読み取り専用トランザクション {#read-only}

読み取り専用トランザクションはスナップショットを取得するが、WAL への書き込みや write ロックの取得は
行わない。`IsolationLevel.SnapshotIsolation` を `readOnly: true` で用いる。
