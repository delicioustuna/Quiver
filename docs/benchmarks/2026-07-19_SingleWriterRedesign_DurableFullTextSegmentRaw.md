# Single Writer 再設計 Durable Full-Text Segment 性能生出力

- 測定日：2026-07-19
- 測定対象：`5c4f6b2a25f85618d41d2b81fa8877ccdbf0b85e`
- machine：NIRVANA
- logical processors：16
- runtime：.NET 10.0.9
- configuration：Release

## 判定

| gate | 上限または条件 | 実測 | 判定 |
|---|---:|---:|---|
| product 4 segment search p50 | 8.55 ms | 1.202 ms | PASS |
| product ingest WAL amplification | 11.74x | 1.01x | PASS |
| product total write amplification | 11.74x | 2.66x | PASS |
| product manifest publish p99 | 500 ms | 2.925 ms | PASS |
| product WAND / strict | 同じ top-k | 一致 | PASS |
| product merge 前後 | 同じ visible result | 一致 | PASS |
| normal reopen primary scan | 0 | 0 | PASS |
| clean-slate 4 segment p50 | 8.55 ms | 5.132 ms | PASS |
| clean-slate write amplification | 11.74x | 2.01x | PASS |

product total write amplification は、primary payload の WAL bytes を分母とし、indexed WAL bytes と append-only segment body bytes の合計を分子とした。
したがって、WAL に載らない immutable body の絶対書き込み量を WAL amplification から分離している。

FTS6 の 100,000 chunk workload は p50 18.121 ms だった。
この値は汎用比較値として記録し、正式な product 4 segment gate の判定には使わない。

## 実行コマンド

```powershell
dotnet run -c Release --no-build --project benchmarks\Quiver.Benchmarks -- --fulltext-segment-publish
dotnet run -c Release --no-build --project benchmarks\Quiver.Benchmarks -- --clean-slate-segment-spike
dotnet run -c Release --no-build --project benchmarks\Quiver.Benchmarks -- --fts6
```

## Product full-text segment gate

```text
=== Product full-text segment gate ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9, documents=20000, queries=300
fulltext_ingest_write, documents=5000, payload_wal_bytes=1597622, manifest_plus_payload_wal_bytes=1613536, segment_body_bytes=2628688, wal_amplification=1.01, total_write_amplification=2.66, required_max=11.74
fulltext_4_segment_search, p50_ms=1.202, required_max=8.55, strict_equivalent=True
fulltext_merge_publish, merge_equivalent=True, publish_p99_ms=2.925, required_max=500
fulltext_reopen, equivalent=True, primary_scans=0, required=0
fulltext_segment_product_result, result=PASS, search_p50_ms=1.202, wal_amplification=1.01, total_write_amplification=2.66, reopen_primary_scans=0, publish_p99_ms=2.925
```

## Clean-slate segment spike

```text
=== Clean-slate full-text/vector segment spike ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
fullTextChunks=100000, fullTextQueries=500, vectorCount=10000, vectorQueries=20, segments=4
segment_contract, text=PASS, vector=PASS, hybrid=PASS, result=PASS
fulltext_segments, chunks=100000, queries=500, segments=4, p50_ms=5.132, p95_ms=13.295, required_p50_ms=8.55, topk_recall=PASS, merge_equivalence=PASS, search_result=PASS
fulltext_segment_write_amp, raw_bytes=89502660, initial_segment_bytes=91666912, merge_output_bytes=87934612, total_write_bytes=179601524, amplification=2.01, required_max=11.74, result=PASS
vector_segments, vectors=10000, queries=20, segments=4, recall@10=1.000, required_recall=0.950, p50_ms=3.086, p95_ms=23.979, merge_equivalence=PASS, result=PASS
segment_spike_result, result=PASS, fulltext_p50_ms=5.132, write_amplification=2.01, vector_recall=1.000
```

## FTS6 informational run

```text
=== Full-Text Search / Ingest Amplification ===
searchChunks=100000, queryCount=500, batchSize=200
WAL with FT:           5,899,409 bytes
WAL plain:             5,884,090 bytes
amplification:              1.00x
built search corpus: 100,000 chunks in 28932 ms
queries: 500
p50: 18.121 ms
p90: 87.504 ms
p99: 566.036 ms
max: 697.797 ms
```
