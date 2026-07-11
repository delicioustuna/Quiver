# レコード & インデックス

> as-built 仕様 (on-disk FormatVersion V5)
>
> **current (as-built)**: 以下は現在実装されている FormatVersion V5 のレコードとインデックス契約である。
> **target (未実装)**: [Single Writer + Snapshot Readers 抜本再設計](../../plans/single-writer-redesign.md) が将来の設計正本であり、本書の本文はその target を先取りして記述しない。
> **実装済み境界**: 再設計の production code はまだ実装されていない。`redesign-baseline` は着工前の測定を固定するタグであり、再設計の実装完了を表さない。

## Slotted ページモデル {#slotted-pages}

すべてのレコードストアは、8,160 バイトのページボディ (`PagedFile.BodySize`) 内で slotted-page
レイアウトを用いる。レコードはストアごとに固定サイズであり、slot index = ページ内のレコードオフセット。

## Node ストア {#node-store}

`NodeStore` (`src/Quiver/Storage/Records/NodeStore.cs`)。

**Node レコード** (15 バイト):

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Flags (alive, deleted) |
| 1 | 6 | FirstRelId（隣接リスト先頭のリレーションシップ） |
| 7 | 6 | FirstPropId（プロパティチェーンの先頭エントリ） |
| 13 | 2 | LabelId |

- **1 ページあたり 544 レコード** (8160 / 15)
- バージョンメタデータ (xmin/xmax) は別の MVCC サイドカー (`EntityVersionMeta`) に格納
- vacuum フリーリストによる slot 再利用 (`OP-3`)。アクティブ tx 中は論理削除

## Relationship ストア {#rel-store}

リレーションシップは隣接リスト構造で格納される。各リレーションシップレコードは、source と target の
両ノードについて next/prev のリレーションシップにリンクし、ノードのエンドポイントごとに双方向連結リストを形成する。

## Hyperedge ストア {#hyperedge-store}

`VersionedHyperedgeStore` (`src/Quiver/Stores/VersionedHyperedgeStore.cs`)。
**ハイパーエッジ**は 1 つの型とロール付きメンバー集合（アリティ 2 以上）を持つ第一級エンティティであり、
リレーションシップとは別の `EntityKind` として格納される。
header レコードが MVCC 可視性の正本になる。

**Hyperedge header レコード**（固定領域 15 バイト、version ヘッダ 24 バイトの後ろ）:

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Flags (in-use) |
| 1 | 2 | TypeId（インターンされたハイパーエッジ型） |
| 3 | 6 | FirstIncidenceId（メンバーチェーンの先頭） |
| 9 | 6 | FirstPropertyId（オーバーフロープロパティチェーンの先頭） |

- `VersionedRecordHeap` + `ItemPointerMap` 上の可変長 payload であり、固定領域の後ろに
  ノードと同形式の inline property 領域（copy-on-write）が続く。超過分は既存の
  PropertyStore チェーンを `FirstPropertyId` から辿る
- xmin/xmax は heap の version ヘッダ、generation と SSN スタンプは `EntityVersionMeta` サイドカーに置く
- メンバー集合は作成時に確定し、以後変更されない。変更は削除 + 再作成で表現する
- 同じロールとノードの組は 1 つのハイパーエッジ内で重複できない。
  同じノードが別ロールで参加すること、同じロールに複数ノードが参加することは許される
- ハイパーエッジ型名とロール名は、ラベルと同様それぞれ独立したトークンストアで
  16 bit ID（`HyperedgeTypeId` / internal な RoleId）にインターンされる

## Incidence ストア {#incidence-store}

`IncidenceStore` (`src/Quiver/Stores/IncidenceStore.cs`)。
**incidence** は「どのノードが、どのロールで、どのハイパーエッジに属すか」を表す node-hyperedge 対
（内部表現）であり、独立した MVCC エンティティではない。
可視性は参照先の hyperedge header に従う。

**Incidence slot**（27 バイト固定、1 ページあたり 302 slot）:

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Flags (in-use / free) |
| 1 | 6 | HyperedgeId (Sequence) |
| 7 | 6 | NodeId (Sequence) |
| 13 | 2 | RoleId（インターンされたロール） |
| 15 | 6 | NextInNode（同一ノードの incidence チェーン） |
| 21 | 6 | NextInHyperedge（同一ハイパーエッジのメンバーチェーン） |

- **fixed-slot 直接アドレス方式**: `sequence → (page = seq / 302 + 2, offset = seq % 302 × 27)` で
  slot を直引きし、version チェーンも間接ポインタ層（map / slot directory）も持たない。
  ヘッダページ (page 1) に高水位と free chain 先頭を置く
- incidence は 2 本のチェーンを貫通する。ノード側は `NextInNode`、ハイパーエッジ側は
  `NextInHyperedge` を辿る。ノード側チェーンの走査は、参照先 header が不可視の incidence を
  skip して後続を継続する
- 逆方向リンク（PrevInNode）は持たない。vacuum の unlink は、dead incidence をノード別に
  グループ化し、影響ノードのチェーンを head から 1 回だけ走査する sweep
  （合計 O(影響チェーン長)）で行う
- free chain は空 slot の `NextInNode` 領域を転用する。slot を free に戻せるのは
  「全 live チェーンから unlink 済み、かつアクティブトランザクションなし」のときに限る

incidence 自身が xmin/xmax を持たないのは、可視性判定に header だけを使うためである。
undo（abort / savepoint）とクラッシュリカバリは物理 page image で行われレイアウトに依存しない。

## Node incidence head {#node-incidence-head}

`NodeIncidenceHeadStore` (`src/Quiver/Stores/NodeIncidenceHeadStore.cs`)。
node sequence を添字に、そのノードのノード側チェーン先頭 incidence（6 バイト Int48）を保持する
固定長 sidecar。head をノードレコード本体に持たせないのは、ハイパーエッジを使わない
ワークロードのノード読み取り帯域を増やさないためである（インライン案との実測比較で採用）。

## Hyperedge の vacuum {#hyperedge-vacuum}

`VacuumTarget.Hyperedges` は visibility horizon を越えた dead hyperedge を
プロパティ → incidence → header の順に回収する。

- incidence の unlink はノード別 sweep（上記）で行い、node incidence head が回収対象を
  指す場合は次の生存 incidence へ進める
- header slot は free list へ戻し、sequence 再利用時に generation を進める。
  古い ID による参照（ベクトル binding を含む）は世代照合で弾く
- 回収件数は `VacuumReport.ReclaimedHyperedges` / `ReclaimedIncidences` で報告される

## Property ストア {#property-store}

`PropertyStore` (`src/Quiver/Storage/Records/PropertyStore.cs`)。

**Property レコード** (41 バイト):

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Flags |
| 1 | 4 | KeyId（インターンされたプロパティキー） |
| 5 | 1 | ValueType |
| 6 | 24 | InlineValue（最大 24 バイトをインライン） |
| 30 | 5 | SpilloverId（24 バイト超の値用） |
| 35 | 6 | NextPropId（プロパティチェーン） |

- **1 ページあたり 199 レコード** (8160 / 41)
- インライン容量: 24 バイト。これより大きい値はオーバーフローページにスピルする。
- MVCC バージョンメタデータは `PropertyVersionMeta` サイドカー経由
- alloc-free な読み取りパスとバージョンチェーンを持つ列指向レイアウト

## EntityRef (ID パッキング) {#entity-ref}

`EntityRef` (`src/Quiver/Core/Ids.cs`) は、エンティティの同一性を単一の `long` にパックする:

```
ビットレイアウト (MSB → LSB):
[63..60]  EntityKind   (4 bits; Node=1, Relationship=2, Property=3, Hyperedge=4)
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

## マルチバリュープロパティ {#multi-value}

`PropertyCardinality` (`Single=0`, `Set=1`) を `PropertyKeyId` ごとに永続化し、
同一キーに複数のスカラー値を持てるようにする。ストレージフォーマット変更なし —
PropertyStore チェーンが MVCC で同一 KeyId の複数エントリを既に許容しているため、
API / スキーマ層のみの拡張。

- **`AddPropertyValue`**: 既存エントリに xmax スタンプせずに新エントリを prepend (重複時はスキップ)
- **`RemovePropertyValue`**: 同一 key+value の visible エントリに xmax スタンプ
- **`GetPropertyValues`**: 同一 keyId の全 visible エントリを `PropertyValuesEnumerator` で列挙
- **B+Tree インデックス**: 要素単位。`Has("tags", "sensor")` は既存 B+Tree ルックアップで包含クエリとして動作
- **Cardinality 制約**: `SetProperty` を Set キーに呼ぶと例外、`AddPropertyValue` を Single キーに呼ぶと例外
- **SourceGenerator**: `List<T>` / `IList<T>` / `IReadOnlyList<T>` を検出し Set cardinality で CRUD を emit
- **型付きトラバーサル**: `Has(s => s.Tags, "outdoor")` で包含フィルタ、`Values(s => s.Tags)` でノードごとの値リスト取得

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
| `Int64Equality` | int64 | SeekIndex による完全一致 |
| Range indexes | int64 | RangeIndex による範囲スキャン |

### ジャーナリングモード {#btree-journal}

B+Tree インデックスは型に応じて異なる WAL ジャーナリングモードで動作する:

| モード | ページ WAL | CLR | 用途 |
|---|---|---|---|
| `Full` | PageImage + coalesce | before-image を取得 | 標準インデックス |
| `Suppressed` | PageImage なし | CLR なし | FT postings/norms リーフ（論理 WAL のみ） |
| `RedoOnly` | eager な PageImage | undo なし | FT 構造ページ（nested top action） |
