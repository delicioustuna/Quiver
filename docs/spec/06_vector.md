# ベクトル検索

> as-built 仕様 (on-disk FormatVersion V3)

## ベクトルインデックス仕様 {#vector-index}

ベクトルインデックスは以下で定義される:

| フィールド | 型 | 説明 |
|---|---|---|
| Name | string | 一意な識別子 |
| Dimensions | int | ベクトルの次元数（正の値） |
| EntityKind | enum | `Node` または `Relationship` |
| Metric | enum | `Euclidean`, `Cosine`, または `Dot` |
| IndexKind | enum | `HnswFlat` または `FlatOnly` |
| ElementType | enum | 要素の格納表現。現在は `Float32` のみ (将来の量子化表現用の契約予約) |
| HnswM | int | レイヤ 1 以上の最大近傍数。既定 32、範囲 2..255 |
| HnswMMax0 | int | レイヤ 0 の最大近傍数。既定 64、範囲 HnswM..255 |
| HnswMaxLayers | int | 最大レイヤ数。既定 8、範囲 1..255 |
| HnswEfConstruction | int | 構築時ビーム幅。既定 400、範囲 HnswM..1,000,000 |

`ElementType` は payload のレコード幅と距離計算の数値型を決める契約フィールドで、
index 作成時に固定される。未対応の値は index 作成時・catalog 読込時・payload open 時の
いずれでも `VectorException` で拒否される (将来の表現で書かれた DB を誤読しない)。

## PersistentVectorStore {#persistent-store}

`PersistentVectorStore` (`src/Quiver/Stores/PersistentVectorStore.cs`) は、`*.quiver`
ファイル内のコンテナテナントとして in-file のベクトルストレージを管理する。

- **バインディングキー**: エンティティの `Sequence`（EntityRef の slot-local 部分）
- **Generation チェック**: 古いバインディング（generation 不一致）は KNN 読み取り時にフィルタされる
- **インデックスごとのテナント**: catalog + payload + HNSW グラフ

## HNSW インデックス {#hnsw}

`HnswIndex` (`src/Quiver/Stores/HnswIndex.cs`) は、近似最近傍探索のための
Hierarchical Navigable Small World グラフを実装する。

### パラメータ {#hnsw-params}

| パラメータ | 既定値 | レイアウトへの影響 |
|---|---|
| M（レイヤあたり最大近傍数） | 32 | あり |
| Mmax0（レイヤ 0 での最大近傍数） | 64 | あり |
| EfConstruction | 400 | なし（構築品質のみ） |
| MaxLayers | 8 | あり |

これらは `VectorIndexSpec` により index 作成時に確定し、catalog に永続化される。`M`、
`Mmax0`、`MaxLayers` からレコード幅を index ごとに導出する。既存 index の値は変更できない。

### オンディスクレイアウト {#hnsw-layout}

**ヘッダページ** (page 1):

| フィールド | 型 |
|---|---|
| EntryPoint | int64 |
| MaxLevel | int32 |
| Count | int64 |
| MaxSeq | int64 |
| FormatVersion | byte (オフセット 31) |

**Node レコード**（index ごとの固定長）:

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Present フラグ |
| 1 | 1 | Level |
| 2 | 2 | パディング |
| 4 | MaxLayers | 近傍カウント (レイヤごとに int8) |
| 4 + MaxLayers | 可変 | 近傍配列: (Mmax0 + (MaxLayers-1) x M) x int64 |

レコードサイズは
`4 + MaxLayers + (Mmax0 + (MaxLayers - 1) * M) * 8`。既定値では 1,164 バイト。

### ベクトルカタログ V2 {#vector-catalog-v2}

catalog の各 entry は `entryLength (int32)` に続いて、index metadata、payload/HNSW tenant、
`IndexKind`、4 つの HNSW パラメタ、`ElementType (byte)` を保持する。reader は entry 内の
既知フィールドを読み、未知の末尾を `entryLength` まで読み飛ばせる。`ElementType` を欠く
短い entry (フィールド追加前に書かれたもの) は `Float32` として読む。V1 の長さ情報なし
packed entry は読み取らず、DB open 時に `FormatVersionMismatchException` で拒否する。
移行処理は提供しない。

payload テナントのヘッダページにも `ElementType` (byte、オフセット 12) を焼き込み、
open 時に catalog 側の値と照合する。

### 操作 {#hnsw-ops}

- **Insert**: 指数減衰でレベルを割り当て、各レイヤで最近傍にリンクする
- **Search (KNN)**: エントリポイントから貪欲に走査し、レイヤを通じて精緻化する。presence チェックと
  generation フィルタ付きの top-k ヒープを用いる
- **Delete**: ノードを absent としてマークし、削除時に近傍を再リンクする
- **Rebuild**: tombstone 数がライブノード数を超えると自動で実行

### 制限 {#hnsw-limits}

- 既存 sequence の上書きは payload のみを更新する。HNSW グラフのトポロジは再リンクされない
- 再リンクと物理削除は rebuild まで遅延される

## 距離メトリクス {#distance}

`VectorScorer` (`src/Quiver/Core/VectorScorer.cs`) は、SIMD 加速された `Vector<float>` 演算で
ベクトル類似度を計算する:

| メトリクス | 数式 | 規約 |
|---|---|---|
| Dot | `sum(a[i] * b[i])` | 大きいほど類似 |
| Cosine | `dot / (norm_a * norm_b)` | 大きいほど類似 |
| Euclidean | `-sum((a[i] - b[i])^2)` | 符号反転。大きいほど類似 |

すべてのメトリクスは **スコアが大きいほど類似** という規約に従う。Euclidean 距離は符号反転されており、
すべてのメトリクスで同一の max-heap を使えるようにしている。

## トランザクション統合 {#tx-integration}

`tx.SetVector(kind, entityId, indexName, vector)` は現在のトランザクション内でベクトルを書き込む。
この書き込みはグラフの mutation と同じコンテナ WAL に相乗りするため、コミットとロールバックは
トランザクションの他の部分とアトミックである。

## KNN 検索 {#knn-search}

```csharp
var results = tx.KnnSearch("vec_idx", queryVector, k: 10);
while (results.MoveNext())
{
    NodeId id = results.Current;
    float score = results.CurrentScore;
}
```
