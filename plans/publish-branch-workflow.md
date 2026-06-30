# 公開ブランチ構成 — publish スクリプトとリポジトリ衛生

## 背景

develop ブランチには内部計画書やサンドボックスなど、外部開発者には不要なファイルが含まれる。
main ブランチを公開用に維持し、develop からの同期時にこれらを除外する仕組みを作る。

**判断基準**: 「クローン/フォークした開発者がビルド・テスト・貢献するために必要か？」

---

## 1. 除外対象の棚卸し

### 公開ブランチから除外するもの

| パス | 理由 | 現状 |
|------|------|------|
| `plans/` | 内部計画書 (22 ファイル)。設計経緯は公開不要 | git 追跡済み |
| `sandbox/` | 開発実験用 (QuiverSandbox, RagSandbox) | git 追跡済み + sln 登録 |
| `scripts/publish.sh` | publish スクリプト自体。公開側には不要 | 新規作成 |

### 既に .gitignore で除外済み（対応不要）

| パス | 備考 |
|------|------|
| `.claude/`, `CLAUDE.md` | Claude Code 設定 |
| `docs/design/` | 内部設計メモ (development.md 等) |
| `docs/benchmarks/` | ベンチマーク計測ログ |
| `*.quiver`, `*.quiver-wal` | 生成 DB ファイル (tools/sample/ 含む) |
| `BenchmarkDotNet.Artifacts/` | ベンチマーク出力 |

### 公開するもの（除外しない）

| パス | 外部開発者にとっての価値 |
|------|------------------------|
| `src/` | ソース本体 |
| `tests/` | テストスイート — PR 時にビルド・テスト必須 |
| `samples/` | 使い方の実例 — Getting Started の入口 |
| `benchmarks/` | 性能変更の検証に必要 |
| `tools/Quiver.Mcp/` | MCP サーバ — ユーザーツール |
| `tools/Quiver.SampleDbGen/` | サンプル DB 生成 — デモ/テスト用 |
| `tools/Quiver.Studio/` | GUI ビューア — 視覚的デバッグ |
| `docs/spec/` | エンジン仕様 (as-built) — コントリビュータ必読 |
| `docs/api/` | API ドキュメント |
| `docs/operations/` | 運用ガイド |
| `.github/workflows/` | CI — フォーク先でも動く |

---

## 2. publish スクリプト

`scripts/publish.sh` に配置（scripts/ 自体も公開ブランチからは除外）。

### 処理フロー

```
1. main ブランチへ checkout
2. develop から --no-commit でマージ
   (マージにより plans/ 等が main のインデックスに再追加される)
3. 除外対象を git rm -rf --cached で「インデックスからのみ」除去
   - ディスク上のファイルは消えない (--cached)
   - develop ブランチのコミット履歴にも影響しない
   - 毎回のマージで再追加されるため、毎回この手順を実行する
4. Quiver.slnx から sandbox プロジェクト参照を除去
5. コミット (main のツリーには除外対象が含まれない)
6. develop へ戻る (develop は一切変更されていない)
```

### 実装タスク

- [x] `scripts/publish.sh` を作成
  - マージコンフリクト時は中断して手動解決を促す
  - `--dry-run` オプションで差分プレビューのみ実行
  - 除外リストはスクリプト冒頭の配列で管理 (追加しやすく)
- [x] `Quiver.slnx` から sandbox を除去する sed/処理を組み込む
  - 公開側 slnx に sandbox の ProjectReference が残らないこと
  - develop 側 slnx は変更しない
- [x] `scripts/` を除外リストに含める (自己除外)

### 除外リスト (スクリプト内の定義)

```bash
EXCLUDE_PATHS=(
    "plans/"
    "sandbox/"
    "scripts/"
)
```

---

## 3. ソリューションファイルの整合性

公開ブランチの `Quiver.slnx` から以下を削除:

```
sandbox\QuiverSandbox\QuiverSandbox.csproj
sandbox\RagSandbox\RagSandbox.csproj
```

develop 側では残す。publish スクリプトがマージ後・コミット前に自動処理する。

---

## 4. 公開リポジトリのチェックリスト

外部開発者がクローンして迷わないための最低限。

### 既にあるもの
- [x] `LICENSE` (MIT)
- [x] `README.md`
- [x] `.github/workflows/ci.yml` — CI
- [x] `.github/dependabot.yml`
- [x] `docs/` — API ドキュメント・仕様・運用ガイド
- [x] `samples/` — 複数サンプルプロジェクト

### 確認・対応が必要なもの
- [x] **CONTRIBUTING.md** — 作成済み
  - ビルド手順 (`dotnet build`)
  - テスト実行 (`dotnet test`)
  - ブランチ戦略 (main は安定、PR は develop ベース)
  - コーディング規約 (簡潔に)
- [x] **README.md の点検** — OK
  - CI バッジが公開リポジトリの URL を指している (`delicioustuna/Quiver`)
  - NuGet バッジ (パッケージ公開後に追加)
  - 「Getting Started」がサンプルを正しく参照している
- [x] **CI ワークフローの点検** — OK
  - `ci.yml`: sandbox 依存なし、main/develop 両ブランチ対応
  - `bench.yml`: nightly + workflow_dispatch
  - `release.yml`: タグ push トリガ、secrets は GitHub 設定後に追加
  - `aot.yml`: main/develop + PR 対応
- [x] **docfx.json の点検** — sandbox/plans 参照なし (旧プロジェクト名の残存は別途対応)
- [x] **Directory.Build.props の点検** — OK
  - Authors, RepositoryUrl, PackageLicenseExpression 等すべて公開用に設定済み

---

## 5. 実行タイミング

このタスクはコメント整理 (comment-cleanup-for-publication.md) の完了後に実施する。
コメント整理で全ファイルが更新された状態を develop にコミットしてから、
publish スクリプトで main へ初回同期する。

### 初回 publish の手順

```
1. develop で全コメント整理を完了 & コミット          ✅ 済
2. scripts/publish.sh を作成 & テスト (--dry-run)      ✅ 済
3. main ブランチを作成 (または既存 main をリセット)    ✅ 済 (既存 main を使用)
4. publish.sh を実行                                   ✅ 済 (commit 1469f1c)
5. main の内容を目視確認                               ✅ 済 (ビルド+テスト OK)
6. GitHub にリポジトリを作成 & push                    ⬜ 未実施
```
