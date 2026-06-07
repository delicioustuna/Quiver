# FT-35 — CLR プリミティブ型のクエリ対応 (正準・順序保存エンコード)

> 作成: 2026-06-07 / 対象ブランチ: develop
> 起点: GC-7 (式ツリー述語) で double 範囲・各種 CLR 型が `NotSupportedException` になることが判明。
> 方針: ユーザ承認済み「薄い物理層 + 賢いエッジ」。C# ユーザに自然な CLR 型をクエリ述語で扱えるようにする。

## 進捗

- **増分1 ✅ 完了 (commit `efeb3d2`)**: 浮動小数点 (double/float/Half) の範囲クエリ。`P.*(double)` +
  `PropertyDoubleRangePredicate` (復号比較=option b、format 不変) + GC-7 を**メンバ CLR 型 routing**へ +
  `Has`/source-gen に float/Half (Double widen 格納)。Quiver.Tests 490 緑。
- **増分2 ✅ 完了 (commit `ce0c802`)**: DateTime 系 (DateTime/DateTimeOffset/DateOnly/TimeOnly/TimeSpan) を
  Int64 で round-trip + long 範囲。`TemporalCodec` に正準化集約。**TimeZone 契約**: 可搬性のため
  Unspecified を UTC 扱い (Local 扱いしない)、Local は UTC へ変換、復元は Utc Kind、DateTimeOffset は
  UtcTicks に畳む。Quiver.Tests 492 緑。
- **増分3 ✅ 確認済み (実装は既存、検証テスト追加)**: 索引 range の OrderedFloat エンコードは
  **FT-35 以前から実装済みだった**。`DoubleKeyCodec` ([KeyCodecs.cs](../src/Quiver/Index/KeyCodecs.cs))
  が total-order 変換 (符号ビット反転 + 負値は全ビット反転) を実装済で、`RangeIndex` の Double 経路
  ([GraphTransaction.cs](../src/Quiver/GraphTransaction.cs)) が `CreateDoubleIndex` (順序保存) を range-seek
  する。**format bump 不要**。負値をまたぐ double range シークの回帰テストを追加して固定
  (`NodeIndexRangeScanOperatorTests.Double_range_orders_across_negative_boundary`)。
  - **残る任意の最適化 (FT-35 スコープ外・PW 系)**: optimizer が `Where(p => p.Score > x)` の数値 range 述語を
    full-scan + filter ではなく `NodeIndexRangeScanOperator` (索引 range) に押し下げる選択。正確性は増分1-2 で
    達成済のため、これは純粋な性能最適化。`IndexKind` に数値 Range 種別を露出し optimizer を配線する別タスク。

**結論**: FT-35 は increments 1-3 で **CLR 型のクエリ正確性 (float/Half/double/DateTime系の比較・範囲) を達成**。
索引 range の順序保存も既存実装で担保済み。数値 range 述語の索引押し下げ (性能) は別 PW タスクへ。

## 目的

`Where(p => p.When > someDateTime)` / `Where(p => p.Score > 1.5)` のような、C# ユーザに自然な
プリミティブ型 (浮動小数点・日時等) の**比較・範囲述語**をクエリ層で動かす。物理プロパティ型や
述語クラスを CLR 型ごとに増やさず、**境界 (source-gen / 型付き DSL) で正準符号化**して既存の
long/string ベース述語に載せる。

## 原則: 物理型は増やさず、型知識をエッジに寄せる

Quiver の物理プロパティは実質 `long スカラ + 任意の byte 列`。CLR 型ごとに `PropertyValueType` や
述語を増やすと物理層・index・WAL・predicate dispatch に波及して肥大する。物理ドメインを少数に
固定し、CLR 型は書き込み・述語構築の境界で canonical encoding して載せる。

## 物理ドメイン分類

| 物理ドメイン | 該当 CLR 型 | 符号化 | 範囲述語 |
|---|---|---|---|
| **SignedLong** | `byte/sbyte/short/int/long`、`char`、`enum`、`DateTime`(UTC Ticks)、`TimeSpan`(Ticks)、`DateOnly`(DayNumber)、`TimeOnly`(Ticks) | 自然に long、単調 | ✅ 既存 long 範囲がそのまま効く |
| **OrderedFloat** | `Half`/`float`/`double` | IEEE ビット → total-order 変換 `bits ^= (bits>>63) \| long.MinValue` | ✅ 変換後は long 範囲で正しく順序づく |
| **Unsigned64** | `uint`(>int域)、`ulong` | long 格納 + 符号フラグ | △ unsigned 比較フラグが要る |
| **Bytes/String (等値のみ)** | `string`、`Guid`、`decimal` | UTF-8 / 16byte / 文字列 | ❌ 範囲不可、等値・テキストのみ |

## 各論と判断

- **Half / float** — OrderedFloat の total-order 変換を実装すれば**同じ変換で double/float/Half が一括対応**
  (Half→float→double は無損失)。型ごとの述語は不要。「double 範囲」はこの変換 1 本で片付く。
- **DateTime 系** — Ticks/DayNumber が単調なので追加の数学なしで long 範囲が即動く。唯一の論点は
  **正準化**: `DateTimeOffset` は UTC Ticks に正規化して格納し tz は別プロパティ or 捨てる。
  「格納・述語・index で一致」させる契約を **1 箇所** で定義することが肝。
- **decimal** — ⚠️ **double に黙って落とさない** (精度欠落)。等値のみ (文字列/byte ドメイン) か、
  スケール付き整数 `(scale, unscaledLong)` 専用ドメインかは別途判断。安易な double 化は禁止。
- **Guid** — 範囲に意味が薄い。16byte 等値のみ。
- **char / enum** — long に自明に載る。trivial。

## 結論 (実装方針)

1. **物理型は `{Int64, Double, String, Bool, Bytes}` に留める**。CLR 型ごとの `PropertyValueType` /
   述語は増やさない。
2. **CLR→物理の正準・順序保存コーデックを 1 箇所** (例 `PropertyCodec`) に定義し、
   **store / index / 述語構築 (GC-7 `ExpressionPredicate`) の三者が同じコーデックを参照**。
   一致しないと範囲検索が壊れるため単一実装が必須。
3. **OrderedFloat の total-order 変換**を入れて Half/float/double を一気に範囲対応 (本タスクの本体)。
4. **DateTime 系**は Ticks 単調性で範囲が即動く。正準化契約だけ決める。
5. **decimal/Guid** は等値限定を明示 (範囲は `NotSupportedException` + 理由)。round-trip 型復元は
   型付き `Load()` が担うので物理層は型を知らなくてよい。

## 着手時の読むべきファイル

- `src/Quiver/Client/P.cs` (述語ファクトリ。`P.Gt(long)` に OrderedFloat/型対応の overload or 経路を追加)
- `src/Quiver/Client/Internal/ClientPredicates.cs` (`PropertyInt64Predicate`/`PropertyDoublePredicate`。
  範囲対応の double 述語、または OrderedFloat 変換後 long 述語へ畳む)
- `src/Quiver/Client/Internal/ExpressionPredicate.cs` (GC-7。メンバ CLR 型 → ドメイン → コーデック)
- `src/Quiver/Stores/InlinePropertyCodec.cs` / `ColumnManager.cs` / `src/Quiver/Index/KeyCodecs.cs`
  (格納・index の符号化。OrderedFloat 変換を一致させる)
- `src/Quiver/Stores/IPropertyStore.cs` (`PropertyValue.FromXxx`)

## 完了条件 (案)

- `Where(p => p.Score > 1.5)` (double/float/Half)、`Where(p => p.When >= dt)` (DateTime 系) が動く。
- store / index / 述語が同一コーデックを共有し、範囲検索が正しい (負の浮動小数点を含む property-based test)。
- decimal/Guid 範囲は明示的に `NotSupportedException`。
- FormatVersion: 新エンコードを入れる場合は bump (develop ゆえマイグレーション不要)。
