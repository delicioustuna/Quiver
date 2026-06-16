# 02. バックアップとリストア

> **いつ読むか** — バックアップ戦略を決めるとき、または別マシン/別環境へ DB を複製したいとき。
> Quiver は単一ディレクトリに状態を持つので、バックアップの本質は「整合した状態のディレクトリを
> どう手に入れるか」に尽きる。方法は 3 つ: **(A) ライブスナップショット (推奨)**、**(B) コールドコピー**、
> **(C) 論理エクスポート**。

---

## 前提: DB ディレクトリの中身

1 つの DB はディレクトリ 1 つ。中には概ね以下が入る:

- データファイル (nodes / relationships / properties / tokens の固定長レコードストア)
- 索引ファイル (`.idx` / `.idxmeta` / `.fileKinds`) — B+Tree 索引
- 隣接ブロックストア
- WAL セグメント (`*.wal`)

**重要**: これらは相互参照しているので、**個別ファイルだけをコピーしても整合しない**。
ディレクトリ全体を「ある一貫した時点」で取る必要がある。下の方法 A / B はそれを保証する。

---

## A. ライブスナップショット (推奨) — `CreateSnapshot`

書き込みを止めずに、整合したコピーを別ディレクトリに作る (OP-1)。本番で最も使うべき方法。

```csharp
using var db = GraphDatabase.Open(@"C:\data\graph");

// ライブ中に整合スナップショットを作成。並行 writer はごく短時間しか待たない。
db.CreateSnapshot(@"C:\backup\graph-2026-05-30");
```

内部の流れ (理解しておくと安心):

1. ベストエフォートでシャープチェックポイントを起動
2. データ / 索引ファイルを page-by-page で複製
3. WAL を末尾までフラッシュしてセグメントを複製

並行する writer は **フレームレベルロックの粒度** でしか待たないので、長時間ブロックしない。
target ディレクトリを後で `GraphDatabase.Open` で開くと recovery が走り、スナップショット時点までの
commit 群が redo され、in-flight だった tx は補償レコードで undo される。**結果として target は
「スナップショットを取った瞬間に正常終了した DB」と等価**になる。

### 索引を含めるかどうか

```csharp
db.CreateSnapshot(@"C:\backup\graph", new SnapshotOptions
{
    IncludeIndexes = false   // 既定 true。false なら復元側で CreateIndex で再構築する想定
});
```

- `IncludeIndexes = true` (既定): 索引ファイルもコピー。開いてすぐ使える。
- `IncludeIndexes = false`: バックアップサイズを削るが、復元後に `db.Schema.CreateIndex(...)` で
  索引を張り直す必要がある。索引が巨大で再構築が許容できるときだけ。

### 注意

- target ディレクトリは空であること (または存在しないこと) を推奨。

---

## B. コールドコピー — プロセス停止中のディレクトリ複製

DB を **Dispose で正常に閉じた後** なら、ディレクトリを OS のファイルコピーで丸ごと複製してよい。
バッチ運用やメンテナンスウィンドウがある環境ではこれで十分。

```pwsh
# 1) アプリを止める (db.Dispose() が完了している状態にする)
# 2) ディレクトリ全体をコピー
Copy-Item -Recurse "C:\data\graph" "C:\backup\graph-cold-2026-05-30"
```

- **DB を開いたままコピーしてはいけない**。書き込み途中のページと WAL が不整合になりうる。
  開いたまま取りたいなら必ず方法 A (`CreateSnapshot`)。
- 正常に閉じた DB ならコピー後の WAL は空に近く、復元側の recovery も軽い。

---

## C. 論理エクスポート (移行・スキーマ変更を伴う場合)

ファイル形式に依存しない移植が必要なとき (例: メジャーバージョン跨ぎの format 変更、
別ストアへの移行) は、読み取りトランザクションで全ノード/エッジを走査して自前のフォーマット
(JSON Lines など) に書き出す。復元は `BeginStreamingBulkLoad` で再投入する。

- 利点: format 非依存、人間が読める、部分抽出ができる。
- 欠点: 完全性は自前の走査コードの正しさ次第。物理バックアップ (A/B) より遅く大きい。

通常運用のバックアップは **A を第一選択**にし、C は移行イベント用と割り切るのがよい。

---

## リストア (復元)

物理バックアップ (A または B) からの復元は **「バックアップディレクトリを開く」だけ**。

```csharp
// バックアップを本番パスへ配置してから開く
using var db = GraphDatabase.Open(@"C:\data\graph");   // recovery が自動で走る
```

別マシンへの復元手順:

1. アプリ (復元先) を停止
2. 既存 DB ディレクトリを退避 (`graph` → `graph.broken` などにリネーム。**いきなり消さない**)
3. バックアップディレクトリを復元先パスへコピー
4. アプリを起動 → 初回 `Open` で recovery が走り、整合状態で立ち上がる
5. `db.Diagnostics.GetStatistics()` で件数を確認、必要なら索引整合チェック
   ([04_recovery_troubleshoot.md](04_recovery_troubleshoot.md) の `CheckIndexConsistency`)

> バックアップの世代管理・スケジューリングは Quiver の責務外。`CreateSnapshot` を
> 定期ジョブ (Windows タスクスケジューラ / cron / ホスト側の `BackgroundService`) から呼び、
> 出力ディレクトリを日付付きで保持し、古い世代を削除する運用を組む。

### 定期スナップショットを `BackgroundService` で回す

Generic Host / ASP.NET Core に組み込む場合、`BackgroundService` から `CreateSnapshot` を周期実行し、
世代を日付付きで保持・剪定するのが簡潔。

```csharp
public sealed class SnapshotBackupService(
    GraphDatabase db, ILogger<SnapshotBackupService> log) : BackgroundService
{
    private const int RetainGenerations = 7;
    private static readonly string BackupRoot = @"D:\backup\graph";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 起動直後ではなく次の「区切り」まで待ってから毎日 1 回など、運用ポリシーに合わせる
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var target = Path.Combine(BackupRoot, $"graph-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
                db.CreateSnapshot(target);             // 書き込みを止めずに整合コピー
                log.LogInformation("snapshot 完了: {Target}", target);
                PruneOldGenerations();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "snapshot 失敗 — 次周期で再試行");
            }
        }
    }

    // 新しい順に RetainGenerations 件だけ残し、古い世代ディレクトリを削除する
    private static void PruneOldGenerations()
    {
        if (!Directory.Exists(BackupRoot)) return;
        var dirs = Directory.GetDirectories(BackupRoot, "graph-*")
                            .OrderByDescending(d => d)   // 名前が日時順なので文字列降順で新しい順
                            .Skip(RetainGenerations);
        foreach (var d in dirs)
            Directory.Delete(d, recursive: true);
    }
}
```

```csharp
builder.Services.AddHostedService<SnapshotBackupService>();
```

ポイント:

- スナップショット失敗は **次周期でリトライ** する設計にし、1 回の失敗でジョブが止まらないようにする。
- 世代剪定は「新しい順に N 件残す」。世代数は RPO (どこまでのデータ損失を許容するか) と
  ストレージ容量から決める。
- バックアップ先は **DB と別物理ストレージ / 別マシン** に置く。同じディスクに置くと
  ディスク障害でバックアップごと失う。

### PowerShell から外部ジョブとして取る

アプリ内ジョブにしたくない場合、コールドコピー (方法 B) を Windows タスクスケジューラから回す:

```pwsh
# backup.ps1 — メンテナンスウィンドウ中にアプリを止めてからコピー
$src = "C:\data\graph"
$dst = "D:\backup\graph-{0:yyyyMMdd-HHmmss}" -f (Get-Date)
Stop-Service MyAppService          # db.Dispose() が完了する停止手順
Copy-Item -Recurse $src $dst
Start-Service MyAppService
# 7 世代より古いものを削除
Get-ChildItem "D:\backup" -Directory -Filter "graph-*" |
    Sort-Object Name -Descending | Select-Object -Skip 7 |
    Remove-Item -Recurse -Force
```

ライブ運用 (停止不可) では方法 A の `CreateSnapshot` を使い、停止できる環境では上記コールドコピーで良い。

---

## バックアップ方式の選び方

| 状況 | 推奨 |
|---|---|
| 24/7 稼働、停止できない | **A: `CreateSnapshot`** |
| 夜間メンテナンスウィンドウがある | A または B (B はシンプル) |
| format を跨ぐ移行 / 別ストアへ移植 | C: 論理エクスポート |

---

## 復元の検証 (リハーサル)

バックアップは **復元できて初めて意味がある**。定期的に:

1. 最新スナップショットを隔離ディレクトリへ復元
2. `GraphDatabase.Open` で起動できることを確認
3. `GetStatistics()` の件数が想定どおりか
4. `db.Diagnostics.CheckIndexConsistency()` で orphan が出ないか

を回す「リストアリハーサル」を運用に組み込むこと。
