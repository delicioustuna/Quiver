# Single Writer 再設計 Wave 6 BasicPerf 生出力

- commit under test：`f1909daed4dc5289d1f8fe5aa4c8c2d09a79005f` に Wave 6 の未コミット実装を加えた作業ツリー。
- 実行日：2026-07-18（Asia/Tokyo）。
- OS と runtime：Windows、.NET 10.0.9、logical processors 16。
- 実行コマンド：`dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --basic-perf`。
- 終了コード：0。
- 実行時間：113.9 秒。
- 比較対象：`docs/benchmarks/2026-07-11_SingleWriterRedesign_BasicPerfRaw.md`。

## 判定

baseline と同じ CRUD、durable commit、warm read、BFS、query wrapper、bulk/transaction workload を判定対象とする。
各値は latency または経過時間の `Wave 6 / baseline` を計算し、1.20 以下を合格とする。

| workload | baseline | Wave 6 | 比率 | 判定 |
|---|---:|---:|---:|---|
| CreateVertex | 12.960 us/op | 13.040 us/op | 1.006 | PASS |
| CreateVertex + SetProperty | 17.700 us/op | 20.080 us/op | 1.134 | PASS |
| CreateEdge | 42.600 us/op | 42.120 us/op | 0.989 | PASS |
| durable commit | 1.022 ms/commit | 1.010 ms/commit | 0.988 | PASS |
| linked 1-hop、degree 10 | 223.6 ns/op | 216.8 ns/op | 0.970 | PASS |
| segment 1-hop、degree 10 | 14.0 ns/op | 11.7 ns/op | 0.836 | PASS |
| linked 1-hop、degree 100 | 195.6 ns/op | 199.3 ns/op | 1.019 | PASS |
| segment 1-hop、degree 100 | 3.6 ns/op | 3.5 ns/op | 0.972 | PASS |
| BFS 2-hop | 0.0385 ms/traversal | 0.0376 ms/traversal | 0.977 | PASS |
| query wrapper | 30,521 ns | 23,680 ns | 0.776 | PASS |
| bulk load | 1,977 ms | 2,200 ms | 1.113 | PASS |
| transaction load | 10,020 ms | 11,094 ms | 1.107 | PASS |

判定対象の最大比率は CreateVertex + SetProperty の 1.134 であり、上限 1.20 を下回る。

## 標準出力

```text
=== Basic performance (README 性能目標) ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9

[write, single-tx amortized]  workload, N, best_ms, us/op, ops/sec
  CreateVertex, 50000, 652, 13.040, 76,687
  CreateVertex+SetProperty, 50000, 1004, 20.080, 49,800
  CreateEdge, 50000, 2106, 42.120, 23,741

[property payload boundaries]  kind, N, ms, us/op, allocated_B/op, file_B/op
  inline-int64, 5000, 259, 51.842, 56864.0, 419.4
  blob-string-300B, 5000, 578, 115.681, 75185.4, 13421.8
  vector-float32x128, 5000, 565, 113.180, 76461.2, 13421.8

[durable commit, 1 vertex = 1 commit, single-thread]  commits, total_ms, ms/commit, commits/sec
  durable, 2000, 2020, 1.010, 990

[read, warm]  metric, degree, ns/op (linked / segment)
  1-hop scan, 10, 216.8 / 11.7
  1-hop scan, 100, 199.3 / 3.5

[BFS 2-hop, adjacency segment]  hubDegree, leaves, ms/traversal
  2-hop, 100, 10000, 0.0376

[query wrapper overhead]  raw_ns, wrapped_ns, overhead_%
  raw_adj 355ns / wrapped 23680ns (per-edge 3.6 / 236.8), 6568.1%

[bulk vs tx]  edges, bulk_ms, tx_ms, speedup
  100000, 2200, 11094, 5.0x

=== Concurrent read scaling ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
workload, threads, ops/sec, speedup_vs_1t, linear_efficiency
1-hop, 1, 38763, 1.00, 100.0%
1-hop, 2, 19442, 0.50, 25.1%
1-hop, 4, 13034, 0.34, 8.4%
1-hop, 8, 11719, 0.30, 3.8%
KNN, 1, 4402, 1.00, 100.0%
KNN, 2, 8497, 1.93, 96.5%
KNN, 4, 15367, 3.49, 87.3%
KNN, 8, 23930, 5.44, 68.0%
BM25, 1, 308, 1.00, 100.0%
BM25, 2, 494, 1.60, 80.2%
BM25, 4, 698, 2.27, 56.7%
BM25, 8, 588, 1.91, 23.9%
```
