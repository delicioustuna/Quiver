# Single Writer 再設計 Wave 1 BasicPerf 生出力

- commit under test：`5919e6ebd377d8a11f1fa14010e67dc9decd9946` に未コミットの hot-path 補修を加えた作業ツリー。
- 実行日：2026-07-15（Asia/Tokyo）。
- OS と runtime：Windows、.NET 10.0.9、logical processors 16。
- 実行コマンド：`dotnet run -c Release --no-build --artifacts-path D:\csharp\Quiver-sw\.codex-temp\wave1-perf-artifacts --project benchmarks\Quiver.Benchmarks -- --basic-perf`。
- 終了コード：0。
- 実行時間：103.8 秒。
- 比較対象：`docs/benchmarks/2026-07-11_SingleWriterRedesign_BasicPerfRaw.md`。

## 判定

Wave 1 の判定対象は baseline と同じ CRUD、durable commit、warm read、BFS、query wrapper、bulk/transaction workload とする。
各値は latency または経過時間の `Wave 1 / baseline` を計算し、1.20 以下を合格とする。

| workload | baseline | Wave 1 | 比率 | 判定 |
|---|---:|---:|---:|---|
| CreateNode | 12.960 us/op | 12.980 us/op | 1.002 | PASS |
| CreateNode + SetProperty | 17.700 us/op | 17.580 us/op | 0.993 | PASS |
| CreateRelationship | 42.600 us/op | 43.180 us/op | 1.014 | PASS |
| durable commit | 1.022 ms/commit | 1.042 ms/commit | 1.020 | PASS |
| linked 1-hop、degree 10 | 223.6 ns/op | 239.6 ns/op | 1.072 | PASS |
| adjacency 1-hop、degree 10 | 14.0 ns/op | 15.9 ns/op | 1.136 | PASS |
| linked 1-hop、degree 100 | 195.6 ns/op | 204.0 ns/op | 1.043 | PASS |
| adjacency 1-hop、degree 100 | 3.6 ns/op | 3.6 ns/op | 1.000 | PASS |
| BFS 2-hop | 0.0385 ms/traversal | 0.0358 ms/traversal | 0.930 | PASS |
| query wrapper | 30,521 ns | 27,640 ns | 0.906 | PASS |
| bulk load | 1,977 ms | 1,951 ms | 0.987 | PASS |
| transaction load | 10,020 ms | 9,755 ms | 0.974 | PASS |

同じ補修済みバイナリの直前 run では `CreateNode + SetProperty` だけが 23.940 us/op（1.353 倍）となり、補修前 run の 18.440 us/op（1.042 倍）とも他 workload の傾向とも一致しなかった。
コード変更を挟まず再実行した結果が 17.580 us/op へ戻ったため、直前値は実行環境の一時的な外れ値と判定した。

## 標準出力

```text
=== Basic performance (README 性能目標) ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9

[write, single-tx amortized]  workload, N, best_ms, us/op, ops/sec
  CreateNode, 50000, 649, 12.980, 77,041
  CreateNode+SetProperty, 50000, 879, 17.580, 56,882
  CreateRelationship, 50000, 2159, 43.180, 23,158

[durable commit, 1 node = 1 commit, single-thread]  commits, total_ms, ms/commit, commits/sec
  durable, 2000, 2085, 1.042, 959

[read, warm]  metric, degree, ns/op (linked / adjblock)
  1-hop scan, 10, 239.6 / 15.9
  1-hop scan, 100, 204.0 / 3.6

[BFS 2-hop, AdjacencyBlock]  hubDegree, leaves, ms/traversal
  2-hop, 100, 10000, 0.0358

[query wrapper overhead]  raw_ns, wrapped_ns, overhead_%
  raw_adj 338ns / wrapped 27640ns (per-edge 3.4 / 276.4), 8068.9%

[bulk vs tx]  edges, bulk_ms, tx_ms, speedup
  100000, 1951, 9755, 5.0x

=== Concurrent read scaling ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
workload, threads, ops/sec, speedup_vs_1t, linear_efficiency
1-hop, 1, 33801, 1.00, 100.0%
1-hop, 2, 25394, 0.75, 37.6%
1-hop, 4, 11884, 0.35, 8.8%
1-hop, 8, 10948, 0.32, 4.0%
KNN, 1, 3962, 1.00, 100.0%
KNN, 2, 5940, 1.50, 75.0%
KNN, 4, 11516, 2.91, 72.7%
KNN, 8, 20627, 5.21, 65.1%
BM25, 1, 259, 1.00, 100.0%
BM25, 2, 338, 1.30, 65.2%
BM25, 4, 481, 1.86, 46.4%
BM25, 8, 563, 2.17, 27.1%
```
