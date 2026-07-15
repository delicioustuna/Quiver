# Single Writer 再設計 baseline: RecallCheck 生出力

- 実行コマンド: `dotnet run -c Release --project benchmarks/Quiver.Benchmarks.RecallCheck`
- 終了コード: 0
- 実行時間: 90.0 s

標準出力:

```text
efSearch, recall@10, mean_latency_ms
32, 0.245, 0.380
64, 0.435, 0.512
100, 0.570, 0.706
200, 0.825, 1.160
[legacy] before_delete recall@10=0.825, build_ms=7028, search_mean_ms=1.251 (N=10000, dim=384, M=16, Mmax0=32, efC=200)
[legacy] after_delete_30pct recall@10=0.865
[default] before_delete recall@10=0.950, build_ms=11737, search_mean_ms=1.449 (N=10000, dim=384, M=32, Mmax0=64, efC=400)
[default] after_delete_30pct recall@10=0.985
RecallCheck PASSED
```
