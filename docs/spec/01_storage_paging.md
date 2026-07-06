# ストレージ & ページング

> as-built 仕様 (on-disk FormatVersion V4)

## ページフォーマット {#page-format}

- **ページサイズ**: 8,192 バイト (`PagedFile.PageSizeConst`)
- **ページヘッダ**: オフセット 0 から `PageHeader.Size` バイト（PageId, PageKind, LSN, CRC32C チェックサム）
- **ボディ**: `BodySize = PageSize - HeaderSize` バイト

## PagedFile {#paged-file}

`PagedFile` (`src/Quiver/Storage/PagedFile.cs`) は、メモリマップトファイルと Clock アルゴリズムの
バッファプールを用いて `IPagedFile` を実装する。

### バッファプール {#buffer-pool}

- デフォルト容量: 256 フレーム (`DefaultPoolCapacity`)
- 退避: `_clockHand` でフレームを走査する **Clock (second-chance)** アルゴリズム
- **STEAL ポリシー**: dirty（未コミット）ページもデータファイルへ退避できる（`EvictFrame` が
  フレームを MMF に書き出す）。WAL がクラッシュリカバリ時の undo 用に before-image (CLR) を
  ログするため、これは安全である。

### Pin / Unpin プロトコル {#pin-unpin}

| 操作 | ロック | 効果 |
|---|---|---|
| `PinForRead(PageId)` | フレーム read ロック | `ReadOnlySpan<byte>` を返し、pin カウントを増やす |
| `PinForWrite(PageId)` | フレーム write ロック | `PageWriteHandle` を返し、WAL 有効時は CLR before-image を取得する |
| `Unpin(PageId)` | read ロックを解放 | pin カウントを減らす |
| `UnpinDirty(PageId, lsn)` | write ロックを解放 | ヘッダの LSN+チェックサムを更新し、PageImage を WAL にログ、dirty マーク |

### ページアロケーション {#page-allocation}

- **Meta ページ** (PageId 0): フリーリストのヘッド (int64) と論理ページ数 (int64) を格納
- 空きページは連結リストを形成する（body[0..7] に next ポインタ）
- アロケーションはフリーリストの再利用を優先し、なければファイル末尾を拡張する
- ファイルは 64 MB 単位で拡張する (`GrowthBytes`)
- アロケーションは WAL をバイパスする（`MmfWritePageAndSync` で LSN=0 として直接書き込む）

### メモリマップトファイル {#mmf}

`MemoryMappedFile` + `MemoryMappedViewAccessor` がバッキングストレージを提供する。
ファイル拡張は unmap/remap を引き起こす (`EnsureFileSizeAndRemapLocked`)。

## インメモリ物理層 {#in-memory-paged-file}

`InMemoryPagedFile` は同じ `IPagedFile` 契約を実装し、8 KB ページとページ単位の read/write ロックを
プロセス内 RAM に保持する。`SingleFileContainer` より上のストア、索引、MVCC、rollback 経路は
バイナリバックエンドと共通であり、物理層と WAL だけを `InMemoryPagedFile` /
`NullWriteAheadLog` に差し替える。

`GraphDatabase.CreateInMemory()` または `GraphDatabase.Open(":memory:")` で選択する。
`Flush()` は no-op で、データファイル、WAL、チェックポイント、リカバリは作成しない。
インスタンスを破棄すると全ページが失われる。

## 単一ファイルコンテナ {#single-file}

`TenantPagedFile` は、複数の論理ストア（nodes, relationships, properties, indexes, vectors,
FT postings, FT norms, catalog）を単一の `*.quiver` ファイルに多重化する。
各テナントはカタログが割り当てる `fileKind` バイトで識別される。

### カタログ {#catalog}

カタログテナントは、論理ストア名（インデックス名、FT インデックス名など）から
その `fileKind` バイトへのマッピングを保持する。カタログ自体もコンテナ内のテナントであり、
WAL リカバリフェーズ中に復旧される。

## WAL サイドカー {#wal-sidecar}

WAL は単一のサイドカーファイル `*.quiver-wal` に存在する。チェックポイントは dirty ページと
インデックスをデータファイルにフラッシュし、その後 WAL を切り詰める。

## チェックサム {#checksum}

すべてのページはヘッダに CRC32C チェックサムを持つ。読み取り時にチェックサムを検証し、
不一致なら `CorruptionException` を発生させる。
