# レコード & インデックス

> as-built 仕様（QUIVER-SW family version 2、2026-08-03）

## Slotted ページモデル {#slotted-pages}

Vertex、Edge、Nexus は `VersionedRecordHeap` と `ItemPointerMap` を使う slotted-page レイアウトである。
各 heap record は 24 バイトの version header と store 固有の固定 payload を持つ。
Property version、incidence、primary vector payload は Sequence から固定 slot を直接計算する。

## Vertex ストア {#vertex-store}

`VersionedVertexStore` (`src/Yatagarasu/Stores/VersionedVertexStore.cs`)。

**Vertex payload**（15 バイト、version header の後ろ）:

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Flags (in-use) |
| 1 | 6 | FirstEdgeId（隣接リスト先頭のEdge） |
| 7 | 6 | FirstPropertyRef（owner-bound property version chain の先頭 Sequence） |
| 13 | 2 | LabelId |

- xmin/xmax は version header に格納する。
- Generation は `EntityVersionMeta` sidecar に格納する。
- vacuum が reader horizon を越えた record を回収した後、Sequence を再利用すると Generation が増える。

## Edge ストア {#rel-store}

`VersionedEdgeStore` (`src/Yatagarasu/Stores/VersionedEdgeStore.cs`) は 45 バイト payload を `VersionedRecordHeap` に格納する。
payload は flags、source、target、type、両端の prev/next、`FirstPropertyRef` で構成する。
xmin/xmax は version header、Generation は `EntityVersionMeta` sidecar に置く。

各Edgeは source と target の両Vertexについて prev/next にリンクし、Vertexのエンドポイントごとに双方向連結リストを形成する。
adjacency、delta、locator が raw Sequence を保持するため、通常の dead version 回収だけでは Edge Sequence を再利用しない。

`IReadTransaction.TryGetEdge` は現在のsnapshotで可視なEdgeについて、generationを含む
`EdgeId`、両端の`VertexId`、型名を`EdgeInfo`として返す。missingまたはstale generationでは
`false`を返す。`EnumerateProperties(EdgeId)`はVertex / Nexusと同じowner-bound property cursorを返す。

`RelationshipReuseCoordinator` は reader horizon の通過後に base rebuild、delta/epoch reset、locator rebuild、derived durable checkpoint を順に完了し、その後だけ Sequence を free list へ返す。
各 phase と対象 Sequence は primary catalog に永続化する。
release 前の crash は再利用しない safe leak となり、reopen 後に未完了 phase から再開する。
free list へ返した Sequence の次回割り当てでは Generation が増える。

`AdjacencySegmentStore` は linked-list から再構築できる derived view である。
descriptor version 2 の `KindSegment` だけを受理し、payload lane がない場合も `PayloadKind.None` の同じ segment format を使う。
旧 adjacency descriptor を読む fallback は持たない。

## Nexus ストア {#nexus-store}

`VersionedNexusStore` (`src/Yatagarasu/Stores/VersionedNexusStore.cs`)。
**Nexus**は 1 つの型とロール付きメンバー集合（アリティ 2 以上）を持つ第一級エンティティであり、
Edgeとは別の `EntityKind` として格納される。
header レコードが MVCC 可視性の正本になる。

**Nexus header レコード**（固定領域 15 バイト、version ヘッダ 24 バイトの後ろ）:

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Flags (in-use) |
| 1 | 2 | TypeId（インターンされたNexus型） |
| 3 | 6 | FirstIncidenceId（メンバーチェーンの先頭） |
| 9 | 6 | FirstPropertyRef（owner-bound property version chain の先頭 Sequence） |

- `VersionedRecordHeap` + `ItemPointerMap` 上の固定 payload であり、inline property 領域は持たない
- xmin/xmax は heap の version header、Generation は `EntityVersionMeta` sidecar に置く
- メンバー集合は作成時に確定し、以後変更されない。変更は削除 + 再作成で表現する
- 同じロールとVertexの組は 1 つのNexus内で重複できない。
  同じVertexが別ロールで参加すること、同じロールに複数Vertexが参加することは許される
- Nexus型名とロール名は、ラベルと同様それぞれ独立したトークンストアで
  16 bit ID（`NexusTypeId` / internal な RoleId）にインターンされる

## 構造置換 {#structural-replacement}

Edgeの端点と型、およびNexusの型とメンバー集合はin-place更新しない。
`IWriteTransaction.ReplaceEdge` / `ReplaceNexus`は同じwrite transaction内で次を行う。

1. 旧entityの全propertyを物理型と`Single` / `Set` cardinality付きでキャプチャする。
2. 指定された新構造を持つentityを新しいIDで作成する。
3. `Bool`、`Int32`、`Int64`、`Double`、`String`、`Bytes`、`FloatArray`を新ownerへコピーする。
4. 旧entityを論理削除し、old/new IDを`EdgeReplacement` / `NexusReplacement`で返す。

作成時の端点・arity・重複member・member可視性検証を再利用する。失敗は呼び出し側transactionの
rollback契約に従う。置換前に開始したreaderは旧entityを、置換commit後に開始したreaderは新entityを
参照し、同じIDの構造がsnapshot間で変化したようには見えない。

Vertexのラベルを個別に変更してincident relationも張り替える場合は、`ReplaceVertex`または
`ReplaceVertices`を使う。batchは全旧Vertexとpropertyを検証・materializeし、全新Vertexを先に作成する。
その後、完全なold/new対応表をEdge端点とNexus memberへ一度適用し、各関係を一度だけ置換してから
旧Vertexを削除する。self-loopの両端と、同一Nexus内に同じVertexが持つ複数roleもすべて張り替える。
結果の`VertexMappings`、`EdgeMappings`、`NexusMappings`は新規IDへの参照更新に使える。
missing / stale ID、batch内の重複要求、置換後に重複する`(role, VertexId)`は拒否し、失敗時の変更は
呼び出し側write transactionのrollbackに従う。

Source Generatorの生成`Update`はproperty-onlyのまま維持する。生成`Replace`はEdge / Nexusでは
`ReplaceEdge` / `ReplaceNexus`、Vertexではincident relationを含む`ReplaceVertex`へ委譲した後、
target modelが宣言するpropertyを生成`Update`と同じ規則で上書きする。構造置換が先に旧entityの
全propertyをコピーするため、target modelから削除したkeyやrename前のkeyも自動では消えない。
利用者は型変換と値写像を行い、不要な旧keyを新IDから`RemoveProperty`で削除する。
複数Vertexの移行は単体の生成`Replace`を反復せず、`ReplaceVertices`へ全対象を渡してから
各new IDへ生成`Update`を適用する。

## Incidence ストア {#incidence-store}

`IncidenceStore` (`src/Yatagarasu/Stores/IncidenceStore.cs`)。
**incidence** は「どのVertexが、どのロールで、どのNexusに属すか」を表す vertex-nexus 対
（内部表現）であり、独立した MVCC エンティティではない。
可視性は参照先の nexus header に従う。

**Incidence slot**（27 バイト固定、1 ページあたり 302 slot）:

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Flags (in-use / free) |
| 1 | 6 | NexusId (Sequence) |
| 7 | 6 | VertexId (Sequence) |
| 13 | 2 | RoleId（インターンされたロール） |
| 15 | 6 | NextInVertex（同一Vertexの incidence チェーン） |
| 21 | 6 | NextInNexus（同一Nexusのメンバーチェーン） |

- **fixed-slot 直接アドレス方式**: `sequence → (page = seq / 302 + 2, offset = seq % 302 × 27)` で
  slot を直引きし、version チェーンも間接ポインタ層（map / slot directory）も持たない。
  ヘッダページ (page 1) に高水位と free chain 先頭を置く
- incidence は 2 本のチェーンを貫通する。Vertex側は `NextInVertex`、Nexus側は
  `NextInNexus` を辿る。Vertex側チェーンの走査は、参照先 header が不可視の incidence を
  skip して後続を継続する
- 逆方向リンク（PrevInVertex）は持たない。vacuum の unlink は、dead incidence をVertex別に
  グループ化し、影響Vertexのチェーンを head から 1 回だけ走査する sweep
  （合計 O(影響チェーン長)）で行う
- free chain は空 slot の `NextInVertex` 領域を転用する。slot を free に戻せるのは
  「全 live チェーンから unlink 済み、かつアクティブトランザクションなし」のときに限る

incidence 自身が xmin/xmax を持たないのは、可視性判定に header だけを使うためである。
undo（abort / savepoint）とクラッシュリカバリは物理 page image で行われレイアウトに依存しない。

## Vertex incidence head {#vertex-incidence-head}

`VertexIncidenceHeadStore` (`src/Yatagarasu/Stores/VertexIncidenceHeadStore.cs`)。
vertex sequence を添字に、そのVertexのVertex側チェーン先頭 incidence（6 バイト Int48）を保持する
固定長 sidecar。head をVertexレコード本体に持たせないのは、Nexusを使わない
ワークロードのVertex読み取り帯域を増やさないためである（インライン案との実測比較で採用）。

## Nexus の vacuum {#nexus-vacuum}

`VacuumTarget.Nexuses` は visibility horizon を越えた dead nexus を
プロパティ → incidence → header の順に回収する。

- incidence の unlink はVertex別 sweep（上記）で行い、vertex incidence head が回収対象を
  指す場合は次の生存 incidence へ進める
- header slot は free list へ戻し、sequence 再利用時に generation を進める。
  古い ID による参照（ベクトル binding を含む）は世代照合で弾く
- 回収件数は `VacuumReport.ReclaimedNexuses` / `ReclaimedIncidences` で報告される

## Horizon-aware vacuum {#horizon-vacuum}

`Vacuum()` は database instance の writer lease を取得するが、active reader の終了は待たない。
`SnapshotRegistry` が返す最古の visibility horizon より前だけを回収するため、long reader は開始時の property、payload、entity、manifest を読み続けられる。

derived index entry を先に退役させ、primary property version とその version だけが参照する payload を同じ maintenance commit で回収する。
その後に incidence、Edge、Vertex、Nexus slot を回収する。
この順序により、到達可能な property version が解放済み payload を指す状態を作らない。

`VacuumTarget.Indexes` は horizon を越えた vector manifest と全文 manifest を退役させる。
どの committed manifest からも参照されない全文 artifact file は物理削除する。
回収結果は `RetiredVectorManifests`、`RetiredFullTextManifests`、`ReclaimedFullTextArtifacts` で報告する。

## Property ストア {#property-store}

`PropertyVersionStore` (`src/Yatagarasu/Stores/PropertyVersionStore.cs`) は owner-bound property version を格納する。
Property は独立 entity ではなく、public `PropertyId` を持たない。
論理アドレスは `PropertyAddress(Owner: EntityRef, Key: PropertyKeyId)` である。

**Property version レコード**（84 バイト）:

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Flags（in-use、spillover、vector payload） |
| 1 | 1 | Cardinality |
| 2 | 1 | ValueType |
| 3 | 1 | 予約 |
| 4 | 8 | Owner（kind、Generation、Sequence を含む packed `EntityRef`） |
| 12 | 4 | KeyId |
| 16 | 6 | PreviousVersion（同じ address の直前 version） |
| 22 | 6 | NextOwned（owner chain の次 version） |
| 28 | 4 | ValueLength |
| 32 | 24 | InlineValue または payload ref |
| 56 | 4 | CRC32C checksum |
| 60 | 8 | xmin |
| 68 | 8 | xmax |
| 76 | 8 | Generation |

- 1 ページあたり 97 レコードである
- 最大 24 バイトの string と bytes は record 内に格納し、それを超える値は checksum 付き immutable blob を参照する
- `FloatArray` は `VectorPayloadRef` を格納し、property record へ配列を inline 化しない
- xmin/xmax と Generation は property version record に格納し、property 専用の `EntityVersionMeta` sidecar は持たない
- owner が一致しない chain read は `CorruptionException`、Generation が一致しない ref は missing として扱う
- vacuum は dead version を回収し、HWM 縮小後に有効範囲だけで free-list を再構築する
- free slot の Generation を保持するため、property page の物理 truncate は payload GC が reader horizon を扱う段階まで遅延する

## EntityRef (ID パッキング) {#entity-ref}

`EntityRef` (`src/Yatagarasu/Core/EntityRef.cs`) は、エンティティの同一性を単一の `long` にパックする。

```
ビットレイアウト (MSB → LSB):
[63..60]  EntityKind   (4 bits; Vertex=1, Edge=2, Reserved=3, Nexus=4)
[59..44]  Generation   (16 bits; 0..65535)
[43..0]   Sequence     (44 bits; slot-local ID; 0..17.6 兆)
```

| 定数 | 値 |
|---|---|
| `SequenceMask` | `0xFFF_FFFF_FFFF` (44 bits) |
| `MaxGeneration` | 65,535 |

- `PackLocal(seq, gen)` = `(gen << 44) | (seq & SequenceMask)`
- Generation は vacuum 後の slot 再利用時にインクリメントされ、ABA エイリアシングを防ぐ
- Generation オーバーフロー (> 65535): その slot は恒久的に退役する
- `VertexId`、`EdgeId`、`NexusId`、`EntityRef` の等価性とハッシュは Generation を含む。
- public な `EntityRef` は typed `From` または検証済み `Create` でだけ生成する。
  `Vertex`、`Edge`、`Nexus` 以外の kind と、範囲外の local value は拒否する。
  `default(EntityRef)` だけが invalid sentinel である。
- internal `EntityId` の packed 値 `0` は canonical Invalid を表す。
  予約値、未知 kind、範囲外 local value は `ToPacked` と strict decoder で拒否する。

## マルチバリュープロパティ {#multi-value}

`PropertyCardinality` (`Single=0`, `Set=1`) を property version に永続化する。
同一 owner と key に複数の値を持つ Set は、同じ owner chain に複数の可視 version を保持する。

- **`AddPropertyValue`**: 既存エントリに xmax スタンプせずに新エントリを prepend (重複時はスキップ)
- **`RemovePropertyValue`**: 同一 key+value の visible エントリに xmax スタンプ
- **`GetPropertyValues`**: 同一 keyId の全 visible エントリを `PropertyValuesEnumerator` で列挙
- **B+Tree インデックス**: 要素単位。`Has("tags", "sensor")` は既存 B+Tree ルックアップで包含クエリとして動作
- **Cardinality 制約**: `SetProperty` を Set キーに呼ぶと例外、`AddPropertyValue` を Single キーに呼ぶと例外
- **SourceGenerator**: `List<T>` / `IList<T>` / `IReadOnlyList<T>` を検出し Set cardinality で CRUD を emit
- **型付きトラバーサル**: `Has(s => s.Tags, "outdoor")` で包含フィルタ、`Values(s => s.Tags)` でVertexごとの値リスト取得

## B+Tree インデックス {#btree}

`BTreeIndex` (`src/Yatagarasu/Index/BTreeIndex.cs`) はディスク常駐の B+Tree を実装する。

### リーフページレイアウト {#btree-leaf}

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 4 | EntryCount (int32) |
| 4 | 8 | NextLeaf (int64, -1 = なし) |
| 12 | 8 | PrevLeaf (int64) |
| 20+ | 可変 | Entries: KeyLen(int16) + Key(可変) + Value(int64) |

### 内部ページレイアウト {#btree-internal}

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 4 | KeyCount (int32) |
| 4 | 8 | FirstChildPageId (int64) |
| 12+ | 可変 | Keys: KeyLen(int16) + Key(可変) + ChildPageId(int64) |

### キーコーデック {#key-codec}

`KeyCodec` は、型付きプロパティ値を B+Tree の順序付けのために比較可能なバイト列へエンコードする:

- **StringEquality**: UTF-8 バイト列
- **Int64Equality**: 正しいソート順のために符号ビットを反転した big-endian int64

### インデックス種別 {#index-kinds}

| 種別 | キー型 | ルックアップ |
|---|---|---|
| `StringEquality` | string | SeekIndex による完全一致 |
| `StringRange` | string | RangeIndex による範囲スキャン |
| `Int32Equality` | int32 | SeekIndex による完全一致 |
| `Int64Equality` | int64 | SeekIndex による完全一致 |
| `DoubleEquality` | double | SeekIndex による完全一致 |

### 統一スカラ索引定義 {#scalar-index-definition}

スカラ索引は `ScalarIndexDefinition(Name, Target, Kind, Unique)` を永続定義の正本とする。
`PropertyTarget` は所有者種別、プロパティキー名、任意のラベルまたは型スコープを明示する。
所有者種別は Vertex、Edge、Nexus を区別し、同じ sequence 値を別種別へ誤解決しない。
各 B+Tree value はエンティティ ID ではなく `PropertyVersionRef` を格納する。
seek と range はプロパティキー、値、所有者種別、所有者世代、所有者の MVCC 可視性、スコープを primary record で再検証する。
stale entry は結果から除外されるため、索引 artifact 自体を可視性の正本にしない。

`Unique = true` は label scope を持つ Vertex の Single cardinality string property と
`StringEquality` の組合せに限る。property 欠落は制約対象外であり、同じ owner への同値設定は冪等である。
別 owner の可視 property が同じ UTF-8 文字列を持つ場合は、property mutation より前に
`UniqueConstraintViolationException` を送出する。この例外は索引名、label scope、property key を公開するが、
property 値はメッセージにも属性にも含めない。owner delete または property remove 後は、同じ transaction 内でも
その値を再利用でき、savepoint rollback と transaction abort は制約上の可視性も巻き戻す。

一意性は B+Tree artifact ではなく primary MVCC state の制約である。`Ready` では B+Tree candidate を primary record で
再検証し、`Building` / `RebuildRequired` と artifact 不在時は primary scan で同じ契約を強制する。
既存データへの定義追加は publish 前に全対象を検査し、重複または非 string 値があれば definition を残さない。
bulk load / streaming bulk load は書込み開始前に入力全体を検査する。reopen、replay、crash recovery 後も
永続 definition に従い、未コミット値が制約を占有することはない。

index catalog format 6 は scalar definition に `Unique` flag を保存する。format 5 は後方互換入力として読み、
全 scalar definition を `Unique = false` と解釈する。新規永続化は format 6 を使用し、未知の過去版・将来版は拒否する。
B+Tree page format、WAL record、writer lock protocol は変更しない。

### ライフサイクルと再構築 {#scalar-index-lifecycle}

永続状態は `Building`、`Ready`、`RebuildRequired` の三種類である。
作成時は定義を `Building` として記録し、既存の可視プロパティを backfill してから `Ready` に遷移する。
同じ名前と同じ定義の再作成は冪等であり、同じ名前で異なる定義を要求した場合は拒否する。
通常 mutation、Set cardinality、bulk load、streaming bulk load は同じ定義集合を更新対象とする。
bulk load 後は定義を `RebuildRequired` にし、snapshot reader が primary property を走査して immutable な候補 artifact を構築する。
open 時に `Building`、`RebuildRequired`、欠損または不正な B+Tree header を検出した場合も、同じ background rebuild を開始する。
primary scan、key decode、sort の間は writer lease を保持しない。
publish transaction は source committed high-water と index definition を再検証し、一致した artifact だけを `Ready` にする。
再検証に失敗した artifact は破棄し、新しい snapshot から再試行する。
source snapshot より古い reader が残る間は publish を延期し、旧 reader が参照する artifact を reset しない。
定義が `Ready` でない間の seek と range は、同じ読み取りスナップショットの primary scan へフォールバックする。
フォールバックも同じ値比較と順序規則を使うため、artifact の状態によって結果集合を変えない。

### ベクトル索引定義と segment lifecycle {#vector-index-lifecycle}

`VectorIndexDefinition` は `PropertyTarget`、dimensions、metric、element type、HNSW 構築パラメタ、`VectorSegmentPolicy` を永続定義とする。
vector property mutation は一致する definition ごとに commit-local flat delta segment を公開する。
merge worker は read snapshot から immutable HNSW artifact を構築し、source manifest generation と definition が一致する場合だけ短い write transaction で新 manifest を公開する。
old reader は旧 manifest を使い続け、新 reader だけが新 manifest を参照する。
candidate は owner generation、property visibility、target、payload checksum を primary store で再検証する。
derived state が不足する場合は同じ snapshot の primary property scan へフォールバックする。

### B+Tree WAL {#btree-journal}

すべての scalar B+Tree ページは同じ `PageImage` WAL 経路を使う。
全文のterm dataとdocument lengthは immutable derived segment に置き、B+Tree page と専用 journaling modeを持たない。

## 全文 definition と segment {#fulltext-segment}

`FullTextIndexDefinition` は `PropertyTarget`、tokenizer/filter pipeline、BM25 parameter、segment policy を統一 catalog に保存する。
永続 filter は構成値まで definition の同一性に含む。日本語異字体展開は既定で無効であり、
利用者が明示した `JapaneseOrthographicVariantFilter` だけを索引時と検索時の同じ pipeline へ適用する。

全文 artifact は full typed owner identity と `PropertyVersionRef` を保持する immutable delta/merged segment である。

artifact file は entry metadata、term dictionary、sorted postings、document length と checksum を `*.yata-ftseg/` に保持する。
catalog manifest は generation、`xmin/xmax`、source committed high-water、artifact ID/length/checksum、lifecycle state を保持する。

検索は visible manifest を選び、candidate を primary owner と property version に照合してから返す。
プロセス内 rollback は transaction-owned write set の before-image を使う。

## Graph JSON export {#graph-json-export}

`GraphJsonExporter` は一つのread transaction snapshotをUTF-8の単一JSON objectへ出力する。
top-levelは `format: "yatagarasu-graph"`、`version: 1`、`source`、`schema`、`vertices`、
`edges`、`nexuses` で構成し、各entity配列は `Utf8JsonWriter` へ逐次書き込む。

source packed IDと `Int64` propertyは10進文字列、`Bytes`はBase64、`FloatArray`は
JSON配列で表す。`Double`と `FloatArray` の非有限値は予約文字列 `NaN`、`Infinity`、
`-Infinity` として有限numberから区別する。property entryはkey、`Single` / `Set` cardinality、
物理 `PropertyValueType`、valueを保持する。日時系の元CLR型は物理保存時に失われているため
`Int64`として出力する。

`GraphSelection.All` は可視graph全体を表す。部分選択では、明示Edgeの両端と明示Nexusの
全メンバーを最終Vertex集合へ加え、両端が集合内の全Edgeと、全メンバーが集合内の全Nexusを
誘導出力する。選択IDの比較はSequenceだけでなくGenerationを含む。

graph JSONは物理backupではなく外部交換形式である。WAL、索引artifact、索引definition、
dead version、MVCC履歴は含めない。`WriteIndented`、property key選択、Bytes / FloatArray除外は
表現の完全性より閲覧性とサイズを優先する明示optionである。
