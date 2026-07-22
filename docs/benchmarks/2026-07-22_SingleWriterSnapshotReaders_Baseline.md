# Single Writer + Snapshot Readers 総合 baseline

- 計測日: 2026-07-22
- 対象 commit: `d89c9fc8616a4bb04c21c608769b6f93b23d54b5`
- branch: `redesign/single-writer`
- host: `NIRVANA`、16 logical processors
- runtime: .NET 10.0.9、Release、Windows x64

同一 commit と同一ホストで correctness、crash、concurrency、page/WAL、segment publish、recall の gate を実行した。
時間は runner が出力した wall-clock/percentile であり、値を丸めている。

## Build、test、監査

```text
dotnet build Quiver.slnx -c Release -v minimal
Build succeeded. 0 Warning(s), 0 Error(s)

dotnet test Quiver.slnx -c Release --no-build -m:1
Passed: 1,988, Failed: 0
Backend 236 / Client 216 / Codec 7 / Fuzz 32 / Hosting 21 / Index 21 /
Operators 316 / Property 19 / PublicApi 1 / Rag 64 / SourceGen 17 /
Storage 33 / Stores 114 / Quiver 799 / Transactions 69 / Wal 23

dotnet test tests/Quiver.Backend.Tests/Quiver.Backend.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~BinaryGraphStorageBackendCrashContractTests|Category=Chaos"
Passed: 151, Failed: 0

dotnet test tests/Quiver.Tests/Quiver.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~FullTextCrashContractTests|FullyQualifiedName~VectorSegmentSnapshotTests|FullyQualifiedName~FullTextSegmentSnapshotTests"
Passed: 16, Failed: 0

dotnet test tests/Quiver.PublicApi.Tests/Quiver.PublicApi.Tests.csproj -c Release --no-build
Passed: 1, Failed: 0

dotnet test tests/Quiver.FuzzTests/Quiver.FuzzTests.csproj -c Release --no-build
Passed: 32, Failed: 0
```

Focused suites では identity/snapshot/single-writer/vector 57 件、property ownership 5 件、RAG 64 件、vacuum/edge reuse 33 件に加え、
vector payload atomicity 2 件、derived index rebuild 3 件、WAL winner/loser 2 件、current index catalog/definition codec rejection 3 件がすべて成功した。

```text
dotnet publish samples/Quiver.Samples.Crud/Quiver.Samples.Crud.csproj --configuration Release --runtime win-x64 --output artifacts/aot
Publish succeeded. IL2xxx/IL3xxx warning: 0

artifacts/aot/Quiver.Samples.Crud.exe
Exit code: 0

scripts/verify-zero-dependency.ps1 -Configuration Release
ProjectReference: 0 / package dependencies: 0 / PASS

scripts/agent-guardrails/check-track-markers.ps1 -Scan
candidate: 0 / PASS

scripts/agent-guardrails/check-markdown-links.ps1 -Roots README.md,docs
PASS

scripts/agent-guardrails/check-skill-redirects.ps1 -AgentsRoot D:/csharp/Quiver/.agents/skills -ClaudeRoot D:/csharp/Quiver/.claude/skills
PASS
```

各 guardrail の self-test も成功した。
`.agents/skills/quiver-implement/SKILL.md` と `.claude/skills/quiver-implement/SKILL.md` の SHA-256 はともに
`11E0DC2E041A1A24BB855E0D4192FA6E371B7E2AA878F9CAAD98B49F278A93EE` で一致した。

## Transaction と reader scaling

```text
dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --basic-perf
CreateVertex: 15.180 us
CreateVertex + property: 22.620 us
CreateRelationship: 47.380 us
durable commit: 1.074 ms
2-hop traversal: 0.0394 ms

dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --single-writer-perf
0 readers writer commit p50: 1545.10 us
32 readers writer commit p50: 1194.40 us
ratio: 0.773x (limit 1.50x) / PASS

dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --read-scaling
All reader-count cases completed / PASS
```

32 reader の実行中も writer は完了し、0 reader 比は上限内だった。

## Page/WAL、CSR、Nexus

```text
dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-page-wal-baseline 20 200 5000 20 1000 20
predicate 2-hop p50: 1.7542 ms (limit 1.8982 ms) / PASS
durable edge update commit p50: 1219.80 us (limit 3491.40 us) / PASS
WAL amplification: 1.00x
vector recall@10: 1.000

dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-csr-product-integration
payload count: 2534
row count: 2534
mismatches: 0
p50: 0.9429 ms / PASS

dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --nexus-traversal
degree 10 ratio: 0.46x
degree 100 ratio: 0.46x
degree 1000 ratio: 0.47x
chain maximum ratio: 2.05x
limit: 3.00x / PASS
```

CSR runner は internal Generation 0 address を public identity として使わず、`EntityIdentityMaterializer` で logical ID に変換してから
row property を照合する。

## Full-text と vector segment

```text
dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-segment-spike
full-text p50: 7.683 ms (limit 8.55 ms) / PASS
full-text WAL amplification: 2.01x (limit 11.74x) / PASS
full-text top-k and merge contracts: PASS
vector recall@10: 1.000 / PASS
vector merge contract: PASS

dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --fulltext-segment-publish
search p50: 2.128 ms
WAL amplification: 1.01x
total amplification: 2.77x
publish p99: 2.602 ms
reopen primary scans: 0
PASS

dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --vector-segment-publish
publish p99: 3.916 ms (limit 500 ms)
PASS

dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --fts6
documents: 100000
WAL amplification: 1.00x
build: 66.897 s
search p50/p90/p99: 16.279 / 86.994 / 556.701 ms
Exit code: 0

dotnet run -c Release --project benchmarks/Quiver.Benchmarks.RecallCheck
current default (M=32 / Mmax0=64 / efConstruction=400) before/after recall@10: 1.000 / 1.000
PASSED
```

FTS6 は product latency gate ではなく、大量 ingest と検索の情報値である。
長時間 ingest で不要になった immutable artifact を保持し続けないよう、10,000 documents ごとに merge 完了を待って public vacuum を実行した。

## 判定

Single writer、32 snapshot readers、durable commit、2-hop、Nexus、full-text、vector recall、segment publish、reopen、vacuum の
correctness と数値 gate はすべて合格した。
query wrapper overhead と FTS6 latency は情報値であり、今回の合否条件には使用していない。
