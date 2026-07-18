# Vector property と immutable segment の検証結果

- 実行日：2026-07-18
- 実行ホスト：NIRVANA
- 論理プロセッサ数：16
- ランタイム：.NET 10.0.9
- source：このファイルを含む commit の tree
- 構成：Release

## RecallCheck

実行コマンドは次のとおり。

```powershell
dotnet run -c Release --project benchmarks\Quiver.Benchmarks.RecallCheck --disable-build-servers
```

生出力は次のとおり。

```text
efSearch, recall@10, mean_latency_ms
32, 1.000, 72.504
64, 1.000, 74.616
100, 1.000, 80.734
200, 1.000, 80.669
[legacy] before_delete recall@10=1.000, build_ms=3182, search_mean_ms=81.910 (N=10000, dim=384, M=16, Mmax0=32, efC=200)
[legacy] after_delete_30pct recall@10=1.000
[default] before_delete recall@10=1.000, build_ms=1726, search_mean_ms=58.157 (N=10000, dim=384, M=32, Mmax0=64, efC=400)
[default] after_delete_30pct recall@10=1.000
RecallCheck PASSED
```

## Segment fan-out と merge equivalence

実行コマンドは次のとおり。

```powershell
dotnet run -c Release --project benchmarks\Quiver.Benchmarks --disable-build-servers -- --clean-slate-segment-spike
```

vector segment 部分の生出力は次のとおり。

```text
vector_segments, vectors=10000, queries=20, segments=4, recall@10=1.000, required_recall=0.950, p50_ms=3.932, p95_ms=24.410, merge_equivalence=PASS, result=PASS
csv,segment_vector,10000,20,4,1.000,3.932,24.410,PASS,PASS
segment_spike_result, result=PASS, fulltext_p50_ms=5.085, write_amplification=2.01, vector_recall=1.000
```

## Product merge worker の publish stall

実行コマンドは次のとおり。

```powershell
dotnet run -c Release --project benchmarks\Quiver.Benchmarks --disable-build-servers -- --vector-segment-publish
```

生出力は次のとおり。

```text
vector_segment_publish, vectors=10000, dimensions=384, writers_during_build=17975, writer_p99_ms=2.734, publish_p99_ms=3.051, required_publish_max_ms=500.000, result=PASS
```

artifact 構築中に通常 writer が進行し、publish transaction の lease 保持時間は既定 lock timeout の 10% 未満だった。
