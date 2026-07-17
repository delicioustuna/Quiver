# Single Writer 再設計 baseline

> 本書は `redesign-baseline` タグを付ける前に埋める実測記録である。
> 未測定欄がある状態ではタグを付けない。

## 1. 測定対象

- commit under test (production): `ee811d1a3672fb96ad8bebfdeb036099cbaf7540`
- 実行時 HEAD: `bf8b05b6b073f77848a9fe8aed34eae8e67f3c37` (`src`、`tests`、`benchmarks` は基点 commit と差分なし)
- 測定日: 2026-07-11 (Asia/Tokyo)
- OS: Windows 10.0.26200 (`dotnet --info`)。CIM による詳細取得は sandbox のアクセス拒否で不可
- CPU: runner 報告では logical processors=16。モデル名は CIM のアクセス拒否で未取得
- RAM: CIM のアクセス拒否で未取得
- .NET SDK/runtime: SDK 10.0.301 / .NET 10.0.9
- build configuration: `Release`
- seed: 外部 seed 引数は指定しなかった。5 runner の標準出力はいずれも seed を表示しなかった

## 2. 着工前に実行するコマンド

次の順序で実行し、標準出力と終了コードを本書の「3. 実測結果」へ保存する。
一部の runner が失敗した場合は、結果を削除せず失敗理由を記録する。

```powershell
dotnet build Quiver.slnx -c Release -v minimal
dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-aries-baseline
dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --basic-perf
dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --fts6
dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --hyperedge-traversal
dotnet run -c Release --project benchmarks/Quiver.Benchmarks.RecallCheck
```

既定件数では運用時間内に完走しない runner がある場合、件数を下げて結果を baseline として固定してよい。
その場合は実際に使った引数を省略せず記録し、再設計後も同じ引数で比較する。

## 3. 実測結果

| workload | 実行コマンド | 結果 | 生出力の保存先 |
|---|---|---|---|
| Release build | `dotnet build Quiver.slnx -c Release -v minimal` | exit 0, 27.28 s, 0 errors, Studio warnings 3 | terminal output only |
| ARIES baseline | `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-aries-baseline` | exit 0, 149.9 s; relationship p50 5.6012 ms, update p50 1155.30 us; FT 11.73x / p50 11.128 ms; vector recall@10 0.950 | `docs/benchmarks/2026-07-11_SingleWriterRedesign_CleanSlateAriesRaw.md` |
| basic performance | `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --basic-perf` | exit 0, 106.3 s; durable 1.022 ms/commit, BFS 2-hop 0.0385 ms, bulk/tx 5.1x | `docs/benchmarks/2026-07-11_SingleWriterRedesign_BasicPerfRaw.md` |
| full-text | `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --fts6` | exit 0, 125.1 s; WAL amplification 11.73x, corpus build 101784 ms, p50 11.174 ms | `docs/benchmarks/2026-07-11_SingleWriterRedesign_Fts6Raw.md` |
| hyperedge traversal | `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --hyperedge-traversal` | exit 0, 10.1 s; degree 10/100/1000 view gate all PASS | `docs/benchmarks/2026-07-11_SingleWriterRedesign_HyperedgeTraversalRaw.md` |
| vector recall | `dotnet run -c Release --project benchmarks/Quiver.Benchmarks.RecallCheck` | exit 0, 90.0 s; legacy 0.825/0.865, default 0.950/0.985 after deletion; PASSED | `docs/benchmarks/2026-07-11_SingleWriterRedesign_RecallCheckRaw.md` |

生出力は `docs/benchmarks/` 配下の測定日付き Markdown へ保存した。
元の一時ログは `C:\Users\srgf_\AppData\Local\Temp\quiver-redesign-baseline-20260711-*.log` にも残している。

## 4. gate の出所

基点 commit に存在しない segment 構造は、この baseline で測定したことにしない。
各 gate は次の出所を使う。

| gate | 出所 | 比較規則 |
|---|---|---|
| 32 readers + 1 writer | 本書の `--basic-perf` または Wave 4 で追加する同一 runner の reader 無し比較 | writer commit p50 が reader 無し比 1.5x 以内。reader が writer を待たない |
| durable point update | `docs/benchmarks/2026-07-08_CleanSlate_AriesBaseline.md` | p50 1163.80 us の3x以内 |
| relationship 2-hop | `plans/clean-slate-redesign.md` の固定 gate | p50 1.8982 ms 以下 |
| full-text 4 segment | `plans/clean-slate-redesign.md` の segment gate | p50 8.55 ms 以下 |
| vector | `plans/clean-slate-redesign.md` と RecallCheck | recall@10 0.95以上 |
| page-image WAL amplification | `redesign-wave-4` と Wave 5 の同一 runner | 同一環境のWave 4比1.00x以内。2026-07-17の参照値はWave 4が16.37x |
| full-text segment WAL amplification | `plans/clean-slate-redesign.md` の ARIES baseline | Wave 8で11.74x以下 |
| hyperedge traversal | `docs/benchmarks/2026-07-06_HYP-6c_Hyperedge.md` | degree 10、100、1000の各形状で binary/view 比3.0x以内 |

絶対値 gate は出所文書と同じ workload、runtime major、seed を再現できる場合だけ判定に使う。
再現できない場合は同一セッションで旧版と新版を測り、比率と環境差を decision log へ記録してユーザの承認を得る。
11.74xはFT専用logical WALと専用recovery passを持つARIES方式の測定値なので、汎用`PageImage`へ統一するWave 5の合否には使わない。

## 5. タグ付与条件

次をすべて満たしたときだけ、現在の `develop` へ `redesign-baseline` annotated tag を付ける。

- 「3. 実測結果」に未測定欄がない。
- 生出力が tracked file としてコミット済みである。
- `git diff ee811d1 -- src tests benchmarks` に production code または benchmark code の変更がない。
- baseline 文書と生出力のコミットが origin/develop へ push 済みである。
