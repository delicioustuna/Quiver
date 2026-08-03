# ストレージ & ページング

> as-built 仕様（QUIVER-SW family version 2、2026-08-02）

## ページフォーマット {#page-format}

- **ページサイズ**: 8,192 バイト (`PagedFile.PageSizeConst`)
- **ページヘッダ**: 40 バイト（`QUIVER-SW` magic、family version、PageId、PageKind、LSN、CRC32 checksum）
- **ボディ**: `BodySize = PageSize - HeaderSize` バイト

page headerの予約byteは0である。同じfamily versionを名乗るpageに非0の未知header extensionがある場合、
`Open`はWAL sidecarを作成する前に`CorruptionException`で拒否する。

## PagedFile {#paged-file}

`PagedFile` (`src/Quiver/Storage/PagedFile.cs`) は、メモリマップトファイルと Clock アルゴリズムの
バッファプールを用いて `IPagedFile` を実装する。

### バッファプール {#buffer-pool}

- デフォルト容量: 256 フレーム (`DefaultPoolCapacity`)
- 退避: `_clockHand` でフレームを走査する **Clock (second-chance)** アルゴリズム
- active writer が所有する dirty frame は退避せず、データファイルにも書かない。
- committed dirty frame は checkpoint または退避時にデータファイルへ書ける。
- pin できる退避候補が尽きると、書き込みトランザクションを `TransactionTooLargeException` で中止する。
  transaction-owned before-image を適用してから writer lease を解放するため、chunk commit で再試行できる。

### Pin / Unpin プロトコル {#pin-unpin}

| 操作 | ロック | 効果 |
|---|---|---|
| `PinForRead(PageId)` | フレーム read ロック | `ReadOnlySpan<byte>` を返し、pin カウントを増やす |
| `PinForWrite(PageId)` | フレーム write ロック | `PageWriteHandle` を返し、write set に before-image を取得する |
| `Unpin(PageId)` | read ロックを解放 | pin カウントを減らす |
| `UnpinDirty(PageId, lsn)` | write ロックを解放 | transaction-owned write set へ最終 after-image を登録し、dirty owner を記録する |

`UnpinDirty` の時点では page LSN を確定しない。
commit が `PageImage` を追記するときに割り当てた LSN を WAL payload とフレームの両方へ刻み、`Commit` の fsync 後に dirty owner を解除する。

### ページアロケーション {#page-allocation}

- **Meta ページ** (PageId 0): フリーリストのヘッド (int64) と論理ページ数 (int64) を格納
- 空きページは連結リストを形成する（body[0..7] に next ポインタ）
- アロケーションはフリーリストの再利用を優先し、なければファイル末尾を拡張する
- 新規ファイルの既定物理確保量は 1 MiB
- 容量不足時は 1、2、4、8、16、32、64 MiB の段階で成長する
- 一回の増分上限は既定 64 MiB であり、初期量と上限は option で設定できる
- すべての確保量は 8 KiB page 境界へ切り上げる
- committed allocation high-water を超える末尾ページは、既存の committed 構造から到達不能な物理領域として commit 前に確保できる
- root、catalog、free-list から新規ページを到達可能にする after-image は、同じ transaction-owned write set と strict Commit 境界に従う

### メモリマップトファイル {#mmf}

`MemoryMappedFile` + `MemoryMappedViewAccessor` がバッキングストレージを提供する。
ファイル拡張は unmap/remap を引き起こす (`EnsureFileSizeAndRemapLocked`)。

## インメモリ物理層 {#in-memory-paged-file}

`InMemoryPagedFile` は同じ `IPagedFile` 契約を実装し、8 KB ページとページ単位の read/write ロックを
プロセス内 RAM に保持する。`SingleFileContainer` より上のストア、索引、MVCC、rollback 経路は
バイナリバックエンドと共通であり、物理層と WAL だけを `InMemoryPagedFile` /
`NullWriteAheadLog` に差し替える。

`QuiverDatabase.CreateInMemory()` または `QuiverDatabase.Open(":memory:")` で選択する。
`Flush()` は no-op で、データファイル、WAL、チェックポイント、リカバリは作成しない。
インスタンスを破棄すると全ページが失われる。

## 単一ファイルコンテナ {#single-file}

`TenantPagedFile` は、複数の論理ストア（vertex、edge、nexus、property version、blob、vector payload、adjacency segment、scalar index、definition catalog）を単一の `*.quiver` ファイルに多重化する。

全文の term data、document length、stats は `*.quiver-ftseg/` 内の checksum 付き immutable segment file に置き、専用 B+Tree tenant を割り当てない。
artifact ID、checksum、source high-water、lifecycle state を持つ小さい manifest は definition catalog tenant に保存する。
各テナントはカタログが割り当てる `fileKind` バイトで識別される。

Primary vector payload の metadata と blob は固定テナント 29、30 に分離する。

export provenance用の `DatabaseInstanceId` はoptional metadata tenant 32に保存する。
新規DBでは一度生成し、再openと物理snapshot copyで維持する。tenantを持たないv0.4 DBを
読み取りtransactionからexportしてもtenantを作らず、最初の書き込み開始前に独立したinternal
transactionで生成・commitする。したがってcallerの最初のwrite transactionがrollbackしてもIDは維持する。
`TryGetDatabaseInstanceId`はtenantを作らず、永続IDがまだ無い場合は`false`を返す。
in-memory backendでもinstance lifetime中は一度生成したIDを維持するが、dispose後には残らない。
このIDはsource document間の同一性判定専用であり、entity identityや認証境界ではない。
vector definition catalog は target property と immutable segment policy を保持する。
HNSW artifact と versioned manifest は primary property から再構築可能な derived data であり、primary property value の正本ではない。

### カタログ {#catalog}

カタログテナントは、論理ストア名（インデックス名、FT インデックス名など）から
その `fileKind` バイトへのマッピングを保持する。カタログ自体もコンテナ内のテナントであり、
WAL リカバリフェーズ中に復旧される。
現行buildが利用しないtenant descriptorもopaque entryとしてcatalogへ保持される。旧buildが無視しても
primary stateとrecoveryの意味が変わらないoptional dataに限り、既知tenantの更新後もdescriptorとpage tableを維持する。

正常終了後の再オープンでは、カタログから versioned entity store、owner-bound property store、primary payload store、adjacency segment を同じ形式で復元する。
カタログは checkpoint 済み committed high-water と次の transaction ID も保持する。
process kill 後は、最後に完了した checkpoint 以降の winner redo と transaction ID 復元を通常 operation より先に終える。

## WAL サイドカー {#wal-sidecar}

WAL は単一のサイドカーファイル `*.quiver-wal` に存在する。
チェックポイントは writer lease を取得して active writer がいない境界を作り、`CheckpointBegin` を fsync してから committed dirty page とカタログを flush する。
データファイルの flush 後に対応する `CheckpointEnd` を fsync できた場合だけ WAL を切り詰める。

全文 index を持つ database は `*.quiver-ftseg/` artifact directory も保持する。
online snapshot は container と WAL に加えてこの append-only artifact を複製する。
reader の終了は待たない。

## オフラインストレージ移行 {#storage-upgrade}

`QuiverDatabase.UpgradeStorage(path, options)` は `Open` より前に呼ぶ明示的な
offline operation である。先頭ページを `PagedFile` で開く前に raw inspection し、
`QUIVER-SW` magic と family version を判定する。現行 family version 2 のデータベースは
先頭ページの checksum まで検証した後、ファイルを書き換えず `AlreadyCurrent` を返す。
通常の`Open`も同じraw inspectionをWAL作成より前に行い、非対応familyと未知header extensionを元fileの
書換えなしで拒否する。

現行 build に登録された移行 step がない source version は、source と target version を持つ
`StorageUpgradeNotSupportedException` で拒否する。v0.4.0 と現行形式はどちらも family version 2
であり、現行 build に旧 layout decoder や実変換 step は登録されていない。

将来 step を登録するときも source page の in-place rewrite は行わない。step は排他された source の
読み取りストリームから、source と同じディレクトリの一時 database を構築する。Quiver は一時 file を
durable flush し、target family と step 固有の整合性検証が成功した後だけ switch marker を永続化する。
その後 source を rollback copy へ rename し、完成済み target を source path へ rename する。
各 rename 境界で中断しても、次の `UpgradeStorage` は marker と source / target / rollback copy の
存在状態から切替を完了する。完成済み target が失われていれば rollback copy を source path へ戻す。

移行前 copy は既定で `<source>.pre-upgrade-v<version>.bak` に保持する。
`StorageUpgradeOptions.KeepBackup = false` でも切替完了までは内部 rollback copy を保持し、
完成済み target の検証後だけ削除する。原子的な rename 境界を保つため、明示 backup path は
source と同じディレクトリに限る。source WAL の解釈と clean-state の検証は format 固有 step の責務であり、
汎用 orchestration が未知の WAL を削除または現行形式として解釈することはない。

## entity version sidecar {#entity-version-sidecar}

Vertex、Edge、Nexus の version sidecar は 24 バイト固定長の `EntityVersionMeta(xmin, xmax, generation)` を格納する。
8,152 バイトの page body には 339 record を格納する。
sidecar header の format version は 4 であり、旧 40 バイト layout の fallback reader は持たない。

## チェックサム {#checksum}

すべてのページはヘッダに CRC32C チェックサムを持つ。読み取り時にチェックサムを検証し、
不一致なら `CorruptionException` を発生させる。
