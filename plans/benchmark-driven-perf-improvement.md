# ベンチマーク駆動の性能改善アプローチ再考 — 計測コンテキストの拡充

> 起票: 2026-07-07 / 対象ブランチ: develop / 状態: 設計案 / 優先度: P2
> 出典: neue.cc「AI と高性能コード」(2026-07-06, https://neue.cc/2026/07/06_highperformancecode_with_ai.html)
> 関連: `plans/perf-improvements-query-read-write.md`（タスク A/B/C の実績）、
> `plans/benchmark-regression-baseline.md`（CI 回帰ゲート）、`plans/benchmark-revalidation.md`（数値再取得）

## 背景

記事の中核主張は「AI に機械語を読ませるコストはゼロだから、`Mean`/`Allocated` だけでなく
逆アセンブリ・分岐予測ミス・キャッシュミス等のコンテキストを最初から渡すべき」というもの。
AI がある前提では、計測の解像度を上げるほど改善サイクルの当たりが良くなる。

Quiver の既存アプローチ（spike 先行 → kill criteria を先に数値固定 → 最小 spike で実測 → 採否）は
「推論より実地検証」と整合しており、記事の哲学とも矛盾しない。実際、タスク A/B/C では
コード精読で立てた仮説が実測で 3 回反転し（plan cache 不要 / 真因は CRC32・per-pin / WAL Encode 重複）、
計測解像度を上げていれば早期に見えた項目が含まれる。

本計画は「既存アプローチを捨てる」のではなく、**計測コンテキストを 1 段深くする道具立てと運用規約**を
足すことで、次回以降のホットカーネル改善の反復効率を上げることを目的とする。

## 記事の論点と Quiver 現状の差分

| 論点 | 記事の主張 | Quiver 現状 | 採否・差分 |
|---|---|---|---|
| 計測項目 | Mean/Alloc に加え asm・BranchMispredictions/Op・CacheMisses・InstructionRetired を既定で渡す | 全ベンチ `[MemoryDiagnoser]`（Mean+Alloc）のみ。`JsonExporter.Full`→回帰チェック | **採用**。`DisassemblyDiagnoser`+`HardwareCounters` を opt-in で追加 |
| 逆アセンブリ | asm を AI に渡し「手書き理想形」まで残余欠陥（境界チェック・movsxd・未ベクトル化）を潰す | 未導入 | **採用（限定）**。対象は抽出済みマイクロカーネルのみ。全 DB standalone runner には広げない |
| HW カウンタ | BranchMispredictions が支配的な遅延源を露呈。要 admin/ETW | 未導入 | **採用（ローカル限定）**。CI ゲートには載せない（hosted runner に ETW/admin なし・非可搬） |
| 分岐予測の罠 | Count=1000 だと Zen5 予測器が固定パターンを丸暗記し誤判断。反復数を増やす | 明文規約なし。spike は単一形状（deg100）が多い | **採用**。反復下限・データランダム化・代表分布を計測妥当性規約として明文化 |
| パラメータ網羅 | Small/Large/Mixed など分布を必ず用意。単一パラメータで満足しない | 一部 `[Params]` あり。ホットカーネルは単一形状が多い | **採用（選別）**。分岐・分岐長が入力依存のカーネルを分布化 |
| AI 指示 | 短く方向づける。重厚な AGENTS.md や秘伝 Skill は賞味期限が短く不要 | `quiver-implement` skill + plans/ の spike 規約 | **現状維持**。重厚ハーネスを新設しない。本計画は「計測バンドルの渡し方」だけ足す |
| 検証責務 | AI 生成コードは line-by-line で理解。妥当性・セキュリティは人間責務。参照実装で外的検証 | plans に「コード精読で根拠」あり。公開 API 差分は `Quiver.PublicApi.Tests` | **採用（強化）**。perf spike の採用条件に line-by-line 正当化と参照比較を明記 |
| モデル横断検証 | 複数モデルで交差検証 | 方針: レビューでマルチエージェント展開しない（本体が直接 diff 検証） | **不採用**。既存方針を優先。外的検証は「参照実装比較」で担保する |

## 採用する方針（要旨）

1. **計測バンドル**: perf spike では Mean/Alloc に加え、対象カーネルの **asm** と
   **BranchMispredictions/Op・CacheMisses/Op** を取り、AI へ渡す 1 セットとして扱う。
2. **道具は opt-in・ローカル限定**: 逆アセンブリと HW カウンタは重く・環境依存なので、
   **抽出済みマイクロカーネル**の profiling config でだけ有効化する。回帰ゲートは従来どおり Mean/Alloc。
3. **計測妥当性を規約化**: 分岐予測の丸暗記を避ける反復下限・ランダム化・代表分布、
   単一形状で結論しない、を `docs/design/development.md` の運用規約に足す。
4. **採用条件に検証を明記**: 生成 diff の line-by-line 正当化と参照実装比較を kill criteria 判定の前提にする。

## 非目標（やらないこと）

- HW カウンタ・逆アセンブリを **CI 回帰ゲートに載せない**（非可搬・admin 依存・実行が重い）。
  回帰判定は `JsonExporter.Full`→`Quiver.Benchmarks.RegressionCheck`（Mean/Alloc、sentinel）を維持する。
- 逆アセンブリを **全経路（DB 全体の standalone runner）に広げない**。
  1-hop traversal 全体の asm は巨大で AI にも人間にも読み解けない。効くのは小さな純カーネルのみ。
- **AGENTS.md 相当の重厚ハーネスや新スキルを作らない**（記事の指摘どおり賞味期限が短い）。
  追加するのは profiling config 1 つと development.md の 1 節、計測バンドルの渡し方だけ。
- **モデル横断のマルチエージェント検証を導入しない**（既存方針）。外的検証は参照実装比較で代替。

## asm / HW カウンタを当てる候補カーネル

いずれも DB のセットアップから切り離せる小さな純関数で、分岐・SIMD・帯域が効く箇所。

| カーネル | 想定ボトルネック | 既存の足がかり |
|---|---|---|
| WAL varint / RLE エンコード・デコード | 値レンジ分岐（バイト数）・分岐予測ミス | タスク C の deferred-encode 経路 |
| `EntityRef`/`NodeId` の gen+seq pack/unpack | ビット演算・分岐 | ARCH-5b |
| B+Tree キー比較 / seek 内周 | 比較分岐・キー長依存 | `BTreeInsertBenchmarks` |
| ページ checksum (CRC32) | スループット・ベクトル化 | タスク B（load 時のみ検証へ変更済） |
| ベクトル距離 / スコア加算 | SIMD 化・アンロール・依存チェーン | `VectorScoreBenchmarks`, `ScorerAccumulatorRunner` |
| MVCC 可視性判定 | committed/aborted/in-flight の分岐分布 | `CommittedTxRegistry` |

## タスク

各タスクは spike 先行（kill/accept 条件を先に固定）で進め、`dotnet build` 成功と既存テスト緑を保つ。

### OBS-1 — profiling config の導入と 1 カーネルでの実証

**内容**: 逆アセンブリ + HW カウンタを opt-in で有効化する構成を追加し、候補から 1 カーネル
（varint もしくは scorer）で計測バンドルを実際に取得できることを実証する。

- `benchmarks/Quiver.Benchmarks/Quiver.Benchmarks.csproj` に `BenchmarkDotNet.Diagnostics.Windows` を追加
  （`HardwareCounters` は Windows/ETW 依存。core の `DisassemblyDiagnoser` は追加不要）。
- profiling 専用の `ManualConfig`（例: `--profile-kernel <name>` で分岐、`Job.ShortRun` で高速反復
  + `DisassemblyDiagnoser(printSource, exportHtml)` + `HardwareCounters(BranchMispredictions,
  BranchInstructions, CacheMisses, InstructionRetired, TotalCycles)`）を用意。
- 対象カーネルを DB セットアップから切り離した独立ベンチとして 1 本用意（既存があれば流用）。

**accept 条件**: 開発機（admin）で対象カーネルの asm と BranchMispredictions/Op が出力され、
Mean/Alloc の既定ベンチ・回帰チェック経路に影響がない。admin なし環境では HW カウンタが
落ちても他が動く（graceful degrade）ことを確認。

**対象**: `benchmarks/Quiver.Benchmarks/Program.cs`（フラグ分岐）、profiling config 新規、対象カーネルベンチ。

### OBS-2 — 計測妥当性ガイドラインの明文化

**内容**: 記事の「分岐予測トラップ」と「パラメータ網羅」「外的検証」を運用規約として
`docs/design/development.md`（エージェント運用ガードレール節の近傍）に短く追加する。ドキュメントのみ。

- 反復下限とデータのランダム化・代表分布（固定小配列で予測器を丸暗記させない）。
- 単一形状（例: deg100 のみ）で結論しない。分布を変えて頑健性を確認してから採否。
- perf spike の採否は **line-by-line 正当化 + 参照実装比較**（BCL primitive / 素朴実装との突き合わせ）を前提。
- 逆アセンブリ・HW カウンタは opt-in・ローカル限定であり CI ゲートではない旨を明記。

**accept 条件**: 節が追加され、既存の spike 手順（kill criteria 先行）と矛盾しない。公開 docs・src には
タスク番号・代替案表記を残さない（plans/・docs/design のみ許容）。

**対象**: `docs/design/development.md`。

### OBS-3 — ホットカーネルのパラメータ分布化

**内容**: 入力依存で分岐/分岐長が変わるカーネルを 2〜3 本選び、`[Params]` に Small/Large/Mixed 等の
分布を足して再計測。単一形状で得ていた結論（例: タスク群の spike 判断）が分布を変えても保つか検証する。

- varint: 値の大きさ分布（小 id / gen パック済みの大値 / 混在）でバイト数分岐を揺らす。
- filter/selectivity: 0% / 50% / 100% で分岐予測ミスの寄与を見る。
- traversal degree: 均一 deg100 だけでなく歪度（power-law 近似）を 1 つ足す。

**accept 条件**: 少なくとも 1 カーネルで「分布により結論が変わる/変わらない」が定量的に示され、
必要なら既存 plan の該当 spike 所見に追記。回帰 sentinel は劣化なし。

**対象**: 選定した既存ベンチ（`BTreeInsertBenchmarks` 等）、必要なら candidate カーネルベンチ。

## 検証

- `dotnet build Quiver.slnx`（各タスクでビルド成功を確認）。
- OBS-1: 開発機で計測バンドル（asm + HW カウンタ + Mean/Alloc JSON）が取得できること。
  既定の `--basic-perf` / 各ベンチ / `Quiver.Benchmarks.RegressionCheck` が従来どおり動くこと。
- OBS-2: 追加節が既存ガードレール・spike 規約と整合。
- OBS-3: 分布追加後も `Quiver.Tests` / `Quiver.Operators.Tests` 緑、sentinel 劣化なし。

## 完了条件

- opt-in の profiling config が 1 カーネルで計測バンドルを産出でき、回帰ゲートは Mean/Alloc のまま不変。
- 計測妥当性（分岐予測トラップ・分布網羅・外的検証・line-by-line 正当化）が development.md に明文化。
- 少なくとも 1 カーネルで単一形状 vs 分布の差が定量化され、今後の perf spike の既定手順に組み込まれる。
