# レコード & インデックス

> as-built 仕様 (v1 baseline)

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
[63..60]  EntityKind   (4 bits; Node=0, Relationship=1)
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
