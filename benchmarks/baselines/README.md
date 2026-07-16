# Quiver Benchmark Baselines ()

This directory holds the baseline timings used by
`Quiver.Benchmarks.RegressionCheck` to detect ≥ 20% performance regressions in
per-operator micro-benchmarks under `Quiver.Benchmarks/Operators/`.

## Files

- `main.json` — current `main`-branch baseline. One entry per operator
  benchmark (29 operators × 1 or more `[Benchmark]` methods).

## Format

A minimal subset of BenchmarkDotNet's `JsonExporter.Full` schema. Each
benchmark contributes one entry:

```json
{
  "Benchmarks": [
    {
      "FullName": "Quiver.Benchmarks.Operators.VertexByLabelScanOperatorBench.Scan_person",
      "Statistics": {
        "Mean": 12345.67,
        "StandardDeviation": 234.5
      }
    }
  ]
}
```

`Mean` is in nanoseconds (BDN default unit). `StandardDeviation` is also in
nanoseconds. `RegressionCheck` flags a regression when

```
new.Mean > old.Mean * 1.20  AND  new.Mean > old.Mean + 3 * old.StandardDeviation
```

— both the relative threshold (20%) and the absolute 3σ band must be exceeded.

## Refresh procedure

1. Run the operator suite:
   ```
   dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --filter "Quiver.Benchmarks.Operators.*"
   ```
2. BDN writes per-class `*-report-full.json` files under
   `benchmarks/Quiver.Benchmarks/BenchmarkDotNet.Artifacts/results/`.
3. Merge them into a single `benchmarks/baselines/main.json`:
   ```
   dotnet run -c Release --project benchmarks/Quiver.Benchmarks.RegressionCheck -- \
       --merge benchmarks/Quiver.Benchmarks/BenchmarkDotNet.Artifacts/results \
       --output benchmarks/baselines/main.json
   ```
4. Commit the merged file. CI-1 (planned) will automate this step on every
   `main` merge so engineers do not maintain it by hand.

## Comparing a PR run

```
dotnet run -c Release --project benchmarks/Quiver.Benchmarks.RegressionCheck -- \
    --baseline benchmarks/baselines/main.json \
    --current  benchmarks/Quiver.Benchmarks/BenchmarkDotNet.Artifacts/results/merged.json
```

Exit code `1` means a regression was found; `0` means clean.

## Initial placeholder

`main.json` currently contains zero entries. The first real baseline lands
when CI-1 enables nightly benchmark runs; until then the diff tool treats a
missing entry as "no baseline, skip" rather than as a regression.
