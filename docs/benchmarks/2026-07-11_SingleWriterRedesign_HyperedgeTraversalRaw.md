# Single Writer 再設計 baseline: Hyperedge traversal 生出力

- 実行コマンド: `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --hyperedge-traversal`
- 終了コード: 0
- 実行時間: 10.1 s

標準出力:

```text
=== Co-membership traversal vs. binary 1-hop (product API) ===
warmup=300  iterations=3000  arity=4  gate=<=3.0x binary

  Degree  Binary us    View us   View x   Chain us  Chain x  Bin alloc View alloc   Gate
--------------------------------------------------------------------------------------------
      10     30.500     23.000    0.75x     71.400    2.34x       4704       6400   PASS
     100     33.200     27.600    0.83x     77.000    2.32x      25504      29375   PASS
    1000    316.100    266.000    0.84x    774.200    2.45x     227008     252464   PASS

overall traversal gate: PASS
```
