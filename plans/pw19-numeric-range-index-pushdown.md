# PW-19 — 数値 range 述語の索引押し下げ

> 作成: 2026-06-08 / 対象ブランチ: develop
> 起点: FT-35 増分3。浮動小数点/整数の range クエリは正確に動く (filter path) が、索引 (B+Tree)
> range シークへ押し下げていないため大規模データで full-scan になる。これは**性能最適化のみ**
> (正確性は FT-35 で達成済)。

## 目的

`g.Nodes<Person>().Where(p => p.Score > 1.5)` や `.Has("score", P.Gt(1.5))` のような
**数値 range 述語**を、対象プロパティに B+Tree 索引があるとき full-scan + filter ではなく
`NodeIndexRangeScanOperator` (索引 range シーク) に押し下げる。

## 既に存在するもの (FT-35 で確認済)

- `DoubleKeyCodec` / `Int64KeyCodec` は **order-preserving** (負値も正しく順序づく)。
- `NodeIndexRangeScanOperator` + `IGraphTransaction.RangeIndex` (Int64/Double/String) は実装済。
- 索引作成: `IndexManager.CreateDoubleIndex` / `CreateInt64Index` (range-capable)。
- 文字列は `IndexKind.StringRange` + optimizer 押し下げが既にある (参考実装)。

## 不足しているもの (本タスクのスコープ)

1. **`IndexKind` に数値 Range 種別を露出**: `Int64Range` / `DoubleRange` (現状は Equality のみ。
   物理的には同じ B+Tree なので `SchemaApi.CreateIndex` の wiring とメタデータ追加が主)。
   - `Int32Range` は Int64 索引に畳める。
2. **optimizer 押し下げ**: `LogicalOptimizer` / `PhysicalPlanner` が、indexed プロパティへの
   `P.Gt/Gte/Lt/Lte/Between` (および GC-7 `Where` 由来の範囲) を `NodeIndexRangeScanOperator` に
   変換する rule。`GraphStats.HasNumericRange` / `HasDoubleRange` を cost 判定に使う。
   StringRange の既存押し下げ経路を一般化するのが筋。
3. **source-gen / EnsureIndex**: 数値 Range 索引を宣言できるよう `_indexKindMap` 等を拡張
   (任意。Equality 索引でも range-scan は可能なので必須ではない)。
4. **片側範囲の境界**: `P.Gt(x)` は上限なし (`+∞`) 等の片側 range を RangeIndex に渡す扱いを整える
   (現 `RangeIndex` は from/to 両端前提)。`double.NegativeInfinity`/`PositiveInfinity`・
   `long.MinValue`/`MaxValue` を番兵に使う。

## 読むべきファイル

- `src/Quiver/Index/KeyCodecs.cs` (order-preserving 確認済)
- `src/Quiver/Operators/NodeIndexRangeScanOperator.cs` / `src/Quiver/GraphTransaction.cs` (`RangeIndex`)
- `src/Quiver/ISchemaApi.cs` (`IndexKind`) / `src/Quiver/SchemaApi.cs` (`CreateIndex`)
- `src/Quiver/Query/Optimizer/*` (`LogicalOptimizer` / `PhysicalPlanner`、StringRange 押し下げの既存経路)
- `src/Quiver/GraphStats.cs` (`HasNumericRange` / `HasDoubleRange`)
- 参考テスト: `tests/Quiver.Operators.Tests/NodeIndexRangeScanOperatorTests.cs`
  (`Double_range_orders_across_negative_boundary` で encoding は担保済)

## 完了条件 (案)

- indexed な数値プロパティへの range 述語が `NodeIndexRangeScanOperator` を使う
  (plan 検査 or PW-18 系 OptimizerPlanRegression sentinel で確認)。
- 非 indexed や cost 不利のときは従来の filter にフォールバック。
- 片側範囲 (`> x` のみ) も索引 range で動く。
- 結果は filter path と一致 (回帰)。bench で full-scan 比の改善を実測。
- オンディスク不変 (encoding 既存)、format bump 不要。
