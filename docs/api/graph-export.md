# Graph JSON export

`GraphJsonExporter` は、Quiver の物理バックアップではなく、別DBへの移行、閲覧、共有に使う
論理graphをUTF-8 JSON objectとして出力する。復旧には `QuiverDatabase.CreateSnapshot` が作る
物理snapshotを使用する。

## 全graphの出力

```csharp
using var db = QuiverDatabase.Open("graph.quiver");
using var output = File.Create("graph.json");

GraphJsonExportResult result = GraphJsonExporter.Export(
    db,
    output,
    options: new GraphJsonExportOptions { WriteIndented = true });
```

writerは `vertices`、`edges`、`nexuses` の各配列を逐次出力するため、全entityをヒープへ
保持しない。`Int64`とsource packed IDはJavaScriptの整数精度を失わないよう10進文字列になる。
`Bytes`はBase64、`FloatArray`はnumber配列になり、非有限浮動小数は `"NaN"`、
`"Infinity"`、`"-Infinity"` で表す。

出力はJSON LinesやQuiver独自のchunk containerではなく、単一の通常JSON objectである。
そのためVS Code等のJSON viewerでそのまま開けるが、大容量文書ではviewer側が
文書全体をメモリへ読み込む場合がある。export側の逐次出力はviewer側のメモリ使用量を
保証しない。Excelで扱う場合はPower Query等で `vertices`、`edges`、`nexuses`を
別テーブルとして展開し、`properties`と `members` の入れ子配列を用途に合わせて
flattenする必要がある。表形式での直接確認が主目的なら、必要なサブグラフや
プロパティに絞って出力する。

destinationの書き込み失敗とキャンセルはcallerへそのまま通知される。逐次書き込みのため、
失敗時のdestinationには未完成のJSONが残る可能性がある。ファイルとして公開する場合は、caller側で
一時ファイルへ書き込んで成功後にrenameする。

## graph JSON v1の完全な最小例

次の文書は、2 Vertex、1 Edge、1 Nexus、Single property、Set propertyをすべて含む。
UUIDとpacked IDは例示値であり、exportごとまたはDB内の採番によって変わる。

```json
{
  "format": "quiver-graph",
  "version": 1,
  "source": {
    "databaseId": "11111111-1111-1111-1111-111111111111",
    "snapshot": "22222222-2222-2222-2222-222222222222"
  },
  "schema": {
    "labels": ["Article", "Person"],
    "edgeTypes": ["AUTHORED"],
    "nexusTypes": ["Fact"],
    "roles": ["object", "subject"],
    "propertyKeys": [
      {"name": "confidence", "cardinality": "single"},
      {"name": "name", "cardinality": "single"},
      {"name": "tags", "cardinality": "set"},
      {"name": "year", "cardinality": "single"}
    ]
  },
  "vertices": [
    {
      "id": "17592186044416",
      "label": "Person",
      "properties": [
        {"key": "name", "cardinality": "single", "type": "string", "value": "Alice"},
        {"key": "tags", "cardinality": "set", "type": "string", "value": "author"},
        {"key": "tags", "cardinality": "set", "type": "string", "value": "local"}
      ]
    },
    {
      "id": "17592186044417",
      "label": "Article",
      "properties": []
    }
  ],
  "edges": [
    {
      "id": "17592186044416",
      "type": "AUTHORED",
      "source": "17592186044416",
      "target": "17592186044417",
      "properties": [
        {"key": "year", "cardinality": "single", "type": "int32", "value": 2026}
      ]
    }
  ],
  "nexuses": [
    {
      "id": "17592186044416",
      "type": "Fact",
      "members": [
        {"role": "subject", "vertex": "17592186044416"},
        {"role": "object", "vertex": "17592186044417"}
      ],
      "properties": [
        {"key": "confidence", "cardinality": "single", "type": "double", "value": 0.95}
      ]
    }
  ]
}
```

Vertex / Edge / Nexusは別kindなので、それぞれの配列で同じlocal packed IDを持ちうる。
Setは一つのJSON array値ではなく、同じkeyと`"cardinality": "set"`を持つproperty entryを
値ごとに繰り返して表す。propertyの`cardinality`は`schema.propertyKeys`の宣言と一致しなければならない。

importerはstreaming参照解決のため、`source`と`schema`をentity配列より前に要求し、
`vertices`を`edges`と`nexuses`より前に要求する。上例のtop-level順をそのまま使えばよい。
`format`、`version`を含む7つのtop-level memberと、5つのschema member、3つのentity配列は
空配列の場合も省略できない。未知または重複するmemberはstrict importで拒否される。

## サブグラフ

```csharp
using IReadTransaction tx = db.BeginReadTransaction();
IEnumerable<VertexId> documents = tx.Query.Vertices()
    .HasLabel("Document")
    .AsEnumerable();

var selection = new GraphSelection(vertices: documents);
GraphJsonExporter.Export(
    tx,
    output,
    sourceDatabaseId,
    selection);
```

既定shapeは最終Vertex集合による誘導部分グラフである。両端が集合内にあるEdgeと、
全メンバーが集合内にあるNexusを出力する。Edgeを明示選択した場合は両端Vertex、Nexusを
明示選択した場合は全メンバーVertexを先に集合へ加え、その後に誘導relationを確定する。

## 出所ID

新規DBは安定した `DatabaseInstanceId` を持ち、再openとsnapshot copyで維持する。
v0.5より前のDBにIDが無い場合、read-only exportはDBを書き換えず一時IDを返す。
重複する複数exportを後で結合する用途では、`FallbackDatabaseInstanceId`へcaller管理の
同じIDを渡す。通常の書き込みを開始すると、その旧DBにもIDが一度だけ永続化される。

## 出力の絞り込み

`IncludedPropertyKeys` でキーを選択でき、`IncludeBytes` と `IncludeFloatArrays` で大きな
payloadを除外できる。これらの除外を使ったJSONは元データの完全な表現ではない。
索引artifact、WAL、dead version、MVCC履歴、索引definitionはgraph JSONへ含めない。
