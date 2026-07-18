# ストレージ & ページング

> as-built 仕様（QUIVER-SW family version 2、2026-07-17）

## ページフォーマット {#page-format}

- **ページサイズ**: 8,192 バイト (`PagedFile.PageSizeConst`)
- **ページヘッダ**: 40 バイト（`QUIVER-SW` magic、family version、PageId、PageKind、LSN、CRC32 checksum）
- **ボディ**: `BodySize = PageSize - HeaderSize` バイト

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

`TenantPagedFile` は、複数の論理ストア（vertex、edge、nexus、property version、blob、vector payload、adjacency segment、index、全文 posting、全文 norm、catalog）を単一の `*.quiver` ファイルに多重化する。
各テナントはカタログが割り当てる `fileKind` バイトで識別される。

Primary vector payload の metadata と blob は固定テナント 29、30 に分離する。
vector definition catalog は target property と immutable segment policy を保持する。
HNSW artifact と versioned manifest は primary property から再構築可能な derived data であり、primary property value の正本ではない。

### カタログ {#catalog}

カタログテナントは、論理ストア名（インデックス名、FT インデックス名など）から
その `fileKind` バイトへのマッピングを保持する。カタログ自体もコンテナ内のテナントであり、
WAL リカバリフェーズ中に復旧される。

正常終了後の再オープンでは、カタログから versioned entity store、owner-bound property store、primary payload store、adjacency segment を同じ形式で復元する。
カタログは checkpoint 済み committed high-water と次の transaction ID も保持する。
process kill 後は、最後に完了した checkpoint 以降の winner redo と transaction ID 復元を通常 operation より先に終える。

## WAL サイドカー {#wal-sidecar}

WAL は単一のサイドカーファイル `*.quiver-wal` に存在する。
チェックポイントは writer lease を取得して active writer がいない境界を作り、`CheckpointBegin` を fsync してから committed dirty page とカタログを flush する。
データファイルの flush 後に対応する `CheckpointEnd` を fsync できた場合だけ WAL を切り詰める。
reader の終了は待たない。

## entity version sidecar {#entity-version-sidecar}

Vertex、Edge、Nexus の version sidecar は 24 バイト固定長の `EntityVersionMeta(xmin, xmax, generation)` を格納する。
8,152 バイトの page body には 339 record を格納する。
sidecar header の format version は 4 であり、旧 40 バイト layout の fallback reader は持たない。

## チェックサム {#checksum}

すべてのページはヘッダに CRC32C チェックサムを持つ。読み取り時にチェックサムを検証し、
不一致なら `CorruptionException` を発生させる。
