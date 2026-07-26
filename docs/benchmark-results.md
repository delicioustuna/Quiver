# ベンチマーク結果

本ページは、Quiver v0.3.0の性能特性を把握するための参考値を掲載する。
計測値は異なる環境での性能を保証するものではない。

計測環境はAMD Ryzen 7 5700X、Windows 11、SSD、.NET 10、Releaseビルドである。

## 基本性能

| 操作 | 実測値 |
|---|---:|
| Vertex作成、単一transaction内で償却 | 約3.5～4 µs/op |
| Vertex作成とプロパティ設定 | 約6 µs/op |
| Edge作成 | 約7 µs/op |
| 1操作ごとのdurable commit | 約1.0 ms/commit |
| degree 100の1-hop scan | 約0.35 µs |
| degree 100の1-hop Fluent query | 約4.2 µs/query |
| 10万Edgeの`BulkLoader` | 通常のbatch transaction比で約11.8倍 |

同じ操作でも、トランザクション境界、索引数、プロパティ数、データの局所性によって結果は変わる。

## HNSW true recall@10

固定seedのランダムコーパスについて、exact top-10とHNSW top-10の平均overlapを測定した。
削除後は生存集合だけでexact top-10を再計算している。

| N | 次元 | 距離 | M/Mmax0/efConstruction | 構築時間 | 検索平均 | 構築直後 | 30%削除後 |
|---:|---:|---|---|---:|---:|---:|---:|
| 10,000 | 384 | cosine | 32/64/400 | 10.00 s | 1.43 ms | 0.950 | 0.985 |

検証コマンドは次のとおりである。

```powershell
dotnet run -c Release --project benchmarks\Quiver.Benchmarks.RecallCheck
```

より軽い構築を優先する場合は、`VectorIndexDefinition`のHNSWパラメーターを明示的に調整する。
小さい`efConstruction`や`efSearch`は構築時間と検索時間を短縮できるが、recallを下げる可能性がある。

## KNN batch search

250件、384次元、32 query、k=10の同一snapshot検索を比較した。

| 経路 | 中央値 | thread allocation |
|---|---:|---:|
| `KnnSearch`を32回実行 | 175.403 ms | 35,217,736 B |
| `KnnSearchBatch` | 6.694 ms | 875,152 B |

`KnnSearchBatch`はprimary vectorを一度だけ走査し、全queryのtop-kを同時に更新する。
結果は個別検索と完全一致し、この条件では26.20倍、割り当て97.52%減だった。

```powershell
dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --knn-batch-spike
```

## 索引付き書き込みのWAL増幅

`Int64Equality`索引を持つVertexについて、Vertex作成、プロパティ設定、索引エントリ追加を1操作として計測した。

| 書き込み方法 | エントリ数 | WAL bytes/entry | 実行時間 |
|---|---:|---:|---:|
| 1 transactionに集約 | 1,000 | 71 B | 108 ms |
| 1 transactionに集約 | 10,000 | 69 B | 183 ms |
| 1 transactionに集約 | 100,000 | 69 B | 963 ms |
| 1件ごとにcommit | 1,000 | 27,108 B | 1,227 ms |
| 1件ごとにcommit | 10,000 | 2,532 B | 11,544 ms |
| 1件ごとにcommit | 100,000 | 397 B | 112,897 ms |

同じtransaction内のページ変更はまとめてWALへ記録できるため、大量挿入は適切な大きさのtransactionへ集約する。
1件ごとのcommitは、個別のdurability境界が必要な場合に限って使用する。

## MergeEdgeのdegree依存コスト

`MergeEdge`は、始点Vertexから同じ型のEdgeを調べて重複を判定する。
したがって、呼び出しコストは始点の同一型out-degreeに比例する。

| 同一型out-degree | 既存Edgeへのhit | 新規Edgeとなるmiss |
|---:|---:|---:|
| 10 | 1.67 µs | 18.20 µs |
| 50 | 5.86 µs | 22.56 µs |
| 100 | 11.71 µs | 28.28 µs |
| 500 | 57.36 µs | 75.69 µs |
| 1,000 | 116.44 µs | 135.73 µs |

重複確認が不要なら`AddEdge`を使う。
高次数Vertexへ多数のEdgeを追加する場合は、既存Edgeを一度取得し、アプリケーション側の集合で重複を判定する方法も選べる。

## 計測値の扱い

本番投入前には、実際のスキーマ、データ量、検索条件、ストレージで計測する。
特にcommit頻度、長時間reader、索引数、ベクトル次元は、スループットとディスク使用量へ直接影響する。
