# インメモリモード実装計画

## Context

SQLite の `:memory:` に相当する、ディスク I/O を一切伴わない RAM 専用モードを Quiver に追加する。
用途: ユニットテストの高速化、一時的な計算グラフ、永続化不要な組み込みシナリオ。

Quiver のストレージ層は `IPagedFile` インタフェースで十分に抽象化されており、
全ストア・索引・トランザクションは `IPagedFile` のみに依存する。
唯一の結合点は `SingleFileContainer` と `TenantPagedFile` が具象 `PagedFile` を
フィールドに持っている点で、これを `IPagedFile` に緩和すれば
`InMemoryPagedFile` をドロップイン置換できる。

## 方針

既存のページ多重化層 (`SingleFileContainer` + `TenantPagedFile`) をそのまま再利用し、
物理層だけを `InMemoryPagedFile` に差し替える。
これにより新規コードを最小化し、abort undo / MVCC / WAL page context の
既存経路をすべて透過的に動かす。

## Phase 1: SingleFileContainer / TenantPagedFile の IPagedFile 化

### 変更ファイル
- `src/Quiver/Storage/SingleFile/SingleFileContainer.cs`
- `src/Quiver/Storage/SingleFile/TenantPagedFile.cs`

### 内容

**SingleFileContainer:**
- `private readonly PagedFile _physical` → `private readonly IPagedFile _physical`
- `internal PagedFile Physical` → `internal IPagedFile Physical`
- 既存コンストラクタ `(string path, int poolCapacityPages)` は `new PagedFile(...)` を
  `_physical` に代入する (動作変更なし)
- 新コンストラクタ `internal SingleFileContainer(IPagedFile physical)` を追加。
  ファイルからの catalog 読み込みと同じフローを踏む
  (`_physical.PageCount <= 1` なら新規初期化、それ以外は `LoadCatalog`)
- `PersistCatalogLocked` 内の `PagedFile.BodySize` → 定数 `PagedFile.PageSizeConst - PageHeader.Size`
  に置き換え (静的定数なので意味的に同一)
- `EntriesPerPageTablePage` も同様に定数参照を維持

**TenantPagedFile:**
- `private readonly PagedFile _physical` → `private readonly IPagedFile _physical`
- コンストラクタ引数 `PagedFile physical` → `IPagedFile physical`
- `int IPagedFile.PageSize => PagedFile.PageSizeConst;` — 静的定数なので変更なし
- `Unpin`/`UnpinDirty` の `((IPagedFile)_physical)` キャストは不要になるが、
  明示的インタフェース実装を呼ぶので残しても害はない

### 検証
`dotnet build` + 既存テスト全パス。動作変更なしの型拡張のみ。

---

## Phase 2: InMemoryPagedFile

### 新規ファイル
`src/Quiver/Storage/InMemoryPagedFile.cs`

### 設計
`IPagedFile` の RAM 専用実装。ページを `List<byte[]>` に格納する。

```
internal sealed class InMemoryPagedFile : IPagedFile
    - _pages: List<byte[]>        // index = PageId, 各 byte[8192]
    - _freeListHead: long         // メタページと同等の free list (-1 = 空)
    - _logicalPageCount: long     // 次に末尾割当する PageId
    - _pageLocks: List<ReaderWriterLockSlim>  // per-page R/W ロック
    - _allocLock: object          // AllocatePage / FreePage の排他
    - _walFileKind: byte?         // EnableWalLogging で設定
    - _wal: IWriteAheadLog?       // before-image 用 WAL 参照
```

**主要メソッド:**

| メソッド | 実装 |
|---------|------|
| `AllocatePage` | free list から再利用、無ければ末尾に `new byte[8192]` を追加。PageHeader.Write で初期化 |
| `FreePage` | free list に push。ページ body 先頭に next free を書く (PagedFile と同じレイアウト) |
| `PinForRead` | `_pageLocks[id].EnterReadLock()` → `new PageReadHandle(this, id, span)` |
| `PinForWrite` | `_pageLocks[id].EnterWriteLock()` → WalPageContext.SetJournalMode + CaptureBeforeImage → `new PageWriteHandle(this, id, span)` |
| `Unpin` | `_pageLocks[id].ExitReadLock()` |
| `UnpinDirty` | `PageHeader.UpdateLsnAndChecksum` + `WalPageContext.LogPageImage` → `ExitWriteLock()` |
| `Flush` | no-op |
| `EnableWalLogging` | `_walFileKind` と `_wal` を保存 |
| `WritePageForRecovery` | `pageBytes.CopyTo(_pages[id])` — abort undo が before-image を直接書き戻す経路 |
| `Truncate` | `_pages` をリサイズ + ロック配列を縮小 |
| `PageSize` | `PagedFile.PageSizeConst` (8192) |
| `PageCount` | `_logicalPageCount` |
| `Path` | `string.Empty` |

コンストラクタでメタページ (page 0) を初期化: `firstFree = -1`, `pageCount = 1`。
PagedFile.InitMetaPage と同じバイナリレイアウト。

### before-image と abort の整合

`PinForWrite` で `WalPageContext.CaptureBeforeImage` を呼ぶことで、
`AbortUndoHandler` がトランザクション中断時に before-image を `WritePageForRecovery` で
復元するフローが透過的に動く。NullWriteAheadLog の `BufferPageImage` は no-op だが、
WalPageContext のスレッドローカルな before-image バッファは依然として機能し、
abort 時の in-process undo が正しく動作する。

---

## Phase 3: NullWriteAheadLog

### 新規ファイル
`src/Quiver/Wal/NullWriteAheadLog.cs`

### 設計
I/O を一切行わない `IWriteAheadLog` 実装。

```
internal sealed class NullWriteAheadLog : IWriteAheadLog
    - _currentLsn: long  // Interlocked.Increment で単調増加
    - _bytesWritten: long
```

| メソッド | 実装 |
|---------|------|
| `CurrentLsn` | `_currentLsn` |
| `FlushedLsn` | `_currentLsn` (常に "flushed") |
| `BytesWritten` | `_bytesWritten` |
| `Append` | `_bytesWritten += payload.Length`, `Interlocked.Increment(_currentLsn)` を返す |
| `FlushTo` / `FlushToAsync` | no-op |
| `BufferPageImage` | no-op |
| `EvictCoalescedPageImagesFor` | no-op |
| `WriteCheckpoint*` | 現在の LSN を返す |
| `WriteFileTruncate` | 現在の LSN を返す |
| `Truncate` | no-op |
| `OpenReader` | `NullWalReader` (内部クラス): `TryReadNext` は常に `false` |
| `Dispose` | no-op |

---

## Phase 4: InMemoryGraphStorageBackendFactory + InMemoryGraphStorageBackend

### 新規ファイル
- `src/Quiver/Backend/InMemoryGraphStorageBackendFactory.cs`
- `src/Quiver/Backend/InMemoryGraphStorageBackend.cs`

### InMemoryGraphStorageBackendFactory

`BinaryGraphStorageBackendFactory.Open()` の簡略版。同じテナント ID を使い、
同じストア / 索引 / トランザクションマネージャを組み立てるが、以下を省略:

1. ファイルシステム操作 (ディレクトリ作成、ファイルパス解決)
2. `RecoveryManager.Recover()` (WAL が空なので不要)
3. `RecoveryManager.RecoverLogical()` (同上)
4. チェックポイント配線 (`Checkpointer`, `AdaptiveCheckpointController`)
5. `container.CommittedHighWaterTxId` からの TxId 復元
6. 隣接ブロック復元 (bulk load 後の adjacency は in-memory では永続化されないため)

```csharp
public IGraphStorageBackend Open(string filePath, GraphDatabaseOptions options)
{
    WalPageContext.End(); MvccContext.End();
    var wal = new NullWriteAheadLog();
    var physical = new InMemoryPagedFile();
    var container = new SingleFileContainer(physical);
    container.EnableWalLogging(DataFileKind, wal);
    var pageManager = new PageManager();
    pageManager.Adopt(container.Physical);

    // テナント open (Binary と同じ ID 体系)
    // → VersionedNodeStore, VersionedRelationshipStore, PropertyStore,
    //   token stores, IndexManager, ColumnManager, PersistentVectorStore

    var fileRegistry = new Dictionary<byte, IPagedFile>
        { { DataFileKind, container.Physical } };
    var committedRegistry = new CommittedTxRegistry();
    // ReloadStoreMeta コールバック (Binary と同構造)
    var undoHandler = new AbortUndoHandler(fileRegistry, ReloadStoreMeta, ftUndoApplier);
    var txManager = new TransactionManager(wal, ...);
    // チェックポイント配線なし (WAL がないので不要)
    return new InMemoryGraphStorageBackend(container, pageManager, wal, ...);
}
```

### InMemoryGraphStorageBackend

`IGraphStorageBackendInternal` を実装。`BinaryGraphStorageBackend` と同等だが:

| メソッド | 差分 |
|---------|------|
| `CreateSnapshot` | `throw new NotSupportedException()` |
| `Vacuum` | `VacuumReport` with `Skipped = true` (または簡易 no-op) |
| `DataDirectory` | `string.Empty` (マイグレーション履歴は非対応) |
| `Dispose` | container + WAL + stores を dispose。WAL 削除やファイルロック解放は不要 |

`BeginGraphTransaction`, `Schema`, `Diagnostics`, `Vectors`, `Transactions`, `Access`, `BulkLoad`
は Binary と同じ委譲パターン。

---

## Phase 5: API 変更

### 変更ファイル
- `src/Quiver/Backend/BackendKind.cs`
- `src/Quiver/GraphDatabase.cs`

### BackendKind

```csharp
public enum BackendKind
{
    Binary = 1,
    InMemory = 2,
}
```

### GraphDatabase

`CreateDefaultFactory` に `BackendKind.InMemory` 分岐を追加:
```csharp
BackendKind.InMemory => new InMemoryGraphStorageBackendFactory(),
```

`Open` メソッドに `:memory:` センチネル検出を追加:
```csharp
if (filePath == ":memory:" && options.BackendFactory == null)
    options.Backend = BackendKind.InMemory;
```

コンビニエンスメソッドを追加:
```csharp
public static GraphDatabase CreateInMemory(GraphDatabaseOptions? options = null)
{
    options ??= new GraphDatabaseOptions();
    options.Backend = BackendKind.InMemory;
    return Open(":memory:", options);
}
```

---

## Phase 6: テスト

### 新規ファイル
- `tests/Quiver.Backend.Tests/InMemoryGraphStorageBackendContractTests.cs`

既存の `GraphStorageBackendContractTests` (抽象基底クラス) を継承し、
`CreateFactory()` で `InMemoryGraphStorageBackendFactory` を返す。
全 contract テスト (CRUD, MVCC, rollback, savepoint, index, merge/upsert 等) が
インメモリバックエンドでも通ることを検証する。

`Commit_survives_backend_reopen` 等の永続性テストは override して
「データが失われること」を正しい挙動として検証するか、Skip する。

### Public API テスト (必要に応じて)
`GraphDatabase.CreateInMemory()` でのエンドツーエンドスモークテスト。

---

## 検証手順

1. `dotnet build` — ビルド通過
2. `dotnet test` — 既存テスト全パス (Phase 1 の型拡張で回帰なし)
3. 新規 contract テスト全パス
4. サンプルコードで `GraphDatabase.CreateInMemory()` → ノード作成 → クエリ → Dispose の
   ゴールデンパスを手動確認

## 新規ファイル一覧 (6)

| ファイル | 内容 |
|---------|------|
| `src/Quiver/Storage/InMemoryPagedFile.cs` | RAM ページ格納 |
| `src/Quiver/Wal/NullWriteAheadLog.cs` | no-op WAL |
| `src/Quiver/Backend/InMemoryGraphStorageBackendFactory.cs` | ファクトリ |
| `src/Quiver/Backend/InMemoryGraphStorageBackend.cs` | バックエンド |
| `tests/Quiver.Backend.Tests/InMemoryGraphStorageBackendContractTests.cs` | contract テスト |
| `tests/Quiver.Backend.Tests/InMemoryModeTests.cs` | 専用テスト (任意) |

## 変更ファイル一覧 (4)

| ファイル | 変更内容 |
|---------|---------|
| `src/Quiver/Storage/SingleFile/SingleFileContainer.cs` | `_physical`/`Physical` を `IPagedFile` 化 + `IPagedFile` 受けコンストラクタ追加 |
| `src/Quiver/Storage/SingleFile/TenantPagedFile.cs` | `_physical` を `IPagedFile` 化 |
| `src/Quiver/Backend/BackendKind.cs` | `InMemory = 2` 追加 |
| `src/Quiver/GraphDatabase.cs` | `:memory:` 検出 + `CreateInMemory()` + `BackendKind.InMemory` 分岐 |
