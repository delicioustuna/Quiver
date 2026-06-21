# ベンチマーク結果

基本性能（ノード作成、1-hop スキャン、BFS 等）は [README の「性能（基本計測）」](../README.md#性能基本計測) を参照。
本ページは、運用ガイドや cookbook で参照される計測データの詳細を掲載する。

計測環境は共通: AMD Ryzen 7 5700X (16 logical cores) / Windows 11 / SSD / .NET 10 / Release ビルド。
計測法は in-process Stopwatch、warmup 後 best-of-N。

---

## 索引付き書き込みの WAL 増幅

索引（B+Tree）を持つノードの大量挿入で、書き込みパターンによって WAL サイズとスループットが
桁違いに変わることを示す計測結果。
[パフォーマンスチューニングガイド](operations/03_performance_tuning.md) の「鉄則」の根拠。

### ワークロード

`Int64Equality` 索引（8-byte キー + 8-byte 値）付きノードの挿入。
各シナリオで、ノード作成 + プロパティ設定 + 索引エントリ挿入を 1 操作として計測。

### 結果

| パス | エントリ数 | WAL bytes/entry | 実行時間 (ms) |
|---|---:|---:|---:|
| **bulk (1 tx にまとめる)** | 1,000 | 148 B | 122 |
| **bulk (1 tx にまとめる)** | 10,000 | 109 B | 204 |
| **bulk (1 tx にまとめる)** | 100,000 | **104 B** | 1,022 |
| per-tx (1 件 1 commit) | 1,000 | **65,965 B** | 1,200 |
| per-tx (1 件 1 commit) | 10,000 | 5,617 B | 10,838 |
| per-tx (1 件 1 commit) | 100,000 | 252 B | 107,728 |

### 解釈

**bulk パス**では、同一ページへの複数変更が 1 つの PageImage に coalesce されるため、
エントリ数に対してほぼフラットな ~100 B/entry に収まる。100k エントリでも WAL は 10 MB 程度。

**per-tx パス**では、commit のたびに変更ページ（NodeStore + 索引 + meta）の PageImage を丸ごと WAL に書く。

- 小規模 (1k) ではエントリあたり ~66 KB の極端な増幅になる。1,000 回の commit が累積して WAL 66 MB。
- 大規模 (100k) では checkpoint truncation（`CheckpointThresholdBytes` 超過時の WAL 切り詰め）が効き、
  252 B/entry まで減衰する。WAL の絶対サイズは ~25 MB で頭打ち。
- ただしスループットは bulk パスの約 100 分の 1 (~935 inserts/sec vs ~100k inserts/sec)。

**実用上の指針**: 大量挿入は 1 トランザクションにまとめること。per-tx パターンは厳密な粒度の
原子性が必要な場合にのみ使い、スループットの犠牲を許容する。

---

## MergeRelationship の degree 依存コスト

`MergeRelationship` は既存エッジの重複を防ぐ upsert 操作を提供する。
内部では始点ノードの同一型 outgoing edge を線形スキャンして既存マッチを探すため、
スキャンコストは **始点の同一型 out-degree に比例** する。
[cookbook の MergeRelationship セクション](cookbook.md) の根拠。

### 1. 単一呼び出し: hit (既存辺にマッチ)

始点に指定本数の同一型 outgoing edge がある状態で、既存辺に対して MergeRelationship を呼ぶレイテンシ。

| 同一型 out-degree | µs/call |
|---:|---:|
| 1 | 2.57 |
| 10 | 1.67 |
| 50 | 5.86 |
| 100 | 11.71 |
| 500 | 57.36 |
| 1,000 | 116.44 |

degree 50〜1,000 の回帰で **~116 ns/edge** の勾配。固定オーバーヘッド（列挙セットアップ + target 照合）は ~2.5µs。

### 2. 単一呼び出し: miss (マッチなし、新規作成)

始点に既存辺がある状態で、存在しない target への MergeRelationship。
全辺をスキャンし終えてから `CreateRelationship` を 1 本実行する。

| 同一型 out-degree | µs/call |
|---:|---:|
| 10 | 18.20 |
| 50 | 22.56 |
| 100 | 28.28 |
| 500 | 75.69 |
| 1,000 | 135.73 |

miss ≈ full scan + CreateRelationship (~6µs)。degree 100 で hit 11.7µs、miss 28.3µs。

### 3. CreateRelationship baseline (存在チェックなし)

| µs/call |
|---:|
| 6.13 |

### 4. 直積 upsert の実時間

Traversal API の `MergeRelationship` (materialize → loop) による直積 upsert。
`degree_before` は各始点に事前に張った同一型辺の本数。

| sources | targets | degree_before | total ms | µs/pair |
|---:|---:|---:|---:|---:|
| 10 | 10 | 0 | 0.88 | 8.82 |
| 10 | 10 | 100 | 2.00 | 19.95 |
| 50 | 50 | 0 | 24.96 | 9.98 |
| 50 | 50 | 100 | 55.15 | 22.06 |

### コスト構造

```
MergeRelationship(src, tgt, type) =
    スキャン: ~116 ns × (src の type 型 out-degree) + ~2.5µs 固定
  + 作成 (miss のみ): ~6µs
```

hit/miss いずれでもスキャンコストが支配的。**degree が低い (< 50) うちは 1 回あたり数µs で実用上問題にならない。**

### degree が高い場合の対処

| out-degree | 1 回の hit コスト | 10×10 直積 (全 hit) 見積り |
|---:|---:|---:|
| 10 | ~2µs | ~0.2ms |
| 100 | ~12µs | ~1.2ms |
| 1,000 | ~116µs | ~12ms |
| 10,000 | ~1,160µs (推定) | ~116ms |

degree 1,000 を超える始点ノードで MergeRelationship を多用するとコストが顕在化する。
そのような高 fan-out ノードでは:

1. **`AddRelationship` を使う** — 存在チェックを省略 (~6µs/call で degree 非依存)
2. **アプリ層で重複制御する** — `HashSet` 等で既存辺を 1 度だけ取得しチェック

RAG バックエンド想定の典型ワークロード (degree < 50、ペア数 < 数百) であれば
MergeRelationship のコストは問題にならない。
