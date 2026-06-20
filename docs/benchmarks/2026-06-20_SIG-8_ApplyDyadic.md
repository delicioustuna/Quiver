# SIG-8 ベンチマーク: `ApplyDyadicOperator` gather/score パイプライン

- 日付: 2026-06-20
- 対象: [src/Quiver/Operators/ApplyDyadicOperator.cs](../../src/Quiver/Operators/ApplyDyadicOperator.cs)
- ベンチ: [benchmarks/Quiver.Benchmarks/Operators/ApplyDyadicOperatorBench.cs](../../benchmarks/Quiver.Benchmarks/Operators/ApplyDyadicOperatorBench.cs)
- コマンド: `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --filter "*ApplyDyadic*"`

## 環境

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8655/25H2/2025Update/HudsonValley2)
AMD Ryzen 7 5700X 3.40GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.300
  [Host]   : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.8 (10.0.8, 10.0.826.23019), X64 RyuJIT x86-64-v3
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

## 結果

| Method                        | CandidateCount | Mean       | Error      | StdDev    | Gen0   | Allocated |
|------------------------------ |--------------- |-----------:|-----------:|----------:|-------:|----------:|
| 'CosineSimilarityOp k=10'     | 50             |   9.292 us |  0.3539 us | 0.0194 us | 0.1831 |   3.23 KB |
| 'NoOp (engine overhead) k=10' | 50             |   8.083 us |  0.1513 us | 0.0083 us | 0.1831 |   3.23 KB |
| 'CosineSimilarityOp k=10'     | 1000           | 174.312 us | 11.2020 us | 0.6140 us | 0.9766 |  18.32 KB |
| 'NoOp (engine overhead) k=10' | 1000           | 148.175 us |  3.9561 us | 0.2168 us | 0.9766 |  18.32 KB |

## 分析

- **エンジンオーバーヘッド** (NoOp): ~0.148µs/候補 (1000 candidates, dim=256)。設計書 §9 目標 ≤2µs を **13× マージン** でクリア。
- **CosineSimilarityOp 込み**: ~0.174µs/候補。SIMD `VectorScorer.Cosine` の実行時間は ~26µs/1000 candidates ≈ 0.026µs/候補 (dim=256)。
- **Cosine 純負荷**: 174.3 - 148.2 = ~26µs (1000 candidates × dim=256)。`VectorScorer` SIMD が支配的。
- **アロケーション**: 18.32 KB/op (1000 candidates)。`ArrayPool` rent/return のため走査ループ自体は 0 alloc。Gen0 が 0.98/1000 ops なのは `VectorKnnHeap` の結果配列確保。
- **50 candidates**: 8〜9µs。セットアップ固定費が支配的。
