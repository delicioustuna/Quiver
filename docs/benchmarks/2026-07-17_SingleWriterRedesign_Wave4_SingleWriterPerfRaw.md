# Single Writer 再設計 Wave 4 Single Writer 性能生出力

- commit under test：`9585ba99f07414ccac5e02c04c947bfa02914831` に Wave 4 の未コミット実装を加えた作業ツリー。
- 実行日：2026-07-17（Asia/Tokyo）。
- OS と runtime：Windows、.NET 10.0.9、logical processors 16。
- 実行コマンド：`dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --single-writer-perf`。
- 終了コード：0。
- 実行時間：5.7 秒。

## 判定

32 readers と並行する writer commit p50 を、同じ runner の reader なし p50 と比較する。
合格条件は比率 1.50 以下である。

| readers | writer commit p50 | reader operations | reader なし比 | 判定 |
|---:|---:|---:|---:|---|
| 0 | 1296.90 us | 0 | 1.000 | baseline |
| 32 | 1178.90 us | 800 | 0.909 | PASS |

## 標準出力

```text
=== Single writer + snapshot readers ===
machine=NIRVANA, procs=16, runtime=.NET 10.0.9
readers, writer_commit_p50_us, reader_operations
0, 1296.90, 0
32, 1178.90, 800
ratio_vs_readerless=0.909, gate<=1.500: PASS
```
