# グラフ移行cookbook

Quiverでは「移行」を一つのdump/restore機能へまとめない。目的に応じて次の境界を選ぶ。

| 目的 | API | ID |
|---|---|---|
| 同じ物理形式へ復旧 | `CreateSnapshot` | 維持する |
| Quiverの物理familyを更新 | `UpgradeStorage` | 登録stepが維持する |
| 別DBへ移行・subgraphを共有 | `GraphJsonExporter` / `GraphJsonImporter` | import先で新規採番する |
| 同じDBのapplication modelを変更 | `IMigration` + `Update` / `Replace*` | property更新/label renameは維持、構造置換は新規採番 |

## 同じ形式へ復旧する

物理バックアップはJSONを経由しない。コピー先primary fileのパスを指定する。

```csharp
using var db = QuiverDatabase.Open(@"C:\data\graph.quiver");
db.CreateSnapshot(@"D:\backup\graph-20260729.quiver");
```

primary、active WAL、全文segment bodyの扱いと復元手順は
[バックアップとリストア](https://github.com/delicioustuna/Quiver/blob/main/docs/operations/02_backup_restore.md)を参照する。

## Quiverの物理形式を更新する

storage upgradeはDBを閉じ、`Open`より前に明示的に実行する。

```csharp
StorageUpgradeResult upgrade = QuiverDatabase.UpgradeStorage(
    @"C:\data\graph.quiver",
    new StorageUpgradeOptions { KeepBackup = true });

if (upgrade.Status is StorageUpgradeStatus.AlreadyCurrent)
    Console.WriteLine("storage family is current");
```

v0.5.0はv0.4.0と同じQUIVER-SW family version 2を使うため、現行DBは書き換えない。
このbuildに実変換stepのない旧familyは`StorageUpgradeNotSupportedException`になる。

## 別DBへ移行する

Graph JSONはUTF-8の通常JSON objectであり、全graphとVertex-induced subgraphを逐次出力できる。

```csharp
using var source = QuiverDatabase.Open(@"C:\data\source.quiver");
await using var output = File.Create(@"D:\exchange\graph.json");
GraphJsonExporter.Export(
    source,
    output,
    GraphSelection.All,
    new GraphJsonExportOptions { WriteIndented = true });
```

複数文書は同じimport operationへ渡す。同じsource database/kind/packed IDは重複排除されるが、
target IDは新規採番される。transactionとstreamのcommit/disposeはcallerが所有する。

```csharp
using var target = QuiverDatabase.Open(@"C:\data\target.quiver");
using var tx = target.BeginWriteTransaction();
using var first = File.OpenRead(@"D:\exchange\part-1.json");
using var second = File.OpenRead(@"D:\exchange\part-2.json");

GraphJsonImportResult imported = GraphJsonImporter.Import(tx, [first, second]);
tx.Commit();
```

既存targetとの意味的同一性を使う場合だけ、labelとSingle property keyのidentity ruleを明示する。
曖昧な候補、型不一致、missing reference、競合する重複定義はerrorになる。

## application modelを変更する

変更の種類ごとに最小のAPIを選ぶ。

- 同じlabel/typeでpropertyだけを変える: Source Generator生成`Update`
- label全体をrenameする: `ISchemaEditor.RenameLabel`
- 一つのEdgeのtype/endpoint、またはNexusのtype/memberを変える: 生成`Replace`
- 個別Vertexのlabelを変え、incident Edge/Nexusも張り替える: 生成`Replace`
- 複数Vertexを同時に置換する: `ReplaceVertices`でbatch rewrite後、生成`Update`

### 一件のSource Generator置換

生成`Replace`は旧entityの全propertyを新entityへコピーし、その後target modelが宣言するpropertyだけを
生成`Update`と同じ規則で上書きする。target modelから消したkeyやrename前のkeyは自動削除されない。

```csharp
VertexGraphRewriteResult rewrite = Customer.Replace(
    tx,
    oldPersonId,
    new Customer { DisplayName = "Alice", LoyaltyLevel = 3 });

VertexId newCustomerId = rewrite.VertexMappings[0].NewId;
tx.RemoveProperty(newCustomerId, "full_name");
tx.RemoveProperty(newCustomerId, "loyalty_text");
```

`full_name`から`DisplayName`へのrenameや文字列から数値への型変換は、callerが旧modelを読み、
新modelを構築するときに決める。Source Generatorは値写像を推測しない。

EdgeとNexusも同じ契約である。

```csharp
EdgeReplacement edge = Authored.Replace(
    tx, oldEdgeId, newAuthorId, articleId,
    new Authored { PublishedYear = 2026 });

NexusReplacement nexus = Fact.Replace(
    tx, oldNexusId,
    new Fact
    {
        Subject = new GraphVertexRef<Person>(newAuthorId),
        Object = new GraphVertexRef<Article>(articleId),
        Confidence = 0.95,
    });
```

`OldId` / `NewId`の対応は、同じtransactionでの参照更新と、commit後の外部参照更新に使う。
新IDはcommit前には暫定値であり、rollback後は利用できない。

### 複数Vertexのbatch移行

複数の旧Vertexが同じEdgeまたはNexusに参加する場合、生成`Replace`を一件ずつ反復してはいけない。
関係が中間IDを経由して複数回再作成されるためである。対象IDと旧modelをmutation前にmaterializeし、
完全な対応表を`ReplaceVertices`へ一度渡す。

```csharp
VertexId[] oldIds = tx.Query.Vertices()
    .HasLabel("LegacyCustomer")
    .AsEnumerable()
    .ToArray();

var oldModels = oldIds.ToDictionary(
    id => id,
    id => LegacyCustomer.Load(tx, id));

VertexGraphRewriteResult rewritten = tx.ReplaceVertices(
    oldIds.Select(id => new VertexRewriteRequest(id, "Customer")).ToArray());

foreach (VertexReplacement mapping in rewritten.VertexMappings)
{
    LegacyCustomer old = oldModels[mapping.OldId];
    if (!int.TryParse(old.LoyaltyText, out int loyaltyLevel))
        throw new InvalidOperationException("loyalty_text must be an integer");

    Customer.Update(tx, mapping.NewId, new Customer
    {
        DisplayName = old.FullName,
        LoyaltyLevel = loyaltyLevel,
    });
    tx.RemoveProperty(mapping.NewId, "full_name");
    tx.RemoveProperty(mapping.NewId, "loyalty_text");
}
```

batch rewriteは全新Vertexを先に作り、完全なVertex対応表を各Edge/Nexusへ一度だけ適用してから
旧Vertexを削除する。結果の`EdgeMappings`と`NexusMappings`も外部参照更新に利用できる。

この処理を`IMigration.ApplyAsync`内で実行すると、対象のmaterialize、構造置換、property変換、
migration historyを一つのapplication migrationとして管理できる。一transactionに収まらない
resumable application migrationはv0.5.0の契約外なので、対象件数とWAL上限を事前に確認する。
