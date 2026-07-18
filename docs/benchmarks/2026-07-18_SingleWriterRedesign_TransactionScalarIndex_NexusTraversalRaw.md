# Single Writer 再設計 transaction/scalar index cutover Nexus traversal 生出力

- commit under test：`2145b96b463d8880a042086638681e9a1e7d9c53` の実装内容を含む作業ツリー。
- 実行日：2026-07-18（Asia/Tokyo）。
- OS と runtime：Windows、.NET 10.0.9、logical processors 16。
- 実行コマンド：`dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --nexus-traversal`。
- 終了コード：0。
- 実行時間：13.4 秒。

## 判定

degree 10、100、1000 の view/binary 比は 0.40x、1.09x、0.47x だった。
全形状で上限 3.0x を下回る。

## 標準出力

```text
=== Co-membership traversal vs. binary 1-hop (product API) ===
warmup=300  iterations=3000  arity=4  gate=<=3.0x binary

  Degree  Binary us    View us   View x   Chain us  Chain x  Bin alloc View alloc   Gate
--------------------------------------------------------------------------------------------
      10     67.900     27.400    0.40x     53.100    0.78x       3304       4760   PASS
     100     40.100     43.700    1.09x     71.000    1.77x      11140      14787   PASS
    1000    387.900    182.500    0.47x    787.400    2.03x      83048     108264   PASS

overall traversal gate: PASS
```
