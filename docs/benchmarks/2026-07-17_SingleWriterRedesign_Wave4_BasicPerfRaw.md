# Single Writer 再設計 Wave 4 BasicPerf 生出力

- commit under test：`9585ba99f07414ccac5e02c04c947bfa02914831` に Wave 4 の未コミット実装を加えた作業ツリー。
- 実行日：2026-07-17（Asia/Tokyo）。
- OS と runtime：Windows、.NET 10.0.9、logical processors 16。
- 実行コマンド：`dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --basic-perf`。
- 終了コード：0。
- 実行時間：104.4 秒。
- 比較対象：`docs/benchmarks/2026-07-11_SingleWriterRedesign_BasicPerfRaw.md`。

## 判定

baseline と同じ CRUD、durable commit、warm read、BFS、query wrapper、bulk/transaction workload を判定対象とする。
各値は latency または経過時間の `Wave 4 / baseline` を計算し、1.20 以下を合格とする。

| workload | baseline | Wave 4 | 比率 | 判定 |
|---|---:|---:|---:|---|
| CreateVertex | 12.960 us/op | 12.920 us/op | 0.997 | PASS |
| CreateVertex + SetProperty | 17.700 us/op | 19.960 us/op | 1.128 | PASS |
| CreateEdge | 42.600 us/op | 42.300 us/op | 0.993 | PASS |
| durable commit | 1.022 ms/commit | 1.018 ms/commit | 0.996 | PASS |
| linked 1-hop、degree 10 | 223.6 ns/op | 226.7 ns/op | 1.014 | PASS |
| segment 1-hop、degree 10 | 14.0 ns/op | 12.7 ns/op | 0.907 | PASS |
| linked 1-hop、degree 100 | 195.6 ns/op | 197.3 ns/op | 1.009 | PASS |
| segment 1-hop、degree 100 | 3.6 ns/op | 3.6 ns/op | 1.000 | PASS |
| BFS 2-hop | 0.0385 ms/traversal | 0.0381 ms/traversal | 0.990 | PASS |
| query wrapper | 30,521 ns | 24,815 ns | 0.813 | PASS |
| bulk load | 1,977 ms | 2,100 ms | 1.062 | PASS |
| transaction load | 10,020 ms | 8,904 ms | 0.889 | PASS |

判定対象の最大比率は CreateVertex + SetProperty の 1.128 であり、上限 1.20 を下回る。
durable commit は 1018 us/commit であり、上限 3491.40 us を下回る。

## 標準出力

```text
=== Basic performance (README 性能目標) ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9

[write, single-tx amortized]  workload, N, best_ms, us/op, ops/sec
  CreateVertex, 50000, 646, 12.920, 77,399
  CreateVertex+SetProperty, 50000, 998, 19.960, 50,100
  CreateEdge, 50000, 2115, 42.300, 23,640

[property payload boundaries]  kind, N, ms, us/op, allocated_B/op, file_B/op
  inline-int64, 5000, 218, 43.617, 55863.8, 419.4
  blob-string-300B, 5000, 565, 113.139, 65514.0, 13421.8
  vector-float32x128, 5000, 528, 105.799, 66008.3, 13421.8

[durable commit, 1 vertex = 1 commit, single-thread]  commits, total_ms, ms/commit, commits/sec
  durable, 2000, 2036, 1.018, 982

[read, warm]  metric, degree, ns/op (linked / segment)
  1-hop scan, 10, 226.7 / 12.7
  1-hop scan, 100, 197.3 / 3.6

[BFS 2-hop, adjacency segment]  hubDegree, leaves, ms/traversal
  2-hop, 100, 10000, 0.0381

[query wrapper overhead]  raw_ns, wrapped_ns, overhead_%
  raw_adj 369ns / wrapped 24815ns (per-edge 3.7 / 248.1), 6617.1%

[bulk vs tx]  edges, bulk_ms, tx_ms, speedup
  100000, 2100, 8904, 4.2x

=== Concurrent read scaling ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
workload, threads, ops/sec, speedup_vs_1t, linear_efficiency
1-hop, 1, 38133, 1.00, 100.0%
1-hop, 2, 16827, 0.44, 22.1%
1-hop, 4, 11980, 0.31, 7.9%
1-hop, 8, 11162, 0.29, 3.7%
KNN, 1, 4332, 1.00, 100.0%
KNN, 2, 8493, 1.96, 98.0%
KNN, 4, 15094, 3.48, 87.1%
KNN, 8, 23792, 5.49, 68.7%
BM25, 1, 296, 1.00, 100.0%
BM25, 2, 477, 1.61, 80.5%
BM25, 4, 612, 2.07, 51.6%
BM25, 8, 538, 1.82, 22.7%
```
