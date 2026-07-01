# 性能回帰ベースライン有効化計画

起票日: 2026-07-02

状態: 設計案

優先度: P1

関連計画: `plans/benchmark-revalidation.md`

## 背景

`benchmarks/baselines/main.json` の `Benchmarks` は空配列である。

回帰チェッカーは基準値がない項目を skip し、比較件数が0件でも成功を返す。

nightly workflow は動作しても性能回帰を検出できない。

既存の `benchmark-revalidation.md` は利用者向け数値の再取得を扱う。

本計画はCIの回帰判定を実効化するため、目的を分離する。

## 目的

性能回帰ジョブが空の基準値や比較漏れを成功として扱わず、代表的なホットパスの劣化を検出する状態にする。

基準値の更新手順と承認条件を固定し、性能低下を基準値の更新で隠せないようにする。

## 非目標

- 全ベンチマークをPRごとに実行しない。
- 異なるハードウェアの絶対時間を精密に比較しない。
- READMEの性能値を自動更新しない。

## ベースラインの構成

回帰監視対象を `sentinel` セットとして明示する。

最低限、次の系統を各1件以上含める。

- ノード作成とプロパティ更新
- リレーション作成
- B+Tree seekとrange scan
- 1-hop traversal
- BM25検索
- HNSW KNN検索
- hybrid検索
- commitとWAL flush

長時間のスケーリング測定と診断用ベンチマークはnightly artifactに残すが、回帰ゲートから分離する。

## Phase 1: チェッカーのfail-closed化

### 対象ファイル

- `benchmarks/Quiver.Benchmarks.RegressionCheck/Program.cs`
- `benchmarks/Quiver.Benchmarks.RegressionCheck.Tests/`（新規テストプロジェクト）
- `Quiver.slnx`

### 変更内容

次の条件を設定エラーとして exit code 2 にする。

- baselineが0件
- currentが0件
- 比較件数が設定した最小件数未満
- currentのうちbaselineに存在する割合が95%未満
- `Mean` が0以下、`StandardDeviation` が負数、または数値が有限でない
- 同じ `FullName` が重複する

CLIへ次の引数を追加する。

```text
--minimum-comparisons <n>
--minimum-coverage <ratio>
--require-environment <fingerprint>
```

テストでは空baseline、部分一致、重複、不正数値、正常比較、回帰検出を固定データで検証する。

## Phase 2: 実測ベースラインの生成

基準環境を一つ決め、CPU、OS、.NET SDK、電源設定、BenchmarkDotNet versionを記録する。

ホスト型runnerを使う場合、過去の別VMとの絶対比較は参考値に留める。

厳密なゲートには、次のどちらかを採用する。

1. 固定したself-hosted runnerで履歴baselineと比較する。
2. 同一job内でmainと対象commitを順番に測定し、同じrunner上の相対値を比較する。

初期導入では固定開発機でsentinelセットを5回実行し、中央値に最も近いrunをbaselineとして採用する。

`main.json` には測定環境fingerprintと生成commitを保存する。

## Phase 3: CIの分離

### 対象ファイル

- `.github/workflows/bench.yml`
- `benchmarks/baselines/main.json`
- `benchmarks/baselines/README.md`

workflowを次の二つの責務に分ける。

- **回帰ゲート**：sentinelセットだけを実行し、baselineとの一致率と性能を判定する。
- **全量測定**：全ベンチマークを実行し、判定に使わずartifactとして保存する。

回帰ゲートは比較件数をjob summaryへ出力する。

比較件数0件、baseline coverage不足、環境fingerprint不一致は赤にする。

## ベースライン更新手順

baselineはmain上の意図した性能変更に限って更新する。

更新PRには次の情報を含める。

- 変更前後の比較表
- 低下した項目と理由
- 改善した項目
- 測定環境fingerprint
- 測定対象commit
- 5回のrun間分散

コード変更とbaseline更新を同じcommitへ混在させない。

## 検証

- 空のbaselineで回帰jobが失敗する。
- 95%未満の名前一致率で失敗する。
- 既知の20%超かつ3σ超の劣化データで失敗する。
- 改善とノイズ範囲のデータで成功する。
- 実測baselineで最低限のsentinel項目が比較される。
- `dotnet build Quiver.slnx` を実行する。

## 完了条件

- `main.json` が空ではなく、生成commitと環境情報を持つ。
- nightlyのjob summaryに比較件数とbaseline coverageが表示される。
- 監視対象の欠落を成功として扱わない。
- baseline更新手順が `benchmarks/baselines/README.md` に反映される。
