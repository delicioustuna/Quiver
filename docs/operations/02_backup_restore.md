# 02. バックアップとリストア

> **いつ読むか** — 同じ Quiver ストレージ形式へ復旧するためのバックアップを設計するとき。
> 別 DB への移行やサブグラフの受け渡しには Graph JSON、Quiver の物理形式を更新するときは
> 明示的な storage upgrade、アプリケーションモデルを変更するときは `IMigration` を使う。

## DB を構成するファイル

バイナリバックエンドの正本は、`QuiverDatabase.Open` に渡す単一の primary file である。
たとえば `C:\data\graph.quiver` を開いた場合、同じ DB に属する物理ファイルは次の名前になる。

| パス | 役割 | 存在する時期 |
|---|---|---|
| `graph.quiver` | コアレコード、schema、token、隣接情報、scalar/vector index、migration historyを格納するprimary file | 常時 |
| `graph.quiver-wal` | active WAL sidecar | DBを開いている間、または異常終了後。clean closeでは通常削除される |
| `graph.quiver-ftseg\*.qfts` | manifest参照用のimmutable全文segment body。vacuum前の未参照artifactを含みうる | 全文indexを利用している場合 |

旧レイアウトのようなVertex、Edge、property、B+Treeごとのdata/index fileは存在しない。
`SnapshotOptions.IncludeIndexes`は別backendとの互換オプションであり、現行binary backendでは
scalar/vector indexがprimary fileに同居するため実質的にno-opである。

`*.quiver-upgrade` marker、一時target、`*.pre-upgrade-vN.bak`はstorage upgradeの作業物であり、
通常のopen中に使うDB構成ではない。upgrade中のファイルを個別に移動せず、完了または再開によって
markerを解決してからバックアップする。

## A. ライブスナップショット（推奨）

`CreateSnapshot`にはディレクトリではなく、コピー先primary fileのパスを渡す。

```csharp
using var db = QuiverDatabase.Open(@"C:\data\graph.quiver");
db.CreateSnapshot(@"D:\backup\graph-20260729.quiver");
```

binary backendは同じベース名で次の一式を作る。

- `D:\backup\graph-20260729.quiver`
- snapshot時点でWALがあれば`D:\backup\graph-20260729.quiver-wal`
- 全文segmentがあれば`D:\backup\graph-20260729.quiver-ftseg\*.qfts`

内部ではsharp checkpoint後にprimary fileをpage単位でコピーし、WALをflushしてコピーし、
最後にimmutable全文artifact directoryをコピーする。manifestが参照しない余分なartifactは可視にならない。
コピー先を開くとWAL recoveryが実行され、
明示Commitを持つtransactionだけが公開された整合状態へ収束する。

snapshotはDBを閉じずに実行でき、readerは継続できる。現行binary backendではsnapshot全体が
single-writer mutation leaseに参加するため、既存writerの終了を待ち、処理中の新しいwriterは待機する。
大きなDBでは所要時間を測定し、writer待ち時間を許容できる時間帯に実行する。

コピー先には、既存DBや別snapshotの同名sidecarを混在させない。新しいファイル名を世代ごとに割り当て、
完成したsnapshot一式を同じベース名の単位で保持する。

## B. コールドコピー

DBを`Dispose`で閉じ、同じファイルを開く全プロセスが停止した後なら、primary fileとそのsidecar一式を
OSのファイルコピーで複製できる。clean close後は通常、primary fileと全文segment directoryだけが残る。
異常終了後などWALが残っている場合は、`*-wal`も同じ時点の一式としてコピーする。

```powershell
$source = "C:\data\graph.quiver"
$target = "D:\backup\graph-20260729.quiver"

Copy-Item -LiteralPath $source -Destination $target

$sourceWal = "$source-wal"
if (Test-Path -LiteralPath $sourceWal) {
    Copy-Item -LiteralPath $sourceWal -Destination "$target-wal"
}

$sourceFullText = "$source-ftseg"
if (Test-Path -LiteralPath $sourceFullText) {
    Copy-Item -LiteralPath $sourceFullText -Destination "$target-ftseg" -Recurse
}
```

DBを開いたままOSコピーしてはいけない。primary、WAL、全文artifactの時点がずれるためである。
停止できない運用ではAの`CreateSnapshot`を使う。

## リストア

物理snapshotまたはcold copyは、コピー先primary fileを`Open`するだけで復元できる。

```csharp
using var restored = QuiverDatabase.Open(@"D:\restore\graph.quiver");
var report = restored.Diagnostics.CheckConsistency();
if (!report.IsConsistent)
    throw new InvalidOperationException(string.Join(Environment.NewLine, report.Issues));
```

本番パスへ戻すときは次の順序にする。

1. 復元先アプリケーションを停止し、DBが閉じたことを確認する。
2. 現在のprimary、WAL、全文segment directoryを同じベース名の一式として退避する。
3. snapshot一式を復元先へコピーし、primaryとsidecarのベース名を一致させる。
4. primary fileを`Open`し、WAL recoveryを完了させる。
5. `GetStatistics()`と`CheckConsistency()`を確認する。

退避した一式は検証が終わるまで削除しない。バックアップ自体も別物理ストレージへ複製し、
定期的に隔離パスでopenするリストアリハーサルを行う。

## Graph JSONとstorage upgradeはバックアップではない

`GraphJsonExporter`のUTF-8 JSONは、別DBへの移行、人間による確認、サブグラフ共有のための
論理exchange formatである。importではtarget側のVertex、Edge、Nexus IDを新規採番し、
WAL、MVCC history、derived index artifactは復元しない。障害復旧の代わりには使わない。

`QuiverDatabase.UpgradeStorage(path)`は、閉じたDBの物理形式を現行familyへ明示的に移すoffline operationである。
通常のversion移行をJSON経由で行うAPIではなく、`Open`も暗黙upgradeをしない。
v0.5.0のcurrent familyはv0.4.0と同じversion 2なので、現行ファイルには
`StorageUpgradeStatus.AlreadyCurrent`を返し、ファイルを書き換えない。このbuildに登録された実変換stepのない
旧familyは`StorageUpgradeNotSupportedException`で拒否される。

アプリケーションのlabel/property/typed modelを変える場合は`IMigration`と構造置換APIを使う。
用途ごとの最小手順は[グラフ移行cookbook](../api/migration-cookbook.md)を参照する。

## 方式の選び方

| 目的 | 使う機能 |
|---|---|
| 同じQuiver形式へ障害復旧する | `CreateSnapshot`、または停止後のcold copy |
| Quiverの物理familyを更新する | DBを閉じて`UpgradeStorage` |
| 別DBへ移行・subgraph共有・内容確認 | `GraphJsonExporter` / `GraphJsonImporter` |
| 同じDB内でapplication schema/dataを変更する | `IMigration` + `Update` / `Replace*` |

## 運用チェックリスト

- snapshotを毎回新しいベース名へ作成し、primaryとsidecarを一式で世代管理する。
- snapshot失敗時は不完全なtarget一式を採用せず、別名で再実行する。
- RPOに合わせて取得間隔を決め、復元確認後にのみ古い世代を剪定する。
- バックアップをDBとは別の物理ストレージへ複製する。
- 定期的にsnapshotを隔離パスで開き、統計と整合性を確認する。
