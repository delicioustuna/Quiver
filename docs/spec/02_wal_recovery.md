# WAL とリカバリ

> as-built 仕様（QUIVER-SW family version 2、2026-08-02）

## フォーマットファミリ {#format-family}

データファイルと WAL は `QUIVER-SW` という同じフォーマットファミリに属する。
ファミリバージョンは `2` である。
旧データベースと旧 WAL は読み替えず、open 時に拒否する。
データファイルの不一致は `StorageFormatMismatchException`、WAL の不一致は `WalFormatMismatchException` で通知する。

WAL はデータファイルと同じ場所に置く単一の `*.yata-wal` サイドカーファイルである。
先頭 16 バイトはファイルヘッダであり、magic `QUIVER-SW`、kind `W`、family version `2` を記録する。
byte 11～15は予約領域で0とし、非0の未知header extensionは`WalFormatMismatchException`で拒否する。
空の WAL は open 時に現行ヘッダで初期化する。

## WAL レコード {#wal-records}

各レコードは `Length(4) + LSN(8) + TransactionId(8) + Type(1) + CRC32(4) + Payload` で構成する。
整数は little-endian で格納する。
LSN は WAL 内で単調に増加する `Int64` である。

| 種別 | 値 | 役割 |
|---|---:|---|
| `BeginWrite` | 1 | 書き込みトランザクションの開始 |
| `PageImage` | 2 | ページ全体の after-image |
| `Commit` | 3 | トランザクションの永続化境界 |
| `Abort` | 4 | 未コミットトランザクションの終了 |
| `CheckpointBegin` | 5 | チェックポイント開始 |
| `CheckpointEnd` | 6 | 対応するチェックポイントの完了 |
| `FileTruncate` | 7 | ページファイルの切り詰め |

上表以外の type は corruption として拒否する。
family version 2にはrecordのrequired / ignorable識別子がないため、length prefixが妥当でも未知typeをskipしない。
1.xでは上表のrecord集合を固定し、新しいrecovery意味論はfamily bumpなしに追加しない。
レコードヘッダ、payload、CRC のいずれかが欠けた WAL も拒否する。
CRC が一致しないレコードは `CorruptionException` とし、途中までを正常なログとして扱わない。

## 書き込み契約 {#write-contract}

トランザクションは変更したページの before-image をメモリ上の write set に保持する。
同じページを複数回変更した場合、WAL へ出力する `PageImage` は最終状態へ集約する。
commit は集約済み `PageImage` と明示的な `Commit` を WAL へ書き、`Commit` の LSN まで fsync した時点で成立する。
各 `PageImage` の record LSN は、payload 内の page LSN と同じ値である。

fsync 済みの `Commit` は取り消さない。
その後の post-commit 処理が失敗しても `Abort` を追記せず、呼び出し側へは durable commit として扱う。
commit 前の例外、明示 abort、savepoint rollback は、メモリ上の before-image を逆順に適用してプロセス内で復元する。

active writer が変更した、committed 構造から到達可能な dirty page は、commit fsync 前にデータファイルへ書かない。
退避、close、checkpoint、buffer pressure もこの no-steal 規則を迂回しない。
commit はデータページの flush を待たないため、no-force である。

バッファプールが active writer の dirty page で埋まり、pin できる退避候補がなくなった場合は `TransactionTooLargeException` を送出する。
エンジンは before-image を適用してその writer を自動 abort し、writer lease を解放する。

読み取り専用トランザクションは `BeginWrite`、`Abort`、`PageImage` を含む WAL record を一切生成しない。
したがって、開始、読み取り、commit、dispose の全経路で WAL bytes は 0 のままである。

## リカバリ契約 {#recovery}

winner は checksum が正しい明示的な `Commit` レコードを持つトランザクションだけである。
`PageImage` の存在やページ LSN から commit を推測しない。
`BeginWrite` だけを持つトランザクションと、`Abort` で終わったトランザクションの `PageImage` は再生しない。

リカバリは WAL を解析して winner 集合と再生開始 LSN を決めた後、winner の `PageImage` を LSN 順に適用する。
同じページへ複数の committed image がある場合は、後の image が先の状態を置き換える。
データファイル上の page LSN が `PageImage` の LSN 以上なら、その image は適用済みとして読み飛ばす。
WAL record LSN と payload 内の page LSN が一致しない image は corruption として拒否する。
`FileTruncate` は payload の file kind と page count を検証して冪等に再適用する。

crash recovery は loser の undo pass、論理 mutation、compensation record、全文専用 pass を持たない。

全文 segment は primary text property から再構築できる derived artifact であり、segment-local posting、norm、tombstone の専用 WAL record を持たない。
segment body は manifest transaction より前に checksum 付き immutable artifact file として fsync する。
recovery は winner の catalog PageImage から manifest を復元し、artifact ID、length、checksum を検証する。
未参照 file は不可視 orphan であり、欠損または checksum 不一致の参照は primary corruption ではなく `RebuildRequired` とする。
loser の物理変更は no-steal によってデータファイルへ到達しないため、winner redo だけで復旧できる。

open はデータベースheaderをWAL sidecarの作成より前に検証し、その後WAL header、WAL record、再生する
page imageを検証する。非対応database / WALは元fileを書き換えずに拒否し、recoveryと完了checkpointを
終えてから通常operationを受け付ける。
WAL で観測した transaction のうち winner でない ID は aborted gap に復元する。
次の transaction ID は、checkpoint 済み catalog と WAL で観測した最大 ID の後へ進める。

## チェックポイント {#checkpoint}

`CheckpointBegin` の payload は 20 バイト、`CheckpointEnd` の payload は 8 バイトである。
`CheckpointEnd` は対応する `CheckpointBegin` の LSN を保持する。
先行する Begin と一致する End がある場合だけ、その checkpoint を完了済みとみなす。
対応しない End、不正な payload 長、途中で切れた checkpoint record は corruption として拒否する。

checkpoint は writer lease を取得し、active writer がいない sharp boundary で実行する。
reader の終了は待たない。
コミット後にWALサイズのしきい値を超えて起動するチェックポイントは、延期可能な保守処理である。
別の書き込み側がすでに権限を取得している場合は、待機も例外送出もせず、その回を見送る。
残ったWALは、次のコミット時、または終了時の明示的なチェックポイントで処理する。
明示的なチェックポイントは、従来どおり書き込み権限の取得を待つ。

`CheckpointBegin` を fsync した後に committed high-water と次の transaction ID を catalog へ保存し、committed dirty page とデータファイルを flush する。
対応する `CheckpointEnd` を fsync できた場合だけ checkpoint を完了済みとみなし、その End 以前の WAL を切り詰める。

checkpoint の各段階でクラッシュした場合は、対応する End のない checkpoint を採用しない。
open-time recovery の redo 後も同じ手順で完了 checkpoint を作るため、再びクラッシュしても page LSN による冪等 redoから再開できる。

## ページとチェックサム {#pages}

ページサイズは 8 KiB、ヘッダサイズは 40 バイトである。
ページヘッダは `QUIVER-SW` magic、family version、page kind、page ID、page LSN、checksum を保持する。
page ID、family、version、checksum の不一致は open または page load 時に拒否する。

新規データベースは既定で 1 MiB を物理確保する。
容量不足時は 1、2、4、8、16、32、64 MiB の段階で適応的に成長し、既定の一回増分上限は 64 MiB である。
`InitialFileAllocationBytes` と `MaximumFileGrowthStepBytes` は 8 KiB 境界へ切り上げて適用する。
再 open 後も現在の容量から同じ規則で成長し、既存ページを保持する。
