# Single Writer 再設計 baseline: BasicPerf 生出力

- 実行コマンド: `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --basic-perf`
- 終了コード: 0
- 実行時間: 106.3 s

標準出力:

```text
=== Basic performance (README 性能目標) ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9

[write, single-tx amortized]  workload, N, best_ms, us/op, ops/sec
  CreateNode, 50000, 648, 12.960, 77,160
  CreateNode+SetProperty, 50000, 885, 17.700, 56,497
  CreateRelationship, 50000, 2130, 42.600, 23,474

[durable commit, 1 node = 1 commit, single-thread]  commits, total_ms, ms/commit, commits/sec
  durable, 2000, 2044, 1.022, 978

[read, warm]  metric, degree, ns/op (linked / adjblock)
  1-hop scan, 10, 223.6 / 14.0
  1-hop scan, 100, 195.6 / 3.6

[BFS 2-hop, AdjacencyBlock]  hubDegree, leaves, ms/traversal
  2-hop, 100, 10000, 0.0385

[query wrapper overhead]  raw_ns, wrapped_ns, overhead_%
  raw_adj 366ns / wrapped 30521ns (per-edge 3.7 / 305.2), 8234.1%

[bulk vs tx]  edges, bulk_ms, tx_ms, speedup
  100000, 1977, 10020, 5.1x

=== Concurrent read scaling ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
workload, threads, ops/sec, speedup_vs_1t, linear_efficiency
1-hop, 1, 37596, 1.00, 100.0%
1-hop, 2, 21366, 0.57, 28.4%
1-hop, 4, 12905, 0.34, 8.6%
1-hop, 8, 10783, 0.29, 3.6%
KNN, 1, 4058, 1.00, 100.0%
KNN, 2, 7632, 1.88, 94.0%
KNN, 4, 13413, 3.31, 82.6%
KNN, 8, 21724, 5.35, 66.9%
BM25, 1, 268, 1.00, 100.0%
BM25, 2, 458, 1.71, 85.4%
BM25, 4, 640, 2.39, 59.7%
BM25, 8, 591, 2.21, 27.6%
```
