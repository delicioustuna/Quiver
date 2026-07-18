# レコード & インデックス

> as-built 仕様（QUIVER-SW family version 2、2026-07-18）

## Slotted ページモデル {#slotted-pages}

Vertex、Edge、Nexus は `VersionedRecordHeap` と `ItemPointerMap` を使う slotted-page レイアウトである。
各 heap record は 24 バイトの version header と store 固有の固定 payload を持つ。
Property version、incidence、primary vector payload は Sequence から固定 slot を直接計算する。

## Vertex ストア {#vertex-store}

`VersionedVertexStore` (`src/Quiver/Stores/VersionedVertexStore.cs`)。

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

`VersionedEdgeStore` (`src/Quiver/Stores/VersionedEdgeStore.cs`) は 45 バイト payload を `VersionedRecordHeap` に格納する。
payload は flags、source、target、type、両端の prev/next、`FirstPropertyRef` で構成する。
xmin/xmax は version header、Generation は `EntityVersionMeta` sidecar に置く。

各Edgeは source と target の両Vertexについて prev/next にリンクし、Vertexのエンドポイントごとに双方向連結リストを形成する。
adjacency、delta、locator が raw Sequence を保持する間は Edge Sequence を再利用しない。

`AdjacencySegmentStore` は linked-list から再構築できる derived view である。
descriptor version 2 の `KindSegment` だけを受理し、payload lane がない場合も `PayloadKind.None` の同じ segment format を使う。
旧 adjacency descriptor を読む fallback は持たない。

## Nexus ストア {#nexus-store}

`VersionedNexusStore` (`src/Quiver/Stores/VersionedNexusStore.cs`)。
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

## Incidence ストア {#incidence-store}

`IncidenceStore` (`src/Quiver/Stores/IncidenceStore.cs`)。
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

`VertexIncidenceHeadStore` (`src/Quiver/Stores/VertexIncidenceHeadStore.cs`)。
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

## Property ストア {#property-store}

`PropertyVersionStore` (`src/Quiver/Stores/PropertyVersionStore.cs`) は owner-bound property version を格納する。
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

`EntityRef` (`src/Quiver/Core/EntityRef.cs`) は、エンティティの同一性を単一の `long` にパックする。

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

`BTreeIndex` (`src/Quiver/Index/BTreeIndex.cs`) はディスク常駐の B+Tree を実装する。

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

スカラ索引は `ScalarIndexDefinition(Name, Target, Kind)` を永続定義の正本とする。
`PropertyTarget` は所有者種別、プロパティキー名、任意のラベルまたは型スコープを明示する。
所有者種別は Vertex、Edge、Nexus を区別し、同じ sequence 値を別種別へ誤解決しない。
各 B+Tree value はエンティティ ID ではなく `PropertyVersionRef` を格納する。
seek と range はプロパティキー、値、所有者種別、所有者世代、所有者の MVCC 可視性、スコープを primary record で再検証する。
stale entry は結果から除外されるため、索引 artifact 自体を可視性の正本にしない。

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

### B+Tree WAL {#btree-journal}

すべての B+Tree ページは同じ `PageImage` WAL 経路を使う。
全文 postings/norms のリーフと構造ページにも専用 journaling mode は設けない。
プロセス内 rollback は transaction-owned write set の before-image を使う。
