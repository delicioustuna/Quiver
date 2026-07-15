# Single Writer 再設計 baseline: FTS-6 生出力

- 実行コマンド: `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --fts6`
- 終了コード: 0
- 実行時間: 125.1 s

標準出力:

```text
=== FTS-6: Full-Text Search / Ingest Amplification ===
searchChunks=100000, queryCount=500, batchSize=200

--- ingest WAL amplification over 5,000 chunks, checkpoint OFF (design 13 §9: target ≤ 5×) ---
WAL with FT:          77,930,908 bytes  ( 15586.2 bytes/chunk)  ingest    4877 ms
WAL plain:             6,641,975 bytes  (  1328.4 bytes/chunk)  ingest     422 ms
amplification:             11.73×

built search corpus: 100,000 chunks in  101784 ms (     982 chunks/s)

--- search latency over 100,000 chunks (design 13 §9: target p50 < 10ms) ---
queries:            500
p50:                   11.174 ms
p90:                   41.026 ms
p99:                  223.195 ms
max:                  236.750 ms

csv,ampChunks,walWithFt,walPlain,bytesPerChunkFt,amplification,searchChunks,queries
csv,5000,77930908,6641975,15586.2,11.73,100000,500
```
