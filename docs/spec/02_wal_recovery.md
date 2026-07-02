# WAL & リカバリ

> as-built 仕様 (on-disk FormatVersion V2)

## Write-Ahead Logging {#wal}

Quiver はクラッシュリカバリに ARIES スタイルの write-ahead logging を用いる。WAL ファイル
(`*.quiver-wal`) は可変長レコードのシーケンシャルログであり、各レコードは単調増加する LSN を持つ。

### WAL レコード種別 {#record-types}

| 種別 | 値 | 目的 |
|---|---|---|
| `Begin` | 1 | トランザクション開始 |
| `Commit` | 2 | トランザクションコミット（永続性の境界） |
| `Abort` | 3 | 明示的アボート |
| `PageImage` | 10 | ページ全体の after-image（redo 用） |
| `PageDelta` | 11 | ページへのデルタ更新 |
| `CompensationLogRecord` | 12 | undo 用の before-image (CLR) |
| `IndexMutation` | 13 | レガシーな論理 B+Tree mutation（予約済み、現在は発行しない） |
| `CheckpointBegin` | 14 | チェックポイント開始のセンチネル |
| `CheckpointEnd` | 15 | チェックポイント完了のセンチネル |
| `FileTruncate` | 16 | Vacuum によるページファイルの切り詰め |
| `FtLeafMutation` | 17 | 全文 postings/norms リーフの論理 mutation |
| `FtStructureImage` | 18 | 全文 B+Tree 構造ページの after-image（nested top action） |
| `Checkpoint` | 100 | レガシーな単一レコードチェックポイント（読み取り専用の互換） |
| `EndOfSegment` | 0xFE | セグメント境界マーカー |

### Write-Ahead 保証 {#write-ahead}

dirty ページがバッファプールから退避される (STEAL) 前に、WAL はそのページの CLR
(CompensationLogRecord = before-image) を少なくとも含んでいなければならない。`UnpinDirty` 時には
after-image (PageImage) もログされる。これによりクラッシュ後の redo と undo の双方が可能になる。

### Presume-Committed {#presume-committed}

Quiver は **presume-committed** リカバリ戦略を用いる。トランザクションがコミット済みとみなされるのは、
WAL に `Commit` レコードが見つかった場合に限る。Commit レコードを持たないトランザクションは
loser とみなされ、undo される。

## 2 フェーズリカバリ {#two-phase-recovery}

`RecoveryManager` はリカバリを 2 フェーズで実装する:

### Pass 1: 解析 + Redo {#pass-1-redo}

直近の完了済みチェックポイント（対応する `CheckpointBegin`/`CheckpointEnd` のペアで特定）から
WAL を走査する。各レコードについて:

- **PageImage / PageDelta**: ページイメージをデータファイルに書き込んで redo する
- **FtStructureImage**: 無条件に redo する（nested top action -- undo されない）
- **FtLeafMutation（コミット済み tx）**: state-setting な `ApplyFtLeafRedo` で redo する
- **Begin / Commit / Abort**: トランザクションのステータスを追跡する
- **FileTruncate**: 切り詰めを冪等に再適用する

Winner（コミット済み）トランザクションのページイメージは前向きに redo される。これにより
データファイルは少なくとも最後の WAL フラッシュ時点の状態まで復元される。

### Pass 2: Undo（loser トランザクション） {#pass-2-undo}

`Begin` はあるが `Commit` も `Abort` もないトランザクション（loser）について:

- **CLR (CompensationLogRecord)**: before-image をデータファイルに復元する
- **FtLeafMutation**: LIFO 順で逆操作（undo）を適用する

Undo は loser トランザクションのレコードを LSN 降順で処理し、トランザクション前の状態を復元する。

## チェックポイント {#checkpoint}

`Checkpointer` (`src/Quiver/Transactions/Checkpointer.cs`) は定期的なチェックポイントを実行する:

### チェックポイントのフェーズ {#checkpoint-phases}

| フェーズ | アクション |
|---|---|
| `AfterBegin` | `CheckpointBegin` を WAL に書き込む |
| `AfterDataFlush` | バッファプールの全 dirty ページをデータファイルにフラッシュ (`PageManager.FlushAll`) |
| `AfterIndexFlush` | 全インデックスをフラッシュ (`IndexManager.FlushAll`) |
| `AfterEnd` | `CheckpointEnd` を WAL に書き込む |
| `AfterTruncate` | WAL の先頭部分を切り詰める（このチェックポイント以前のレコードを除去） |

### アトミック性 {#checkpoint-atomicity}

チェックポイントが完了とみなされるのは、`CheckpointBegin` と `CheckpointEnd` の双方が存在する場合のみ。
チェックポイント途中でクラッシュした場合（例: data flush 後・End 前）、リカバリは直前の完了済み
チェックポイントにフォールバックしてそこから再生する。これにより、部分フラッシュの状態が
永続的なものとして扱われるのを防ぐ。

### チェックポイントの起動契機 {#checkpoint-trigger}

チェックポイントは、書き込まれた WAL バイト数が `GraphDatabaseOptions.CheckpointThresholdBytes` を
超え、かつアクティブなトランザクションが 1 つも進行中でないときに起動される。

## LSN {#lsn}

Log Sequence Number (LSN) は単調増加する int64 である。各ページヘッダは最後の変更時の LSN を保持する。
リカバリはページ LSN と WAL レコード LSN を比較して redo が必要か判定する
（ページ LSN >= レコード LSN ならスキップ）。
