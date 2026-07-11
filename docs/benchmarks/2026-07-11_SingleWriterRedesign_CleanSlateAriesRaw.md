# Single Writer 再設計 baseline: Clean-slate ARIES 生出力

- 実行コマンド: `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-aries-baseline`
- 終了コード: 0
- 実行時間: 149.9 s

標準出力:

```text
=== Clean-slate ARIES baseline ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
degree=100, traversalIterations=300, fullTextChunks=100000, fullTextQueries=500, vectorCount=10000, vectorQueries=20

--- relationship row-path baseline ---
predicate_2hop, edges=10000, matches=2500, p50_ms=5.6012, p95_ms=8.8233, throughput_traversals_per_sec=178.5
relationship_property_direct_lookup, ns_per_lookup=359.3
relationship_property_update_commit, commits=300, p50_us=1155.30, p95_us=1284.40
csv,relationship,predicate_2hop_p50_ms,predicate_2hop_p95_ms,direct_lookup_ns,update_commit_p50_us,update_commit_p95_us
csv,relationship,5.6012,8.8233,359.3,1155.30,1284.40

--- full-text ARIES baseline ---
fulltext_ingest_wal, chunks=5000, with_index_bytes=77930908, plain_bytes=6641975, amplification=11.73, with_index_ms=4815, plain_ms=499
fulltext_search, chunks=100000, queries=500, build_ms=108143, p50_ms=11.128, p95_ms=88.289, max_ms=231.953
csv,fulltext,chunks,queries,wal_amplification,search_p50_ms,search_p95_ms
csv,fulltext,100000,500,11.73,11.128,88.289

--- vector ARIES baseline ---
vector_search, vectors=10000, queries=20, build_ms=12842, recall@10=0.950, mean_ms=1.764, p50_ms=1.771
csv,vector,vectors,queries,recall,mean_ms,p50_ms
csv,vector,10000,20,0.950,1.764,1.771
```
