# ストレージ & ページング

> as-built 仕様（QUIVER-SW family version 2、2026-09-09）

## ページフォーマット {#page-format}

- **ページサイズ**: 8,192 バイト (`PagedFile.PageSizeConst`)
- **ページヘッダ**: 40 バイト（`QUIVER-SW` magic、family version、PageId、PageKind、LSN、CRC32 checksum）
- **ボディ**: `BodySize = PageSize - HeaderSize` バイト

page headerの予約byteは0である。同じfamily versionを名乗るpageに非0の未知header extensionがある場合、
`Open`はWAL sidecarを作成する前に`CorruptionException`で拒否する。

## PagedFile {#paged-file}

`PagedFile` (`src/Yatagarasu/Storage/PagedFile.cs`) は、メモリマップトファイルと Clock アルゴリズムの
バッファプールを用いて `IPagedFile` を実装する。

### バッファプール {#buffer-pool}

- デフォルト容量: 256 フレーム (`DefaultPoolCapacity`)
- 常駐ページの検索と固定数・参照ビットの更新は、PageIdの安定したハッシュ値で選ぶ16個のシャードのいずれかで保護する。
  フレーム容量は全シャードで共有する。PageIdの分布が偏っても、シャードごとの固定容量による早期の追い出しは発生しない。
- 退避: `_clockHand` でフレームを走査する **Clock (second-chance)** アルゴリズム
- active writer が所有する dirty frame は退避せず、データファイルにも書かない。
- committed dirty frame は checkpoint または退避時にデータファイルへ書ける。
- pin できる退避候補が尽きると、書き込みトランザクションを `TransactionTooLargeException` で中止する。
  transaction-owned before-image を適用してから writer lease を解放するため、chunk commit で再試行できる。

### Pin / Unpin プロトコル {#pin-unpin}

ロックは全体調整用のロック、全シャードの番号昇順で取得し、逆順で解放する。
常駐ページを取得する場合は対象シャードだけをロックする。未常駐なら一度解放してから全体ロックと全シャードを取得し、
常駐状態を再検査する。シャードだけを保持したまま全体ロックを待つことはない。
追い出し、ページ対応表の変更、復旧、書き出し、切り詰め、メモリマップの再作成、破棄は同じロック範囲で行う。
割り当てなど内部からの再入も同じ範囲を使い、`System.Threading.Lock`の再入規則に従う。
読み取り専用の分析では、全体調整用のロックの下で常駐状態を確認し、未常駐ページを別のバッファへ読む。
これにより書き出しを発生させない。単一シャードの経路を使うのは、通常の常駐ページ取得だけである。

フレームの識別情報・世代、Clockアルゴリズムの参照ビット、ページとフレームの対応の再割り当ては、全シャードのロックの保持下で行う。
常駐ページを取得する際の参照ビットの更新と固定数の増加は、そのページのシャードのロックの保持下で行う。
固定数が正のフレームは追い出せないため、シャードの解放後もフレームのロック取得と利用中の識別情報は変わらない。
常駐ページ取得時の`MeterListener`への通知はシャードの解放後に行い、コールバックの例外時も固定を解除する。
通常のページ取得では、シャードを解放してからフレームをロックする。
書き出しではフレームの読み取りロックを待機せず取得し、書き込み側が保持するフレームを待たない。
変更状態とLSNは、既存のフレームロック、書き込み権限、WALの永続化境界に従う。

破棄では全シャードのロック取得後に書き出しを行い、メモリマップとファイルを破棄する。
以後のページ取得は`ObjectDisposedException`で拒否する。
取得済みの読み取り用バッファは管理メモリ上に保持するため、破棄と競合しても読み取り権限を解放できる。
書き込みについては、権限を保持したまま破棄せず、書き込みを終えてから閉じる。

| 操作 | ロック | 効果 |
|---|---|---|
| `PinForRead(PageId)` | フレームの読み取りロック | `ReadOnlySpan<byte>`を返し、固定数を増やす |
| `PinForWrite(PageId)` | フレームの書き込みロック | `PageWriteHandle`を返し、更新集合に変更前イメージを保存する |
| `ReleaseRead(ReadPageLease)` | 読み取りロックを解放 | 取得時のフレーム番号・世代で直接解放し、最後に固定数を減らす。プールのロックとページ検索は使わない |
| `ReleaseWriteUnchanged(WritePageLease)` | 書き込みロックを解放 | 変更前の検証失敗時に固定を解除する。変更済みの印、チェックサム・LSNの更新、変更後イメージの登録は行わない |
| `ReleaseWriteDirty(WritePageLease, lsn)` | 書き込みロックを解放 | トランザクションが所有する更新集合へ最終の変更後イメージを登録し、変更所有者を記録する。プールのロックとページ検索は使わない |

`ReleaseWriteDirty`の時点ではページLSNを確定しない。
コミットが`PageImage`を追記するときに割り当てたLSNをWALの内容とフレームの両方へ記録し、`Commit`の同期書き込み後に変更所有者を解除する。

Vertex・Edge・Nexusのストアへの書き込みでは、ページ取得後のスロット検証とレコード範囲の取得全体で例外を処理する。
変更前に失敗した場合は`PageWriteHandle.ReleaseUnchanged()`で書き込み権限を解放し、元の例外を再送出する。
この経路は変更済みバッファの巻き戻しには使わない。ページ取得時の変更前イメージは維持するが、変更後イメージは登録しない。
読み取り権限から書き込みモードを推測してロックを解放することはない。
ディスク上のページの書き込みを終える際は、書き込みロックを先に解放し、固定数を最後に減らす。
これにより、ロック中のフレームが追い出しや再割り当ての対象になることを防ぐ。
変更後イメージの登録が失敗した場合も、ロックと固定を解除する。
変更済みページはトランザクションが所有する変更前イメージから巻き戻す責務があり、未変更としての解放へ切り替えない。

`PageReadHandle`と`PageWriteHandle`は、物理PageId、フレーム番号、フレーム世代を含む専用の権限情報を保持する。
フレーム世代はディスクからページを読み込むたびに増える内部値で、保存形式のエンティティ世代とは異なる。
解放時はフレームの識別情報と、現在のスレッドが対応するロックを保持していることを検証する。
フレームのロックを先に解放し、`Interlocked.Decrement`で固定数を減らす。固定数が正の間は追い出し・再割り当てを行えない。
読み取り・書き込みロックの取得が失敗した場合も、先に増やした固定数を例外処理内で戻す。
テナントが返すハンドルは物理フレームの権限情報を保持し、解放時に論理ページを再変換しない。

ハンドルはコピーせず、取得したスレッドで一度だけ解放する。
通常のビルドでは、同じハンドルの二重解放と再割り当て後の古いフレームへの操作を拒否する。
Debugビルドでは共有する検査用の状態により、ハンドルのコピーが同じフレームの別の固定を解除する誤用も検出する。
この検査用のメモリ割り当てはReleaseビルドには存在しない。
Vertex・Edge・Nexus・Incidenceの借用書き込みハンドルは、`PageWriteHandle`の所有権を引き継ぐ。
移譲元は解放できず、移譲先だけが権限を解放する。ページ番号だけを受け取る解放APIは持たない。

書き出し・チェックポイント・再マップでは、フレームの読み取りロックを待機せず取得し、
取得できない書き込み中のページを、その回の保存対象から外す。現在のスレッドが書き込みロックを持つ場合も同様である。
読み取りで固定中のコミット済み変更ページは保存でき、実行中のトランザクションが所有する変更ページは保存しない。
チェックポイントは、書き込み権限を取得し、実行中の書き込みがない境界で実行する。

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

`YatagarasuDatabase.CreateInMemory()` または `YatagarasuDatabase.Open(":memory:")` で選択する。
`Flush()` は no-op で、データファイル、WAL、チェックポイント、リカバリは作成しない。
インスタンスを破棄すると全ページが失われる。

## 単一ファイルコンテナ {#single-file}

`TenantPagedFile` は、複数の論理ストア（vertex、edge、nexus、property version、blob、vector payload、adjacency segment、scalar index、definition catalog）を単一の `*.yata` ファイルに多重化する。

全文の term data、document length、stats は `*.yata-ftseg/` 内の checksum 付き immutable segment file に置き、専用 B+Tree tenant を割り当てない。
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

WAL は単一のサイドカーファイル `*.yata-wal` に存在する。
チェックポイントは writer lease を取得して active writer がいない境界を作り、`CheckpointBegin` を fsync してから committed dirty page とカタログを flush する。
データファイルの flush 後に対応する `CheckpointEnd` を fsync できた場合だけ WAL を切り詰める。

全文 index を持つ database は `*.yata-ftseg/` artifact directory も保持する。
online snapshot は container と WAL に加えてこの append-only artifact を複製する。
reader の終了は待たない。

## オフラインストレージ移行 {#storage-upgrade}

`YatagarasuDatabase.UpgradeStorage(path, options)` は `Open` より前に呼ぶ明示的な
offline operation である。先頭ページを `PagedFile` で開く前に raw inspection し、
`QUIVER-SW` magic と family version を判定する。現行 family version 2 のデータベースは
先頭ページの checksum まで検証した後、ファイルを書き換えず `AlreadyCurrent` を返す。
通常の`Open`も同じraw inspectionをWAL作成より前に行い、非対応familyと未知header extensionを元fileの
書換えなしで拒否する。

現行 build に登録された移行 step がない source version は、source と target version を持つ
`StorageUpgradeNotSupportedException` で拒否する。v0.4.0 と現行形式はどちらも family version 2
であり、現行 build に旧 layout decoder や実変換 step は登録されていない。

将来 step を登録するときも source page の in-place rewrite は行わない。step は排他された source の
読み取りストリームから、source と同じディレクトリの一時 database を構築する。Yatagarasu は一時 file を
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
