# GA Readiness タスク計画

起票日: 2026-06-18
ステータス: 実行待ち

## 概要

GA リリースに向けた品質評価で 12 タスクを起票。
3 カテゴリ: エンジン改修 (1), FTS クエリ構文拡張 (4), テスト拡充 (6), ドキュメント (1)

---

## Wave 1: エンジン改修 + FTS 基盤 (先行)

### GA-1: 並行 writer 排他制御 (opt-in enforcement flag)
- **概要**: `GraphDatabaseOptions.EnforceExclusiveWriter` フラグ追加。`BeginTransaction()` で active writer がいれば `InvalidOperationException` をスロー
- **対象ファイル**: `GraphDatabase.cs`, `GraphDatabaseOptions.cs`, `TransactionManager` 周辺
- **工数**: ~15 LOC, 小
- **実行**: `/quiver-implement GA-1 並行 writer 排他制御`

### GA-2: FTS Prefix クエリ対応 (wildcard)
- **概要**: `g.Search("idx", "quiv*", k)` 形式。`PostingsKey.TermRange()` による B+Tree range scan は既存。QueryParser でワイルドカード認識
- **対象ファイル**: `Bm25Scorer.cs`, `FullTextScanOperator.cs`, `FullTextIndex.cs`, `GraphTraversalSource.cs`
- **工数**: 1-2 日
- **実行**: `/quiver-implement GA-2 FTS Prefix クエリ`
- **前提**: なし

### GA-3: FTS Boolean クエリ対応 (AND/OR/NOT)
- **概要**: `g.Search("idx", "graph AND database NOT vector", k)` 形式。再帰降下 QueryParser + multi-cursor intersection
- **対象ファイル**: 新規 QueryParser, `Bm25Scorer.cs` に RankBoolean 追加, `FullTextScanOperator.cs`
- **工数**: 3-5 日
- **実行**: `/quiver-implement GA-3 FTS Boolean クエリ`
- **前提**: GA-2 (QueryParser 共有)

### GA-4: FTS Fuzzy クエリ対応 (edit-distance)
- **概要**: `g.Search("idx", "quiver~1", k)` 形式。QueryParser で ~N 認識 → Levenshtein 候補生成 → OR 結合
- **対象ファイル**: QueryParser 拡張, `Bm25Scorer.cs`, `FullTextScanOperator.cs`
- **工数**: 2-3 日
- **実行**: `/quiver-implement GA-4 FTS Fuzzy クエリ`
- **前提**: GA-2, GA-3 (QueryParser 共有)

### GA-5: ITokenFilter インターフェース設計
- **概要**: `ITokenFilter` インターフェース定義。Tokenizer → Filter chain → Sink パイプライン構築。Phase 2 Synonym 拡張点
- **対象ファイル**: `ITokenizer.cs` 周辺に新規 `ITokenFilter.cs`, `TokenizerRegistry` 拡張
- **工数**: 1-2 日
- **実行**: `/quiver-implement GA-5 ITokenFilter インターフェース`
- **前提**: なし (GA-2〜4 と独立)

---

## Wave 2: テスト拡充 (並列実行可能)

### GA-6: PhysicalPlanner / LogicalOptimizer 単体テスト
- **概要**: プラン生成パス (IndexSeek 選択, KNN pushdown, Filter 最適化) の単体テスト追加
- **対象**: `tests/Quiver.Operators.Tests/` に新規テストクラス
- **実行**: `/quiver-implement GA-6 PhysicalPlanner テスト`

### GA-7: BM25Scorer 単体テスト
- **概要**: 既知コーパスに対する期待 BM25 スコア値の検証テスト
- **対象**: `tests/Quiver.Tests/` に新規テストクラス
- **実行**: `/quiver-implement GA-7 BM25Scorer テスト`

### GA-8: GraphTraversal DSL / Client API テスト拡充
- **概要**: DSL チェーン構築、Match パターンコンパイル、カーソル反復のテスト拡充
- **対象**: `tests/Quiver.Client.Tests/`
- **実行**: `/quiver-implement GA-8 GraphTraversal テスト`

### GA-9: FTS オペレータ単体テスト
- **概要**: FullTextScanOperator / FilteredFullTextScanOperator / AllRelationshipsScanOperator の isolated テスト
- **対象**: `tests/Quiver.Operators.Tests/`
- **実行**: `/quiver-implement GA-9 FTS オペレータテスト`

### GA-10: EdgeWeightProvider / PageSelectionBitmap テスト
- **概要**: 重み付き最短路・ビットマップフィルタリングの正確性検証
- **対象**: `tests/Quiver.Operators.Tests/`
- **実行**: `/quiver-implement GA-10 EdgeWeight テスト`

### GA-11: Migration エッジケーステスト拡充
- **概要**: ロールバック、スキーマ非互換、重複適用、並行マイグレーション等のエッジケース
- **対象**: `tests/Quiver.Tests/MigrationTests.cs` 拡張
- **実行**: `/quiver-implement GA-11 Migration テスト`

---

## Wave 3: ドキュメント整備

### GA-12: known_limits CRITICAL 項目の周知強化
- **概要**: 08_known_limits.md の CRITICAL 4 件に設計根拠・緩和策・将来方針を追記。README Limitations セクション追加。1.x 互換ポリシー明文化
- **対象**: `docs/spec/08_known_limits.md`, `README.md`, `docs/api-stability.md`
- **実行**: `/quiver-implement GA-12 known_limits ドキュメント整備`

---

## 推奨実行順序

```
セッション 1: Wave 1 前半
  GA-1 (排他制御, 30 min)
  GA-2 (Prefix, 1-2 日)
  GA-5 (ITokenFilter, 並列可, 1-2 日)

セッション 2: Wave 1 後半
  GA-3 (Boolean, 3-5 日)

セッション 3: Wave 1 完了 + Wave 2 開始
  GA-4 (Fuzzy, 2-3 日)
  GA-6〜11 (テスト, サブエージェント並列)

セッション 4: Wave 2 残 + Wave 3
  GA-12 (ドキュメント)
  最終ビルド確認
```

## Phase 2 送り (GA スコープ外)

- Phrase クエリ (位置インデックス構造変更, 2-3 週)
- Synonym 実装 (ITokenFilter 上に構築, 1 週)
- カタログオーバーフロー修正 (チェーンページ, ~300 limit で十分)
- Async API (ValueTask)
- Multi-label ノード
- Composite Index
- Import/Export フォーマット
