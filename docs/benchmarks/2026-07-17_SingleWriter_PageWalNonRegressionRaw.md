# Single Writer page-image WAL 非回帰測定

## 測定条件

- 測定日：2026-07-17（Asia/Tokyo）
- 比較基点：`0e7c3bb04396a36b70c052bf85b2bee5fd4b2103`
- 候補：`ede755edac004606e618bf8d77e3d8f01d7f2eb6`
- machine：NIRVANA
- logical processors：16
- runtime：.NET 10.0.9
- configuration：Release

両 commit で次のコマンドを順番に実行した。

```powershell
dotnet run -c Release --project benchmarks\Quiver.Benchmarks --disable-build-servers -- --clean-slate-page-wal-baseline 20 200 5000 20 1000 20
```

## 判定

| 指標 | 比較基点 | 候補 | 判定 |
|---|---:|---:|---|
| RAG ingest WAL amplification | 16.37x | 16.26x | PASS（基点比0.993x） |
| durable edge property update p50 | 1177.20 us | 1217.00 us | PASS（3491.40 us以内） |
| vector recall@10 | 1.000 | 1.000 | PASS |

page-image WALの増幅率は比較基点から0.67%減少した。

## 比較基点の生出力

```text
=== Clean-slate QUIVER-SW page-WAL baseline ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
degree=20, traversalIterations=200, fullTextChunks=5000, fullTextQueries=20, vectorCount=1000, vectorQueries=20

--- edge row-path baseline ---
predicate_2hop, edges=400, matches=100, p50_ms=1.3162, p95_ms=1.9443, throughput_traversals_per_sec=759.8
edge_property_direct_lookup, ns_per_lookup=768.1
edge_property_update_commit, commits=200, p50_us=1177.20, p95_us=1427.30
csv,edge,predicate_2hop_p50_ms,predicate_2hop_p95_ms,direct_lookup_ns,update_commit_p50_us,update_commit_p95_us
csv,edge,1.3162,1.9443,768.1,1177.20,1427.30

--- full-text page-WAL baseline ---
fulltext_ingest_wal, chunks=5000, with_index_bytes=95485158, plain_bytes=5832603, amplification=16.37, with_index_ms=4230, plain_ms=442
fulltext_search, chunks=5000, queries=20, build_ms=3631, p50_ms=5.476, p95_ms=19.019, max_ms=31.227
csv,fulltext,chunks,queries,wal_amplification,search_p50_ms,search_p95_ms
csv,fulltext,5000,20,16.37,5.476,19.019

--- vector page-WAL baseline ---
vector_search, vectors=1000, queries=20, build_ms=568, recall@10=1.000, mean_ms=0.238, p50_ms=0.229
csv,vector,vectors,queries,recall,mean_ms,p50_ms
csv,vector,1000,20,1.000,0.238,0.229
```

## 候補の生出力

```text
=== Clean-slate QUIVER-SW page-WAL baseline ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
degree=20, traversalIterations=200, fullTextChunks=5000, fullTextQueries=20, vectorCount=1000, vectorQueries=20

--- edge row-path baseline ---
predicate_2hop, edges=400, matches=100, p50_ms=1.7355, p95_ms=1.8490, throughput_traversals_per_sec=576.2
edge_property_direct_lookup, ns_per_lookup=884.7
edge_property_update_commit, commits=200, p50_us=1217.00, p95_us=1529.30
csv,edge,predicate_2hop_p50_ms,predicate_2hop_p95_ms,direct_lookup_ns,update_commit_p50_us,update_commit_p95_us
csv,edge,1.7355,1.8490,884.7,1217.00,1529.30

--- full-text page-WAL baseline ---
fulltext_ingest_wal, chunks=5000, with_index_bytes=95681984, plain_bytes=5884097, amplification=16.26, with_index_ms=4529, plain_ms=435
fulltext_search, chunks=5000, queries=20, build_ms=3789, p50_ms=5.341, p95_ms=18.017, max_ms=31.046
csv,fulltext,chunks,queries,wal_amplification,search_p50_ms,search_p95_ms
csv,fulltext,5000,20,16.26,5.341,18.017

--- vector page-WAL baseline ---
vector_search, vectors=1000, queries=20, build_ms=656, recall@10=1.000, mean_ms=0.199, p50_ms=0.182
csv,vector,vectors,queries,recall,mean_ms,p50_ms
csv,vector,1000,20,1.000,0.199,0.182
```
