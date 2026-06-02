# ARCH-4 (+ ARCH-5a) 実装プラン — 単一ファイル Pager + テナント化 + 共有 buffer pool + WAL 一本化

> 作成: 2026-06-02 / ブランチ: develop / 指示書: `plans/arch4-to-arch8-rearchitecture-phases.md` §2
> 着手承認: 取得済み (ユーザ「本タスクで実施」/ Open API = ファイルパスに変更)

## 0. 再考結論 (スコープ)

- **ARCH-4 と ARCH-5a を統合**して実施する。理由: 完了条件「静止時=単一ファイル」を満たすには
  各ストアの論理ページ空間を物理ページへ写像する層 (= ARCH-5a のテナント機構) が不可避。分割しても
  二度手間になるだけ。
- **ARCH-5b (ID Kind+Gen+Seq) / 5c (property 再設計) / 6 / 7 / 8 は別タスクのまま**。5b は直交する
  ID エンコーディング変更、5c はストア内部再設計、6/7/8 は vector/query/DSL 層で、本タスクに混ぜても
  rework 削減にならずリスクだけ増える。

## 1. アーキテクチャ (option B = 物理ページ WAL)

```
*.quiver  (単一物理ファイル, 8KB page, 既存 PagedFile を物理層として再利用)
  page 0           … PagedFile メタ (FirstFree / PageCount) — 既存のまま
  page 1           … カタログ root (テナント記述子テーブル + チェーン)
  page 2..         … テナントの page-table ページ群 / 各ストアのデータページ (物理空間で混在)
*.quiver-wal       … 単一 WAL サイドカー (運用中のみ。checkpoint でマージ / クリーン終了で削除)
```

- **物理層 = 既存 `PagedFile` を 1 個だけ使う**。Clock buffer pool / MMF / WAL logging / ARIES recovery /
  truncate をそのまま流用。pool 容量 = `BufferPoolSize / 8192`。WAL fileKind は 1 個 (data)。
- **`TenantPagedFile : IPagedFile`** = 各ストアに渡す薄い変換シム。論理ページ空間 (page 0 予約, 1=header,
  2+=records) を物理ページへ写像 (page table) し、pin/alloc/free/unpin を共有 PagedFile に物理 ID で委譲。
  → 既存ストアコードは **無改修** (NodeStore 等は自分が page 0,1,2.. を所有していると思い込んだまま動く)。
- **recovery は純物理**: WAL は物理 page image を 1 ファイルへ redo/undo するだけ。page table もカタログも
  物理ページなので透過的に redo される。論理→物理変換は runtime のみで recovery に漏れない。
- **fileKind/fileRegistry 機構の縮約**: 全ページが 1 物理空間なので data fileKind は 1 つ。索引ごとの
  `.fileKinds` byte 割当は不要化 (索引も物理ページを container から取る)。

## 2. カタログ / page-table フォーマット

- **カタログ root (page 1)**: テナント記述子配列。1 件 = `tenantId(1) flags(1) pageTableHead(8)
  logicalPageCount(8) logicalFreeHead(8)` = 26B。body ~8160B → ~313 件/page (増設は next ポインタで連鎖)。
- **page table ページ**: body[0..8)=next 物理ページ ID, body[8..)= int64 物理 ID の配列 (logical index =
  並び順)。1019 entries/page。論理 N → ページ (N/1019) の slot (N%1019)。
- **in-memory cache**: open 時に各テナントの page table を `long[]`/`List<long>` に読み込み O(1) 変換。
  alloc/free は write-through で page table ページを更新 (= 物理ページ書き込み = WAL logging 対象)。

## 3. テナント ID 割当

`WalFileKind` を拡張し、固定テナントを定義 (現 data fileKind を流用):
```
System=0x00 (カタログ/page-table)  Nodes=1 NodeVer=5 Rels=2 RelVer=6 Props=3 PropVer=7 Blob=4
Labels Tok / RelTypes Tok / PropKeys Tok / Adjacency / AdjacencyIdx … (0x08..0x3F に新設)
索引 … 0x40.. (FileKindCatalog 由来) → カタログのテナント記述子へ移行
```
※ option B では WAL fileKind は物理層で 1 個に統一するため、テナント ID は**カタログ内の論理識別子**
として使う (WAL の fileKind とは分離)。

## 4. 増分 (各ステップ build/test 緑、別コミット)

1. **増分1 (基盤)**: `SingleFileContainer` (PagedFile 1 個 + カタログ root + テナント open) +
   `TenantPagedFile : IPagedFile` (論理↔物理 page table, 委譲)。page レベル単体テスト
   (2 テナント共存 + reopen 永続 + 物理 free 再利用 + 分離検証)。**WAL 未配線でも clean-close 永続で検証**。
2. **増分2 (ストア載せ替え)**: NodeStore/Rel/Prop/Blob/Ver を TenantPagedFile 上で動かす。WAL logging を
   container 経由 (data fileKind 1 個) で配線。crash/recovery を新 container で緑に。
3. **増分3 (非ページサイドカー吸収)**: token (`*.tok`) / 索引カタログ (`.fileKinds`/`.idxmeta`) /
   adjacency epoch を、PagedFile-backed テナント or カタログページへ移行。`*.tok` 等を全廃。
4. **増分4 (索引・adjacency 載せ替え)**: B+Tree 索引 / adjacency block store を TenantPagedFile 上に。
5. **増分5 (WAL 単一サイドカー)**: `WriteAheadLog` を `wal/` segment 群 → `*.quiver-wal` 単一ファイルへ。
   checkpoint truncate / RecoveryManager の reader をファイル単一化に追従。クリーン終了で削除。
6. **増分6 (buffer pool 配線)**: `GraphDatabaseOptions.BufferPoolSize` → container pool 容量。観測テスト。
7. **増分7 (factory + 公開 API)**: `BinaryGraphStorageBackendFactory` を単一 container 生成へ書換。
   `GraphDatabase.Open(directory)` → `Open(filePath="*.quiver")`。テスト群のパス修正。
8. **増分8 (format bump + 仕上げ)**: `FormatVersion` V4→V5。旧 → `FormatVersionMismatchException`。
   crash contract (両 backend) / checkpoint atomicity / WAL replay を新 Pager で緑。PublicApi 再承認。

## 5. 完了条件 (指示書 §2 より)

- `dotnet build Quiver.slnx` 0 errors / 全テスト緑。
- 静止時 `mydb.quiver` 単一ファイル (+ 運用中 `mydb.quiver-wal`)。クリーン終了後は単一ファイルのみ。
- crash recovery / checkpoint atomicity が単一ファイル Pager で緑。
- `BufferPoolSize` がプール容量を制御 (option 値変更が観測できるテスト)。
- `FormatVersion` 進行、旧フォーマットは `FormatVersionMismatchException`。

## 6. リスク / 留意

- 最下層 (ARIES/MVCC/checkpoint/100-iter crash contract) に触れる。option B で recovery/WAL の物理
  セマンティクスを温存し、PagedFile を再利用してリスクを最小化する。
- `PagedFile` は store 単体テストが直接使うため**温存** (テナント変換は別クラス)。
- 一括テキスト変換は perl/sed (PowerShell `Set-Content` は日本語 mojibake 化のため禁止)。
