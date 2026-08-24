# Graph JSON import

`GraphJsonImporter` は、一つ以上の graph JSON v1 文書を caller 所有の
`IWriteTransaction` へ読み込み、新しい ID を持つ graph として結合する。
物理 snapshot の復元やストレージ形式の移行には使用しない。

## 単一文書

```csharp
using var db = YatagarasuDatabase.Open("target.yata");
using IWriteTransaction tx = db.BeginWriteTransaction();
using var input = File.OpenRead("graph.json");

GraphJsonImportResult result = GraphJsonImporter.Import(tx, input);
tx.Commit();
```

importer は transaction を commit / rollback せず、入力 stream も破棄しない。
構文、schema、参照、重複定義の検査に失敗した場合も transaction は caller が rollback できる。

## 複数文書の union

```csharp
using IWriteTransaction tx = db.BeginWriteTransaction();
using var first = File.OpenRead("part-1.json");
using var second = File.OpenRead("part-2.json");

GraphJsonImportResult result = GraphJsonImporter.Import(
    tx,
    new Stream[] { first, second });
tx.Commit();
```

同じ `(source.databaseId, entity kind, generationを含むsource packed ID)` は、
一つのtarget entityへ対応する。重複文書ではproperty順、Set値順、Nexus member順を
同一性に含めず正規化するが、label、type、端点、member、propertyの型・多重度・値が
異なる定義は `GraphJsonImportException` になる。異なる`databaseId`の同じlocal IDは別entityである。
source packed IDは文書内の参照用opaque値であり、targetの永続entity IDではない。
generation部分が0のsource IDもdocument-local referenceとして受理するが、import後にその値を維持しない。

各文書は参照閉包を持たなければならない。Edgeの両端とNexusの全memberは、同じ文書の
`vertices`に含める。missing reference、未知のmember / type / 予約文字列、duplicate JSON member、
途中切断、trailing dataを読み飛ばさない。

## mappingの取得

```csharp
var mappings = new List<GraphJsonImportMapping>();
var options = new GraphJsonImportOptions
{
    MappingSink = mappings.Add,
};

GraphJsonImporter.Import(tx, streams, options);
```

callbackは新しいsource entityごとに一度だけ呼ばれる。`ProvisionalTargetPackedId`は
transactionのcommit前に払い出された暫定IDであり、rollback後は使用できない。
全mappingを保持するかどうかはcallerが選ぶ。

## 既存graphとのsemantic merge

既定は異なるsource databaseの同一性を推測せずappendする。業務上のidentityが明示できる場合だけ、
labelと一つのSingle property keyを指定する。

```csharp
var options = new GraphJsonImportOptions
{
    SemanticMerge = new GraphJsonSemanticMergeOptions
    {
        VertexIdentityRules = [new("Person", "externalId")],
        PropertyConflictPolicy = GraphJsonPropertyConflictPolicy.Error,
    },
};

GraphJsonImportResult result = GraphJsonImporter.Import(tx, streams, options);
Console.WriteLine($"semantic matches: {result.SemanticMatchCount}");
```

各Vertexは候補を0件、1件、複数件に分け、複数候補をerrorにする。identity propertyがmissing、
Set cardinality、または同じrule内で物理型不一致の場合もerrorであり、identity値は上書きしない。
ruleがないlabelはappendする。Vertex解決を全入力文書について先に行い、identity以外のpropertyは
後でsource database ID、entity kind、source packed IDの決定順に適用するため、文書順で照合結果を変えない。

Edgeはmapped source / targetとtype、Nexusはtypeと順序を正規化したrole付きmember集合で照合する。
同じ構造のtarget relationが複数ある場合も任意の一件を選ばずerrorにする。異なるsource identityが
同じtarget relationへ対応する場合のproperty適用も同じ決定順に遅延する。

property競合方針は、異なる値を拒否する`Error`、既存値を残す`KeepTarget`、source値へ置換する
`OverwriteTarget`のいずれかを明示する。Set propertyはkey全体を比較・保持・置換する。
`VertexCount` / `EdgeCount` / `NexusCount`は新規作成数、`SemanticMatchCount`は意味的照合で既存または
同operation内の先行targetを再利用した数、
`DuplicateEntityCount`は同じsource identityの重複排除数である。

semantic mergeは一意制約を仮定しない。永続property indexの一候補だけを信用せず、operation初回に
targetのVertex、Edge、Nexusを各一度走査し、typed identity、端点とtype、正規化member集合をkeyとする
一時候補indexを構築する。新規作成entityも同indexへ加え、複数候補を確実に検出する。一時indexの
追加メモリはtarget entity数とidentity / Nexus member payloadに比例する。さらに文書順に依存しない
property競合処理のため、`Bytes`と`FloatArray`を含む全source entityのproperty payloadをEOF後の
適用までheapに保持し、追加メモリはproperty payload総量に比例する。大規模semantic mergeでは
入力JSONサイズだけでなく、デコード後のpayload総量、target entity数、Nexus member総数を見積もる。

## streamingと上限

readerは`JsonDocument`へ文書全体を展開せず、UTF-8 tokenと一entityずつを逐次処理する。
通常のexporterが書く順序と同じく、`source`と`schema`をentity配列より前に、
`vertices`を`edges` / `nexuses`より前に置く。object内のentity field順、property順、
Set値順、Nexus member順は任意である。

source identity mapと定義fingerprintはunion operation中保持する。またv0.5.0のimportは
一つのwrite transactionに収まることを前提とし、resumable importや部分commitは提供しない。
非常に大きい入力は、JSON parserではなくtransaction sizeの上限に先に達する場合がある。
semantic mergeでは前節の全property payloadも保持するため、通常のappend / unionが持つ
「一entity payloadずつ」というメモリ特性は適用されない。v0.5.0はtemp spoolを提供しない。
`cancellationToken`はJSON token読取、semantic target indexのentity / member走査、EOF後の
deferred entity / property / value適用境界で観測する。キャンセル時もtransactionはcaller所有のままであり、
途中まで作成・適用された変更はcallerがrollbackする。
