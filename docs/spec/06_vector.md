# ベクトル検索

> as-built 仕様 (v1 baseline)

## ベクトルインデックス仕様 {#vector-index}

ベクトルインデックスは以下で定義される:

| フィールド | 型 | 説明 |
|---|---|---|
| Name | string | 一意な識別子 |
| Dimensions | int | ベクトルの次元数（正の値） |
| EntityKind | enum | `Node` または `Relationship` |
| Metric | enum | `Euclidean`, `Cosine`, または `Dot` |

## PersistentVectorStore {#persistent-store}

`PersistentVectorStore` (`src/Quiver/Storage/Records/PersistentVectorStore.cs`) は、`*.quiver`
ファイル内のコンテナテナントとして in-file のベクトルストレージを管理する。

- **バインディングキー**: エンティティの `Sequence`（EntityRef の slot-local 部分）
- **Generation チェック**: 古いバインディング（generation 不一致）は KNN 読み取り時にフィルタされる
- **インデックスごとのテナント**: catalog + payload + HNSW グラフ

## HNSW インデックス {#hnsw}

`HnswIndex` (`src/Quiver/Storage/Records/HnswIndex.cs`) は、近似最近傍探索のための
Hierarchical Navigable Small World グラフを実装する。

### パラメータ {#hnsw-params}

| パラメータ | 値 |
|---|---|
| M（レイヤあたり最大近傍数） | 16 |
| Mmax0（レイヤ 0 での最大近傍数） | 32 |
| EfConstruction | 200 |
| MaxLayers | 8 |

### オンディスクレイアウト {#hnsw-layout}

**ヘッダページ** (page 1):

| フィールド | 型 |
|---|---|
| EntryPoint | int64 |
| MaxLevel | int32 |
| Count | int64 |
| MaxSeq | int64 |
| FormatVersion | byte (オフセット 31) |

**Node レコード**（各 1,164 バイト固定）:

| オフセット | サイズ | フィールド |
|---|---|---|
| 0 | 1 | Present フラグ |
| 1 | 1 | Level |
| 2 | 4 | パディング |
| 4 | 12 | 近傍カウント (8 x int8、レイヤごと) |
| 12+ | 可変 | 近傍配列: (Mmax0 + (MaxLayers-1) x M) x int64 = 144 エントリ |

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
