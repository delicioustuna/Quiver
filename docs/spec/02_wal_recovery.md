# WAL とリカバリ

> as-built 仕様（QUIVER-SW family version 1、2026-07-17）
>
> 本文は Single Writer + Snapshot Readers 再設計 Wave 2 で実装した parser foundation の契約を記す。
> Wave 5 の統合リカバリは本仕様の対象外である。

## フォーマットファミリ {#format-family}

データファイルと WAL は `QUIVER-SW` という同じフォーマットファミリに属する。
ファミリバージョンは `1` である。
旧データベースと旧 WAL は読み替えず、open 時に拒否する。
データファイルの不一致は `StorageFormatMismatchException`、WAL の不一致は `WalFormatMismatchException` で通知する。

WAL はデータファイルと同じ場所に置く単一の `*.quiver-wal` サイドカーファイルである。
先頭 16 バイトはファイルヘッダであり、magic `QUIVER-SW`、kind `W`、family version `1` を記録する。
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
レコードヘッダ、payload、CRC のいずれかが欠けた WAL も拒否する。
CRC が一致しないレコードは `CorruptionException` とし、途中までを正常なログとして扱わない。

## 書き込み契約 {#write-contract}

トランザクションは変更したページの before-image をメモリ上の write set に保持する。
同じページを複数回変更した場合、WAL へ出力する `PageImage` は最終状態へ集約する。
commit は集約済み `PageImage` と明示的な `Commit` を WAL へ書き、`Commit` の LSN まで fsync した時点で成立する。

fsync 済みの `Commit` は取り消さない。
その後の checkpoint や post-commit 処理が失敗しても `Abort` を追記せず、呼び出し側へは durable commit として扱う。
commit 前の例外、明示 abort、savepoint rollback は、メモリ上の before-image を逆順に適用してプロセス内で復元する。

読み取り専用トランザクションは `BeginWrite`、`Abort`、`PageImage` を含む WAL record を一切生成しない。
したがって、開始、読み取り、commit、dispose の全経路で WAL bytes は 0 のままである。

## リカバリ契約 {#recovery}

winner は checksum が正しい明示的な `Commit` レコードを持つトランザクションだけである。
`PageImage` の存在やページ LSN から commit を推測しない。
`BeginWrite` だけを持つトランザクションと、`Abort` で終わったトランザクションの `PageImage` は再生しない。

リカバリは WAL を解析して winner 集合と再生開始 LSN を決めた後、winner の `PageImage` を LSN 順に適用する。
同じページへ複数の committed image がある場合は、後の image が先の状態を置き換える。
`FileTruncate` は payload の file kind と page count を検証して冪等に再適用する。

Wave 2 の形式は crash undo 用の before-image、論理 mutation、compensation record を持たない。
未コミット変更をデータファイルへ永続化しないための統合境界は Wave 5 で完成させる。

## チェックポイント {#checkpoint}

`CheckpointBegin` の payload は 20 バイト、`CheckpointEnd` の payload は 8 バイトである。
`CheckpointEnd` は対応する `CheckpointBegin` の LSN を保持する。
先行する Begin と一致する End がある場合だけ、その checkpoint を完了済みとみなす。
対応しない End、不正な payload 長、途中で切れた checkpoint record は corruption として拒否する。

完了済み checkpoint があれば、その `CheckpointEnd` から後を redo 対象とする。
checkpoint の途中でクラッシュした場合は、その不完全な checkpoint を採用しない。
WAL の切り詰めはデータページの flush と checkpoint 完了後にだけ行う。

## ページとチェックサム {#pages}

ページサイズは 8 KiB、ヘッダサイズは 40 バイトである。
ページヘッダは `QUIVER-SW` magic、family version、page kind、page ID、page LSN、checksum を保持する。
page ID、family、version、checksum の不一致は open または page load 時に拒否する。

新規データベースは既定で 1 MiB を物理確保する。
容量不足時は 1、2、4、8、16、32、64 MiB の段階で適応的に成長し、既定の一回増分上限は 64 MiB である。
`InitialFileAllocationBytes` と `MaximumFileGrowthStepBytes` は 8 KiB 境界へ切り上げて適用する。
再 open 後も現在の容量から同じ規則で成長し、既存ページを保持する。
