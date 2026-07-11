# Single Writer 再設計 baseline

> 本書は `redesign-baseline` タグを付ける前に埋める実測記録である。
> 未測定欄がある状態ではタグを付けない。

## 1. 測定対象

- commit under test: `ee811d1a3672fb96ad8bebfdeb036099cbaf7540`
- 測定日: 未測定
- OS: 未測定
- CPU: 未測定
- RAM: 未測定
- .NET SDK/runtime: 未測定
- build configuration: `Release`
- seed: runner の固定 seed。runner が seed を表示しない場合は、その事実を結果へ記録する

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
| ARIES baseline | 未測定 | 未測定 | 未測定 |
| basic performance | 未測定 | 未測定 | 未測定 |
| full-text | 未測定 | 未測定 | 未測定 |
| hyperedge traversal | 未測定 | 未測定 | 未測定 |
| vector recall | 未測定 | 未測定 | 未測定 |

生出力は `docs/benchmarks/` 配下の測定日付き Markdown へ保存する。
一時ファイルへの出力だけで済ませない。

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
| WAL amplification | `plans/clean-slate-redesign.md` の ARIES baseline | 11.74x以下 |
| hyperedge traversal | `docs/benchmarks/2026-07-06_HYP-6c_Hyperedge.md` | degree 10、100、1000の各形状で binary/view 比3.0x以内 |

絶対値 gate は出所文書と同じ workload、runtime major、seed を再現できる場合だけ判定に使う。
再現できない場合は同一セッションで旧版と新版を測り、比率と環境差を decision log へ記録してユーザの承認を得る。

## 5. タグ付与条件

次をすべて満たしたときだけ、現在の `develop` へ `redesign-baseline` annotated tag を付ける。

- 「3. 実測結果」に未測定欄がない。
- 生出力が tracked file としてコミット済みである。
- `git diff ee811d1 -- src tests benchmarks` に production code または benchmark code の変更がない。
- baseline 文書と生出力のコミットが origin/develop へ push 済みである。
