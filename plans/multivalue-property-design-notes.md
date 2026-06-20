# マルチバリュープロパティ 設計メモ

> 2026-06-19 作成。SIG トラック完了後の検討材料。実装判断は未確定。

## 動機

タグ・ラベル集合のような「1 キー = N 値」のユースケース。
例: `("tags", ["sensor", "outdoor", "v2"])`。
Poseidon (arxiv 2510.11166) の SPOI モデルがヒント — 同一 (S, P) に複数 O を持てる。

## 重要な発見: 現行ストレージはほぼ対応済み

PropertyStore のプロパティチェーン（inline + overflow）は MVCC 版管理のために
**同一 KeyId の複数エントリを既に許容**している:

```
SetProperty の動作:
  1. チェーンを走査し、同一 keyId の visible エントリに xmax スタンプ (論理削除)
  2. 新エントリを head に prepend (xmin = currentTx)
→ 結果として常に 1 つだけ visible = 単一値セマンティクス
```

マルチバリューに必要な変更は「xmax スタンプせずに prepend する `AddPropertyValue`」
を足すだけ。PropertyStore のレコードフォーマット (41 bytes) も
PropertyEnumerator も変更不要。

## 設計案

### 案 A: 同一チェーン・異なるセマンティクス (推奨)

ストレージ層の変更なし。API / スキーマ層の拡張のみ。

**スキーマ**: PropertyKeyId にマルチバリューフラグを追加。

```csharp
db.Schema.GetOrCreatePropertyKey("tags", multiValued: true);
```

**API** (IGraphTransaction):

```csharp
// 新規 — マルチバリュー専用
void AddPropertyValue(NodeId nodeId, string key, in PropertyValue value);
void RemovePropertyValue(NodeId nodeId, string key, in PropertyValue value);
PropertyValueEnumerator GetPropertyValues(NodeId nodeId, string key);

// 既存 — 単一値キーで呼ぶ。マルチバリューキーに SetProperty したら例外
void SetProperty(NodeId nodeId, string key, in PropertyValue value);
PropertyValue GetProperty(NodeId nodeId, string key);
```

**内部実装**:

- `AddPropertyValue`: チェーン head に prepend (xmax スタンプなし)
- `RemovePropertyValue`: 同一 key+value の visible エントリを走査し xmax スタンプ
- `GetPropertyValues`: PropertyEnumerator で同一 keyId の全 visible エントリを列挙
- MVCC: 各 Add/Remove は独立した xmin/xmax → スナップショット分離が自然に効く

**B+Tree インデックス**:

- 要素単位でインデックス。`Has("tags", "sensor")` は既存 B+Tree ルックアップ
- AddPropertyValue → インデックスに 1 エントリ追加
- RemovePropertyValue → インデックスから 1 エントリ削除
- 既存のインデックス機構がそのまま動作

**TypedGraphTraversal**:

```csharp
[Node("Sensor")]
public partial class Sensor
{
    [Property] public string Site { get; set; } = "";
    [Property] public List<string> Tags { get; set; } = [];   // SourceGen がマルチバリュー認識
    [Property] public float[] Waveform { get; set; }           // SIG: ベクトル (単一値)
}

// 包含チェック — B+Tree で効率的
g.V<Sensor>().Has(s => s.Tags, "outdoor").ToList();

// 全タグ取得
g.V<Sensor>().Values<List<string>>(s => s.Tags);
```

**SourceGenerator**:

- `List<T>` / `IReadOnlyList<T>` 型を見てマルチバリュー認識
- Insert: 各要素で `AddPropertyValue` を emit
- Load: `GetPropertyValues` → `List<T>` に collect
- Update: 旧値セットと新値セットを diff → 差分 Add/Remove (or clear + re-add)
- 要素型は `_typeMap` にある型のみ (string, int, long, double, bool)

**利点**:
- ストレージフォーマット変更なし
- MVCC が自然に動く (各要素が独立バージョン管理)
- B+Tree インデックスがそのまま動く (要素単位インデックス)
- `Has()` の包含クエリが最も自然な形で効く
- WAL への影響なし (各 Add/Remove は通常の property mutation)

**欠点**:
- 全タグ取得はチェーン走査 (O(プロパティ数))。ただしタグは少数の前提
- 順序保証なし (集合セマンティクス、リストではない)
- 重複検知はユーザー責務 (or AddPropertyValue 時にチェック → 遅い)

### 案 B: 配列型 (StringArray = 8 等)

FloatArray = 7 と同じパターンで StringArray, IntArray 等を追加。

```csharp
PropertyValueType.StringArray = 8,
PropertyValueType.IntArray = 9,
// ...
```

**利点**: 既存の 1 key = 1 value モデル内。順序保持。

**欠点**:
- タグ 1 つの追加・削除が read-modify-write (全要素読み → 変更 → 全要素書き)
- B+Tree インデックスが要素単位で効かない (配列全体がインデックス値)
  → `Has("tags", "sensor")` に対し全配列をスキャンする特殊ロジックが必要
- PropertyValueType の枠を大量消費
- 型コード爆発: string[], int[], long[], double[], bool[] で 5 枠

### 案 C: エッジベースモデリング (現行回避策)

`(:Sensor)-[:TAGGED]->(:Tag {name: "outdoor"})` のようにグラフ構造で表現。

**利点**: エンジン変更不要。タグがファーストクラスエンティティ。
**欠点**: 単純なタグに対してヘビーウェイト (ノード + エッジ / 件)。

## 推奨

**案 A** (同一チェーン・異なるセマンティクス)。

- ストレージ変更なし → リスク最小
- B+Tree インデックスが最も自然に効く (タグ検索のメインユースケース)
- MVCC が無料で付いてくる
- SIG-3 の PropertyValueType/SourceGenerator 変更と干渉しない
  (SIG は FloatArray = 7 追加、こちらは PropertyKeyId フラグ追加。直交)

## SIG トラックとの関係

- **直交**: SIG の `FloatArray = 7` は「1 key = 1 value (配列全体が値)」。
  マルチバリューは「1 key = N values (各値がスカラー)」。概念が異なる
- **SourceGenerator の変更箇所は重複**: `_typeMap` + Insert/Load/Update の emit ロジック。
  SIG-3 と同時にやらないほうが安全 (逐次)
- **B+Tree インデックスは互換**: `float[]` は B+Tree 対象外 (VectorStore 側)、
  マルチバリュー string/int は既存 B+Tree で要素単位インデックス

## Cardinality モデル (確定方針)

値型と直交する概念として `PropertyCardinality` を定義する。
PropertyKeyId が `(name, cardinality)` を持ち、エンジンが制約を強制する。

```csharp
public enum PropertyCardinality : byte
{
    Single = 0,  // 既定。1 key = 1 value（現行動作）
    Set    = 1,  // 1 key = N values、重複なし、順序なし
}
```

- `List`（順序保持・重複許可）は定義しない。順序が要るデータはグラフ構造
  （エッジ + 順序プロパティ）で表現するのが自然。時系列も同様。YAGNI。
- Cardinality はスキーマ宣言時に固定: `db.Schema.GetOrCreatePropertyKey("tags", cardinality: PropertyCardinality.Set)`
- `Single` キーに `AddPropertyValue` → 例外
- `Set` キーに `SetProperty` → 例外
- `Set` + 重複値の `AddPropertyValue` → エンジンがチェーン走査で検知しスキップ

SourceGenerator は C# 型から推論:

```csharp
[Property] public string Site { get; set; }       // → Single
[Property] public List<string> Tags { get; set; }  // → Set (List<T> 検出)
```

`IReadOnlySet<T>` ではなく `List<T>` を使うのは C# の慣用性。
ただしセマンティクスは集合 (順序非保証・重複なし)。XML doc で明記。

## タスク列

| ID | 内容 | 依存 | 状態 |
|---|---|---|---|
| MV-1 | `PropertyCardinality` enum + スキーマカタログ永続化 | — | ✅ |
| MV-2 | `AddPropertyValue` / `RemovePropertyValue` / `GetPropertyValues` API | MV-1 | ✅ |
| MV-3 | B+Tree インデックス対応 (要素単位インデックス + 包含クエリ) | MV-1, MV-2 | ✅ |
| MV-4 | SourceGenerator `List<T>` 対応 | MV-2, SIG-3 後 | ✅ |
| MV-5 | TypedGraphTraversal + サンプル + ドキュメント | MV-1〜4 | ✅ |

並列性: MV-1 は独立着手可。MV-2 → MV-3 は逐次。MV-4 は SIG-3 完了後。
詳細手順: `.claude/skills/quiver-implement/tasks/multivalue.md`

## 解決済み事項

1. **`Has()` のセマンティクス**: 同じ `Has()` で扱う (MV-5 で確定)。
   型付き `Has(s => s.Tags, "outdoor")` は `List<TElem>` 専用オーバーロードが
   コンパイル時に解決し、内部では既存の untyped `Has(key, value)` に委譲する。
   B+Tree は要素単位でインデックスされているため、包含と等価の区別は不要。
2. **Set の要素数上限**: 設けない。PropertyStore チェーン走査が O(N) だが、
   タグ用途では N が小さい前提。大量要素はグラフ構造 (エッジベース) が推奨。
3. **重複検知のコスト**: チェーン走査で O(N)。B+Tree インデックスでの高速検知は
   将来の最適化として残す。現状の Set セマンティクスでは N ≤ 数十を想定。
