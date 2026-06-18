# 型付き書き込みシンク + クエリパターン整備トラック (WS / QP)

> 起案日: 2026-06-17。
> 経緯: 「名前が B で始まる Person 各々に対し、C で始まる Tool へ Use エッジを動的生成」を
> 型安全に書きたいという相談から、書き込み系拡張をどこまでやるかを評価 (本ファイル上部の決定事項)。
> 併せて Coalesce/Optional/Union 等「工夫された読み取りクエリ」のケース集が薄いと判明したため、
> サンプル + cookbook 整備を同トラックに含める。
> 関連 as-built: [03_mvcc.md](../docs/spec/03_mvcc.md) / [08_known_limits.md](../docs/spec/08_known_limits.md) /
> [merge.md](../docs/api/concepts/merge.md)。

## 評価と決定事項 (ユーザ確定 2026-06-17)

書き込み系トラバーサル拡張について「やる/やらない」を実装現実に照らして評価し、以下を確定:

1. **やらない: 融合ストリーミング変異オペレータ (Gremlin の中間 `addE` ステップ)。**
   - 実行は Volcano pull 型 ([IPhysicalOperator.cs](../src/Quiver/Operators/IPhysicalOperator.cs))。
   - 可視性は read-your-writes (`xmin == self → 可視`、[Visibility.cs:53](../src/Quiver/Core/Visibility.cs#L53))。
     パイプライン途中で書くと自分の書き込みが上流走査に再投入される **Halloween 問題**が発生し、
     回避には materialization barrier が必要 = ストリーミングの利点が消える。
   - 単一ライタ + スレッドアフィン + `[ThreadStatic]` MVCC 文脈 ([08_known_limits.md:27](../docs/spec/08_known_limits.md#L27))
     のため融合化で稼げる並列性も無い。エッジ書き込みに bulk fast path も無い
     (`CreateRelationship` は 1 本ずつ) ため throughput 動機も薄い。
   - write/recovery は最も安全敏感な面 (v1 監査 #1 recovery clobber=HIGH 未修正) で、
     クエリパイプラインに変異を持ち込むとクラッシュ契約面が広がる。

2. **やる: 型安全な「終端 write シンク」(案 B)。** call-site の見た目は `addE` 相当にできる。
   - 端点型安全は **トラバーサルの要素型** (`TypedGraphTraversal<TSource>` / `<TTarget>`) と
     `IGraphRelationship<TRel, TSource, TTarget>` 制約で担保 ([IGraphRelationship.cs:39](../src/Quiver/Client/IGraphRelationship.cs#L39))。
     `NodeId` は untyped のまま。シンクは **両側を materialize してからループ書き込み** = Halloween を構造的に回避。
   - 既存 write 経路 (`CreateRelationship` / `SetProperty` / `MergeNode`) をそのまま使い、
     新規物理オペレータ・プラン・WAL レコードを増やさない。

3. **エッジ upsert プリミティブ `MergeRelationship` を新設。** `MergeNode` と対称。
   coalesce ブランチでの変異による upsert は Quiver では不成立 (ブランチが読み取り専用 +
   `fold/unfold/constant` 非対応) なため、その代替を終端シンク側で型安全に提供する。

4. **読み取り側は新規実装ではなくケース整備。** Coalesce/Optional/Union/As-Select/型安全 Where は
   既に実装済み ([GraphTraversal.cs](../src/Quiver/Client/GraphTraversal.cs))。不足はサンプルと cookbook。
   「coalesce ブランチでは変異できない、代替はこれ」も明記する。

## 設計上の重要発見 (調査時 2026-06-17)

- **端点型安全に typed NodeId は不要。** 受け手が `TypedGraphTraversal<TSource>`、引数が
  `TypedGraphTraversal<TTarget>` なら、`where TRel : IGraphRelationship<TRel,TSource,TTarget>` だけで
  「Person→Use→Tool」をコンパイル時強制できる。`new Use{...}` を返すラムダから `TRel/TTarget` も型推論される。
- **既存 `IGraphRelationship<TSelf>.Insert(tx, NodeId, NodeId, TSelf)` は raw のまま温存。** シンクの内部実装が呼ぶ。
  単一 raw `NodeId` ペアの端点型チェックは原理的に不可 (untyped) なので**非目標**とし、安全性は集合シンク層で提供。
- **`MergeRelationship` の存在チェックは `EnumerateRelationships(from, Outgoing, type)` の O(out-degree)。**
  低 fan-out では問題ないが、直積 MergeRelationship は |B|×deg。コストを文書化し、エッジ存在インデックスは非目標。
- **シンクは materialize-first なので read-your-writes 下でも安全。** `ToListWithIds()`
  ([TypedGraphTraversal.cs:154](../src/Quiver/Client/TypedGraphTraversal.cs#L154)) で両側 id を確定してから書く。
  カーソル生存中の書き込み (案 A) は採らないので write-during-cursor spike は不要。
- **SourceGen 変更なしで Phase 1 完結可。** シンクは `Quiver.Api` の汎用拡張メソッドとして手書きできる。
  生成糖衣 (`people.AddUse(tools, ...)` / `Use.Merge(...)`) は既存 `{Rel}TraversalExtensions`
  ([GraphRelationshipEmitter.cs:88](../src/Quiver.SourceGen/GraphRelationshipEmitter.cs#L88)) に倣う Phase 2。
- **公開 API 追加は approved.txt 更新が必要** ([tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt](../tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt))。

## API 形 (確定イメージ)

```csharp
// WS-1: tx/g レベルのエッジ upsert プリミティブ (MergeNode と対称)
(RelationshipId Id, bool Created) MergeRelationship(NodeId from, NodeId to, string type);   // IGraphTransaction
(RelationshipId Id, bool Created) g.MergeRelationship(NodeId from, NodeId to, string type);  // GraphTraversalSource 糖衣

// WS-2: 型安全な集合終端シンク (TypedGraphTraversal<TSource> 拡張)
long AddRelationship<TSource,TRel,TTarget>(
    this TypedGraphTraversal<TSource> sources,
    TypedGraphTraversal<TTarget> targets,
    Func<TSource,TTarget,TRel> edge)
  where TSource:IGraphNode<TSource> where TTarget:IGraphNode<TTarget>
  where TRel:IGraphRelationship<TRel,TSource,TTarget>;            // 直積で生成、戻り=生成本数

(long Created,long Matched) MergeRelationship<...>( ... 同上 ... );        // 直積で upsert
// プロパティ無し版 (where TRel:new()) と、相関版 (targets: Func<TSource,TypedGraphTraversal<TTarget>>) を overload で
```

```csharp
// 利用例 (発端のシナリオ): B-Person → C-Tool に Use を張る
using var tx = db.BeginTransaction();
var g = tx.G(db.Schema);
long n = g.Nodes<Person>().Where(p => p.Name.StartsWith("B"))
          .AddRelationship(g.Nodes<Tool>().Where(t => t.Name.StartsWith("C")),
                   (p,t) => new Use { Note = "auto" });
tx.Commit();
```

## タスク列

| ID | 内容 | 依存 | 優先 |
|---|---|---|---|
| WS-1 | `MergeRelationship(from,to,type)` → `(RelationshipId,bool Created)`。`IGraphTransaction` + `GraphTransaction` 実装 (存在チェックは `EnumerateRelationships` の degree 走査) + `GraphTraversalSource` 糖衣。approved.txt 更新。 | — | P0 |
| WS-2 | 型安全集合シンク `AddRelationship` / `MergeRelationship` (+プロパティ無し版 +相関版) を `Quiver.Api` 拡張で。内部は materialize→ループ。approved.txt 更新。 | WS-1 | P0 |
| WS-3 | 振る舞いテスト: AddRelationship 直積本数 / MergeRelationship 冪等性 / 端点型制約 (正常系コンパイル) / materialize-first が source==target ラベルでも無限ループしない退行テスト。 | WS-1,2 | P0 |
| WS-4 | (任意) SourceGen 糖衣: `Use.Merge(tx,from,to,e)` と `people.AddUse(tools,e=>...)` を `{Rel}TraversalExtensions` に追加 + SourceGen テスト。 | WS-2 | P2 |
| QP-1 | サンプル `samples/Quiver.Samples.QueryPatterns`: 型安全 Where/StartsWith・Coalesce・Optional・Union・As/Select・存在条件つき書き込み (MergeNode/MergeRelationship + C# if)・発端の直積 AddRelationship を 1 本に。 | WS-2 | P1 |
| QP-2 | `docs/cookbook.md` に「工夫された読み取りクエリ」節 + 書き込みシンク節を追記。coalesce ブランチでの変異 (upsert) は非対応である旨と代替を明記 (利用者の混乱回避)。 | QP-1 | P1 |
| QP-3 | ✅ MergeRelationship degree 依存コスト計測 → cookbook 注記 + [計測レポート](../docs/benchmarks/2026-06-18_QP-3_MergeRelationshipCost.md)。hit ~116ns/edge 勾配。 | WS-1 | P2 |

並列性: WS-1 着手後 WS-2/WS-3 は連続。QP-1/QP-2 は WS-2 完了後。WS-4/QP-3 は任意で後回し可。

## 検証 (empirical: 推論で断定しない点)

- **Halloween 退行テスト (WS-3)**: source と target が同一ラベル (例 Person→Person) の AddRelationship で、
  materialize-first が効かず naive 実装なら無限増殖する形を回帰として固定。
- **read-your-writes 整合 (WS-3)**: 同一 tx で AddRelationship 後に再 `ToList()` して期待件数を確認。
- **MergeRelationship コスト (QP-3, 任意)**: out-degree を変えた 1 点計測。bench 化はせず cookbook 注記に留める。

## 非目標 (明示)

- 融合ストリーミング変異オペレータ (中間 `addE`)、ストリーミング変異、複数ライタ前提 write。
- typed `NodeId<TNode>` ラッパ (全 traversal 終端に波及するため過大)。単一 raw ペアの端点型チェックは提供しない。
- エッジ存在インデックス (MergeRelationship は degree 走査で実装)。
- Coalesce/Optional ブランチへの変異ステップ追加 (決定事項 1 と同根で不可)。

## 未実施 (トラック開始を決めたら行う)

- [ ] `.claude/skills/quiver-implement/` への登録 (SKILL.md タスク表 + `tasks/write-sinks.md` 作成)。
      スキルは empirical tuning 済みのため編集は着手決定後に慎重に。本計画書を参照させる。
- [ ] `docs/roadmap.md` に WS-1〜4 / QP-1〜3 追記。
- [ ] approved.txt の差分は WS-1/WS-2 各タスク内で更新 (公開 API を増やすため)。
