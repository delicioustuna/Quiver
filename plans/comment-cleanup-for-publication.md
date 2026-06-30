# コメント整理 — GitHub 公開向け総点検

## 目的

GitHub 公開にあたり、全ソースの XML doc (`///`) とソースコメント (`//`) を
外部開発者が読んで意味の通る状態に整理する。

## 共通ルール（全タスク共通）

### やること

1. **内部タスク番号の除去**
   `FT-`, `ARCH-`, `VEC-`, `SIG-`, `PW-`, `BA-`, `GC-`, `TS-`, `RAG-` 等の
   内部追跡 ID をコメントから削除する。番号を除いた後の文が意味をなすよう文を再構成する。
   - 例: `/// FT-33 SSN (Serial Safety Net) の簡易スモークテスト。` → `/// SSN (Serial Safety Net) の簡易スモークテスト。`
   - 例: `/// PW-12: BitmapFilterOperator + PageSelectionBitmap.` → `/// BitmapFilterOperator と PageSelectionBitmap の結合テスト。`

2. **内部ドキュメント参照の除去**
   `codex_advice_3`, `codex_advice` 等、公開リポジトリに含まれない文書への参照を削除する。
   参照を除いた後の文が自立するよう書き直す。
   - 例: `/// BA-6 / codex_advice_3 §7.2: V2 adjacency view with an inline payload lane`
     → `/// V2 隣接ビューの inline payload レーン検証。`

3. **コメントの日本語化**
   英語のコメント (`///` および `//`) を日本語に翻訳する。
   public API の `<summary>` は利用者向けの簡潔な日本語にする。
   技術用語 (WAL, MVCC, HNSW, BFS, KNN, B-tree, checkpoint 等) はそのまま英語で残してよい。

4. **開発者向けコンテキストの補足**
   コードの「なぜ」が非自明な箇所に、簡潔な日本語コメントを追加する。
   - なぜこの設計にしたか (トレードオフ、制約)
   - 初見で驚くかもしれない挙動
   - パフォーマンス上の理由
   ただし「何をしているか」はコード自体から明らかなので書かない。

5. **ファイルリネーム**
   `tests/Quiver.Client.Tests/UnitTest1.cs` → `AttributesTest.cs` にリネーム
   (Task F で実施)

### やらないこと

- コードのロジック変更 (リファクタリング含む)
- テストの追加・削除
- `docs/` 配下の仕様書への変更
- `CLAUDE.md`, `MEMORY.md` への変更
- コメント以外の場所にある文字列リテラル (テストデータ等) の変更
- 既に適切な日本語コメントが書かれている箇所の書き直し
- XML doc の `<param>`, `<returns>`, `<exception>` 等の追加（既存のものの翻訳のみ）

### 品質チェック

各タスク完了後、`dotnet build` でビルドが通ることを確認する。

---

## タスク分割

### Task A: src/Quiver — ストレージ・WAL・Codec・Backend (~84 files)

**対象ディレクトリ:**
- `src/Quiver/Stores/` (43 files)
- `src/Quiver/Storage/` (16 files, SingleFile/ 含む)
- `src/Quiver/Backend/` (11 files)
- `src/Quiver/Wal/` (11 files)
- `src/Quiver/Codec/` (3 files, Schema/ 含む)

**注意点:**
- Stores はレコード層 (NodeStore, RelationshipStore, PropertyStore 等) でファイル数最多。
  各ストアの `///` にページ構造やスロット配置の設計意図を補足すると有用。
- Wal / Codec はバイナリフォーマットに関わるので、
  エンディアン・チェックサム・ページサイズ等の制約をコメントに残す。
- Backend は `BinaryGraphStorageBackend` — クラッシュ安全性の保証をコメントに明記。

---

### Task B: src/Quiver — クエリ・インデックス・テキスト (~73 files)

**対象ディレクトリ:**
- `src/Quiver/Operators/` (47 files)
- `src/Quiver/Query/` (3 files, Logical/ 含む)
- `src/Quiver/Logical/` (6 files)
- `src/Quiver/Index/` (9 files, FullText/ 含む)
- `src/Quiver/Text/` (8 files)

**注意点:**
- Operators はクエリ実行の物理演算子。各演算子の `///` に
  「何を入力に取り、何を出力するか」のパイプライン上の位置づけを書くと有用。
- Index/FullText は B-tree + 転置索引。BM25 スコアリング、WAND 最適化等の
  アルゴリズム選択理由をコメントに残す。
- Text はトークナイザ/フィルタチェーン。日本語混合バイグラムの設計意図を補足。

---

### Task C: src/Quiver — コア・API・トランザクション (~118 files)

**対象ディレクトリ:**
- `src/Quiver/Core/` (23 files, Telemetry/ 含む)
- `src/Quiver/Client/` (24 files, Internal/, Match/ 含む)
- `src/Quiver/Transactions/` (23 files)
- `src/Quiver/Migrations/` (5 files)
- `src/Quiver/Maintenance/` (4 files)
- `src/Quiver/` ルート直下のファイル (GraphDatabase.cs 等)

**注意点:**
- Core は公開型 (NodeId, PropertyValue, VectorIndexSpec 等) が多い。
  `<summary>` は利用者向けに「何のための型か」を簡潔に。
- Client は Fluent API (GraphTraversal, Match パターン)。
  メソッドチェーンの各ステップが何をするか `///` で説明。
- Transactions は MVCC/SSN の心臓部。可視性ルール・直列化検証の設計判断を補足。
- GraphDatabase.cs はエントリポイント。Open/Close/Dispose のライフサイクルを明記。

---

### Task D: 衛星パッケージ + samples + tools (~105 files)

**対象ディレクトリ:**
- `src/Quiver.Hosting/` (2 files)
- `src/Quiver.Rag/` (12 files)
- `src/Quiver.Embedding/` (8 files)
- `src/Quiver.OpenTelemetry/` (1 file)
- `src/Quiver.SourceGen/` (6 files)
- `samples/` (13 files — 全サンプルプロジェクト)
- `tools/` (63 files — Quiver.Mcp, Quiver.SampleDbGen, Quiver.Studio)

**注意点:**
- samples はユーザーが最初に読むコード。
  各サンプルの冒頭コメントに「何を示すサンプルか」を明確に書く。
  `Program.cs` の手順ごとに簡潔な `//` コメントを付けると初見で追いやすい。
- tools/Quiver.Studio は Avalonia GUI。UI 層のコメントは操作フローを補足。
- tools/Quiver.Mcp は MCP サーバ。ツール定義の description は英語のまま残してよい
  (MCP クライアントが読む文字列のため)。ただしソースコメントは日本語化。
- SourceGen はコンパイル時生成器。Roslyn API 利用の制約・前提をコメントに残す。

---

### Task E: tests/Quiver.Tests + tests/Quiver.Operators.Tests (~126 files)

**対象ディレクトリ:**
- `tests/Quiver.Tests/` (83 files — メイン統合テスト)
- `tests/Quiver.Operators.Tests/` (43 files — 演算子ユニットテスト)

**注意点:**
- テストの `///` にはタスク番号 + codex_advice 参照が集中している。
  番号を消して「何の機能 / どのバグの回帰テストか」を日本語で書き直す。
- テストメソッド名は英語のまま (xunit の出力に影響するため変更しない)。
- テスト本体の `//` コメントはテストの意図 (arrange/act/assert の区切り) が
  伝わる程度に。日本語で書く。
- `Quiver.Tests/Text/` サブフォルダも対象。

---

### Task F: tests/ 残り 14 プロジェクト (~76 files)

**対象ディレクトリ:**
- `tests/Quiver.Backend.Tests/` (19 files — Chaos/Fault テスト)
- `tests/Quiver.Stores.Tests/` (14 files)
- `tests/Quiver.Client.Tests/` (8 files) ← **UnitTest1.cs → AttributesTest.cs リネーム**
- `tests/Quiver.Transactions.Tests/` (8 files)
- `tests/Quiver.FuzzTests/` (6 files)
- `tests/Quiver.PropertyTests/` (6 files)
- `tests/Quiver.Rag.Tests/` (5 files)
- `tests/Quiver.Index.Tests/` (3 files)
- `tests/Quiver.Storage.Tests/` (2 files)
- `tests/Quiver.Codec.Tests/` (1 file)
- `tests/Quiver.Hosting.Tests/` (1 file)
- `tests/Quiver.PublicApi.Tests/` (1 file)
- `tests/Quiver.SourceGen.Tests/` (1 file)
- `tests/Quiver.Wal.Tests/` (1 file)

**注意点:**
- Backend.Tests/Chaos/ はカオステスト基盤。FaultKind, ChaosScenario 等の
  設計意図 (どんな障害を注入するか) をコメントに残す。
- PropertyTests は FsCheck 系。プロパティの不変条件を `///` で明文化。
- FuzzTests はファジング対象。シード選定理由をコメントに残す。
- **UnitTest1.cs のリネーム**: `tests/Quiver.Client.Tests/UnitTest1.cs` を
  `AttributesTest.cs` にリネームする。csproj は明示的なファイル参照がないので変更不要。
  `git mv` でリネームし、名前空間はそのまま (`Quiver.Api.Tests`)。
