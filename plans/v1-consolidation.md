# v1.0 統合・正規化 計画書 (SQLite 撤去 / format 再ベース / 正規設計文書 / 参照移行)

> 起票: 2026-06-15。状態: **実装中 (D✅ C✅ A✅ B🔄)**。
> D (format 再ベース) ✅ / C (SQLite 撤去) ✅ / A (docs/spec/ 正規文書) ✅ / B (参照移行) 実行中。
> 関連: [[quiver-prerelease-no-format-compat]] (クリーンブレイク方針)、FTS-9 停止判断。

## 0. 背景と目的

個人利用 (MIT/OSS)・ゼロ依存 thesis (信頼計算基盤の最小化、実装検証可能性 = セキュリティ統制) の下で、
現時点の実装を **正規の v1 ベースライン**として確定する。具体的には 4 系統:

- **A. 正規設計文書の新規作成** — 現状 `docs/design/` は **丸ごと .gitignore 対象** (下記実証)。
  ソース/`docs/development.md` が untracked な設計文書を参照しており、clone した第三者には dangling になる
  (= ユーザ指摘の「Git 対象とそうでない設計文書が混在」)。現実装を基準とした **tracked な正規文書**を起こす。
- **B. ソース参照の移行** — ソース中の設計文書参照を、新正規文書の該当箇所リンクへ差し替え/降格。
- **C. SQLite 依存の撤去** — `Quiver.Storage.Sqlite` とその配線・テスト・公開面・docs を除去。
- **D. フォーマット履歴の一新** — `FormatVersion` の V1..V9 履歴を畳み、現実装を **format v1** として再宣言。

### 実証済みの現状 (2026-06-15 git 調査)

| 事実 | 値 |
|---|---|
| `.gitignore` | `docs/design`, `docs/benchmarks/`, `docs/roadmap.md` を ignore (design 文書は全て untracked) |
| `git ls-files docs/design/` | 空 (= 14 文書すべて非追跡) |
| `FormatVersion.Current` | **V9** (FTS-9 の V10 bump は撤去済み)。履歴 V1..V9 が constant として存在 |
| format 履歴の分岐利用 | **無し** — FormatVersion.cs 外は全て `FormatVersion.Current` のみ参照 (16 箇所、全 store の header gate)。歴史的 constant (V2..V9) を分岐に使うコードは 0 → 畳み込みは低リスク |
| SQLite footprint | 専用 project 2 本 (`src/Quiver.Storage.Sqlite` 12 ファイル + `tests/Quiver.Storage.Sqlite.Tests` 6) + `Quiver.slnx` 2 エントリ + コア `Quiver` 内の **public backend 抽象** + コメント言及 |
| `IGraphStorageBackend` | **public** インタフェース。`AssemblyAttributes.cs` に `InternalsVisibleTo("Quiver.Storage.Sqlite"[.Tests])`。PublicApi approval baseline に出現 |
| 設計文書参照 (src `*.cs`) | 88 行 (`docs/design`/`design NN`/`§N`)。内訳: `///` XML doc = **60**、`//` 平 = **27** |
| task-id breadcrumb (FTS-/ARCH-/VEC-/FT- 等) | 648 行 (別スコープ判断、§B) |

---

## A. 正規設計文書の新規作成

### A.1 何を作るか
現実装を基準とした **tracked な正規リファレンス**。現 `docs/design/` は *設計の旅程* (繰延・refute・歴史) を
含み「今どうなっているか」と一致しない箇所がある。正規文書は **as-built (実装が正)** で記述する。

### A.2 配置と形 (決定 D-3)
- 既定案: 新ディレクトリ **`docs/spec/`** にサブシステム別の追跡文書群を置く (リンク先として安定 anchor を持てる)。
  旧 `docs/design/` の番号体系を踏襲しつつ tracked・as-built 化:
  ```
  docs/spec/00_overview.md          ポジショニング / 非目標 / TCB 境界 (旧 12 の as-built 版)
  docs/spec/01_storage_paging.md    ページ/バッファプール/STEAL/単一ファイルコンテナ
  docs/spec/02_wal_recovery.md      WAL / ARIES / 2 相 recovery / presume-committed
  docs/spec/03_mvcc.md              version sidecar / 可視性 / abort / savepoint
  docs/spec/04_records_index.md     node/rel/property/columnar / B+Tree / KeyCodec
  docs/spec/05_query.md             Logical IR / Optimizer / Volcano operators / Traversal / Match
  docs/spec/06_vector.md            VectorPayload / 永続 HNSW / KNN / SIMD
  docs/spec/07_fulltext.md          postings/norms / BM25 / WAND / RRF / logical postings WAL
  docs/spec/08_known_limits.md      MVP 制限・既知バグ (本セッション監査の #1〜#5 を集約)
  ```
- `.gitignore` から `docs/design` の除外を **解除しない** (旧文書は untracked な歴史 scratch として残す)。
  代わりに `docs/spec/` を新規 tracked 文書とする。`docs/development.md` の設計文書リンクも `docs/spec/` へ向け直す。
- **重要**: `docs/spec/08_known_limits.md` に本セッションで確定した silent-corruption 監査結果を必ず収録
  (特に #1 recovery Pass-3 loser-undo clobber = HIGH、未修正なら「既知・未修正」と明記)。

### A.3 記述方針
- as-built。繰延/却下された案は「非目標」または「将来候補」として 1 行に留め、旅程は書かない。
- 各 §見出しに安定 anchor (kebab) を付け、ソースからの deep link 先にする。

---

## B. ソース参照の移行

### B.1 ポリシー (ユーザ指示の明文化)
| 参照の種類 | 件数 | 処理 |
|---|---|---|
| 平 `//` コメント内の設計文書参照 | 27 | 新正規文書 (`docs/spec/NN#anchor`) への **相対リンクに差し替え** |
| `///` XML doc コメント内の設計文書参照 | 60 | **リンク削除 → コメント降格** (XML doc から設計参照を除去。必要なら直前/同位置の `//` 平コメントに事実だけ残す) |
| task-id breadcrumb (FTS-7 等、設計文書パス無し) | 648 | **本タスク対象外** (決定 D-4 で確定。既定: 据え置き) |

### B.2 降格の具体例
```csharp
// before (XML doc に設計参照)
/// <summary>FTS-7 (design 13 §10.6): leaf 論理 undo を逆順適用する。</summary>
// after (XML doc から設計参照を除去 = コメント降格)
/// <summary>leaf 論理 undo を逆順 (LIFO) に適用する。</summary>
// 仕様: docs/spec/02_wal_recovery.md#logical-undo
```
```csharp
// before (平コメントの設計参照)
// FTS-7 (design 13 §10.4): recovery 論理相。
// after (正規文書リンクへ差し替え)
// recovery 論理相。仕様: docs/spec/02_wal_recovery.md#two-phase-recovery
```

### B.3 注意
- XML doc は生成 API ドキュメントに出る面なので、内部設計参照を載せない (= 降格の根拠)。
- 88 箇所は機械的だが anchor 対応付けが要るため、`docs/spec/` 確定後に一括実施。
- `FormatVersion.cs` 内の `(design 13 §9.2/§10)` 等も対象 (C で大半が消える)。

---

## C. SQLite 依存の撤去

### C.1 削除対象
- `src/Quiver.Storage.Sqlite/` (project 丸ごと、12 ファイル) / `tests/Quiver.Storage.Sqlite.Tests/` (6)。
- `Quiver.slnx` の 2 エントリ。
- `src/Quiver/AssemblyAttributes.cs` の `InternalsVisibleTo("Quiver.Storage.Sqlite"[.Tests])`。

### C.2 backend 抽象の扱い (決定 D-2)
`IGraphStorageBackend` は **public** で binary/sqlite の 2 実装を支えてきた。SQLite 撤去後は binary が唯一実装。
- **既定案 (低リスク)**: 抽象は public のまま残し、SQLite 実装のみ除去。binary 単一実装。公開面の破壊なし。
  将来 backend を足す余地を残す。「単一実装の抽象 = dead generality」だが本タスクの scope を限定できる。
- **代替案 (clean v1)**: `IGraphStorageBackend` を畳み binary を直結。public API 破壊 (v1 surface が締まる)。
  churn 大。simplify/TCB 最小化 thesis には沿うが、別タスク化推奨。

### C.3 連鎖修正
- backend 契約テスト (`tests/Quiver.Backend.Tests/GraphStorageBackend*ContractTests.cs`) は両 backend を回す
  パラメタ化。**binary 専用**へ縮約。`SqliteFullTextNotSupportedTests` 等は削除。
- コア `Quiver` 内のコメント言及 (約 10 ファイル、「SQLite backend では…」) を削除/更新。
- `Quiver.Rag` / `Quiver.Embedding` のコメント言及を更新。
- **PublicApi approval baseline 再生成** (`tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt`):
  InternalsVisibleTo 2 行除去 + (D-2 代替案を採る場合) 抽象除去差分。
- docs 更新: `docs/development.md` (パッケージ表/依存図/backend 節), `docs/api/concepts/backends.md`,
  `docs/cookbook.md`, `docs/operations/{01,02,05}*`, `docs/api-stability.md` の SQLite 記述を除去。

---

## D. フォーマット履歴の一新 (format v1 再宣言)

### D.1 変更
- `FormatVersion.cs`: V1..V9 の歴史 constant と各 XML 履歴を畳み、**`Current = V1 = 1`** に再ベース。
  ```csharp
  internal static class FormatVersion
  {
      /// <summary>v1: Quiver 1.0 ベースライン format (pre-1.0 の format 履歴はクリーンブレイクで畳んだ)。</summary>
      public const byte V1 = 1;
      /// <summary>現行 (= 新規 DB 作成時に書き込む)。</summary>
      public const byte Current = V1;
  }
  ```
- `FormatVersionMismatchException` のメッセージから "FT-26 MVCC" 等の歴史語を除去し、汎用文言へ。
- `tests/Quiver.Tests/IndexGenerationTests.cs` の `FormatVersion_current_is_v9` → `..._is_v1` に更新
  (歴史コメントの羅列も整理)。
- 低リスク: 分岐利用が無いため、これ以外の store コードは無改修 (全て `Current` 参照)。

### D.2 影響
- on-disk version byte が 1 になり、既存ローカル `*.quiver` は全て reject (= 意図どおり、未リリース・無移行)。
- byte 値 1 は歴史的に「pre-MVCC 旧 format」を指したが、クリーンブレイクのため衝突は実害なし
  (旧 DB は存在しない)。

---

## E. 製品バージョン 1.0.0 宣言の有無 (決定 D-1)

「現時点のコードをバージョン1として宣言」が **format v1 のみ**か、**製品 SemVer 1.0.0 まで**かで作業が変わる:
- **format のみ (既定)**: D のみ。`Directory.Build.props` の `VersionPrefix` (現 0.1.0) は据え置き or 任意。
- **製品 1.0.0 も**: `VersionPrefix=1.0.0`、README の「pre-release v0.1.0」更新、`docs/api-stability.md` の
  「0.x は無保証」節を改訂、**PublicApi approval baseline を v1 公開契約として凍結**。これは
  本セッションで「唯一の不可逆ステップ」と指摘した API 安定化コミットに相当 (= 重い・要熟慮)。

---

## 実施順序 (承認後)
1. **D (format 再ベース)** — 最小・独立。ビルド/テスト緑を確認。
2. **C (SQLite 撤去)** — project 削除 → 連鎖修正 → PublicApi 再生成 → docs。ビルド/全テスト緑。
3. **A (正規文書 `docs/spec/` 起稿)** — as-built。anchor 確定。
4. **B (参照移行)** — `docs/spec/` 確定後に 88 箇所を一括 (平→リンク / XML→降格)。
5. (D-1 が 1.0.0 の場合) **E** — version/README/api-stability/baseline 凍結。
6. 完了承認用サマリ作成。

## リスクと注意
- C の PublicApi baseline 再生成は意図せぬ public 差分を露呈しうる → diff を必ずレビュー。
- A は分量大 (8 文書)。as-built の正確性が肝 → 各 §は実コードを引いて書く (推論で埋めない)。
- B の XML 降格は 60 箇所。生成 API ドキュメントの文面が変わるので、降格後に `docfx`/doc ビルドがあれば確認。
- 本タスク中に未修正の監査 #1 (recovery clobber) は `docs/spec/08_known_limits.md` に「既知・未修正・HIGH」と
  明記し、握りつぶさない (検証可能性 = セキュリティ統制の原則)。

## 完了条件
- `docs/design` を参照する dangling リンクがソース/tracked docs に残らない。
- `Quiver.Storage.Sqlite` 関連の project/コード/テスト/公開面/docs が消え、`dotnet build` + 全テスト緑。
- `FormatVersion.Current == 1`、version-gate テスト緑。
- `docs/spec/` が tracked で、ソースの設計参照がそこへ向く (平=リンク / XML=降格)。
- (D-1=1.0.0 の場合) VersionPrefix=1.0.0 + API baseline 凍結 + README/api-stability 改訂。

## 着手前に要確定の決定 (D-1〜D-4)
- **D-1**: 「v1 宣言」= format のみ / 製品 1.0.0 も含む。
- **D-2**: backend 抽象 = SQLite だけ除去し抽象は public 維持 (既定) / 抽象も畳む。
- **D-3**: 正規文書 = `docs/spec/` 複数 (既定) / 単一 `docs/architecture.md`。旧 `docs/design/` = 残す (既定) / 削除。
- **D-4**: 参照移行スコープ = 設計文書参照 88 のみ (既定) / task-id 648 も対象。
