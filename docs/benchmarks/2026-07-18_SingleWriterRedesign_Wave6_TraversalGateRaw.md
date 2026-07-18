# Single Writer 再設計 Wave 6 述語付き 2-hop 生出力

- commit under test：`f1909daed4dc5289d1f8fe5aa4c8c2d09a79005f` に Wave 6 の未コミット実装を加えた作業ツリー。
- 実行日：2026-07-18（Asia/Tokyo）。
- OS と runtime：Windows、.NET 10.0.9、logical processors 16。

## runner の訂正

Wave 6 着手指示書に記載した `--clean-slate-aries-baseline` は、Wave 5 で `--clean-slate-page-wal-baseline` へ置換済みであり、現行の実行入口には存在しない。
旧コマンドが終了コード 0 と usage を返すだけで測定しないことを確認した。
現行 row-path の生値は `--clean-slate-page-wal-baseline` で保存し、1.8982 ms の product-path gate は同じ形状を検証する `--clean-slate-csr-product-integration` で判定する。

## 判定

payload lane と row property の結果は 2,534 件で一致し、mismatch は 0 件だった。
述語付き 2-hop p50 は 1.2680 ms であり、上限 1.8982 ms を下回る。

## page-WAL row-path 標準出力

- 実行コマンド：`dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --clean-slate-page-wal-baseline`。
- 終了コード：0。
- 実行時間：263.6 秒。

```text
=== Clean-slate QUIVER-SW page-WAL baseline ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
degree=100, traversalIterations=300, fullTextChunks=100000, fullTextQueries=500, vectorCount=10000, vectorQueries=20

--- edge row-path baseline ---
predicate_2hop, edges=10000, matches=2500, p50_ms=5.2644, p95_ms=8.2721, throughput_traversals_per_sec=190.0
edge_property_direct_lookup, ns_per_lookup=411.2
edge_property_update_commit, commits=300, p50_us=1112.20, p95_us=1220.10
csv,edge,predicate_2hop_p50_ms,predicate_2hop_p95_ms,direct_lookup_ns,update_commit_p50_us,update_commit_p95_us
csv,edge,5.2644,8.2721,411.2,1112.20,1220.10

--- full-text page-WAL baseline ---
fulltext_ingest_wal, chunks=5000, with_index_bytes=117566099, plain_bytes=5884090, amplification=19.98, with_index_ms=4617, plain_ms=439
fulltext_search, chunks=100000, queries=500, build_ms=223914, p50_ms=9.742, p95_ms=75.459, max_ms=228.134
csv,fulltext,chunks,queries,wal_amplification,search_p50_ms,search_p95_ms
csv,fulltext,100000,500,19.98,9.742,75.459

--- vector page-WAL baseline ---
vector_search, vectors=10000, queries=20, build_ms=10254, recall@10=0.950, mean_ms=1.576, p50_ms=1.542
csv,vector,vectors,queries,recall,mean_ms,p50_ms
csv,vector,10000,20,0.950,1.576,1.542
```

## product-path 標準出力

- 実行コマンド：`dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --clean-slate-csr-product-integration`。
- 終了コード：0。
- 実行時間：12.7 秒。

```text
=== Clean-slate CSR product integration ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
degree=100, iterations=300, mutation_count=100

validation, payload_matches=2534, row_matches=2534, mismatches=0, result=PASS
mutation_trace, updated=100, deleted=100, inserted=100
compact, elapsed_ms=3501.26, result=REFERENCE
integrated_predicate_2hop, p50_ms=1.2680, p95_ms=1.2977, required_p50_ms=1.8982, result=PASS
csv,csr_product_integration,payload_matches,row_matches,mismatches,compact_ms,p50_ms,p95_ms,result
csv,csr_product_integration,2534,2534,0,3501.26,1.2680,1.2977,PASS
```
