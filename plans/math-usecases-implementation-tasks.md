# 数理ユースケース 実装タスク（上位 2 トラック）

> 親計画は [plans/math-usecases-track.md](math-usecases-track.md)。一次資料は
> [plans/math-usecases-cornerstone.md](math-usecases-cornerstone.md) /
> [plans/math-usecases-research.md](math-usecases-research.md)。
> 本書は優先度最上位の 2 トラック（BR: 有向ハイパーエッジ到達、MT: 制約最適化 / 最小出典集合）を、
> 単独でビルドと検証ができる増分へ分割する。3 件目以降（HG / PB / WC）は §後続トラックにスタブのみ置き、
> 上位が完了に近づいた時点でオーケストレータが本書へ追記して分割する（親計画 §1 のペース規律）。
> 実装中に確定した利用者向け仕様は本書ではなく `docs/spec/` へ反映する。

## 共通の実装契約

親計画 §3 の共通ルール（管理系文言の排除、コメントは「なぜ」、日本語文書スキル参照、ゼロ依存、実験ループ）に加え、
本トラックは次を前提とする。

| 項目 | 契約 |
|---|---|
| 前提 | HYP トラック（第一級ハイパーエッジ、incidence 直行、role 付き）は完了済み。有向は head/tail ロールで表す |
| 新規ストア | BR は原則ストア追加なし（既存 incidence チェーンの再走査）。MT の solver は永続状態を持たない |
| 外部ソルバ | Entail / Z3 / CP-SAT は**テストの正解照合専用**。製品依存に加えない（親計画 §3.4） |
| 命名 | HYP 命名原則に従う。公開動詞名は実装時に確定（本書の名は作業名）。`MinimumTransversal` の扱いは MT 節で整合を取る |
| FormatVersion | BR は on-disk 変更なしを目標（変えるなら理由を決定記録へ）。開発中 bump の扱いは親計画 §5 |

## 実験ループ

親計画 §3.5 の通り。spike は `benchmarks/Quiver.Benchmarks/Experimental/` に最小実装を置き、
交互実行の中央値と割当量を記録し、決定を本書の「決定記録」へ追記し、不採用コードを削除する。
ベンチのばらつきが 5% を超える場合はプロセス再起動を含む 3 回以上で判定する。

## 依存順

```text
BR-0 ─ BR-S ─ BR-1 ─ BR-2 ─ BR-3(as-built + sample)
MT-0 ─ MT-S ─ MT-1 ─ MT-2(後続・実需 gate)
```

- BR と MT は独立トラックだが、**同時着手しない**（親計画 §1）。BR を BR-3 まで通してから MT を着手する。
- BR-1 の incidence 走査基盤（node → tail-role hyperedge の列挙）は MT-1 の候補生成に再利用できる。

## 共通完了条件

各増分は次を満たしてから後続へ進む。

- `dotnet build Quiver.slnx` が 0 errors。
- 変更箇所に対応するテストプロジェクトが成功する。
- 公開 API を変更した増分は `Quiver.PublicApi.Tests` の承認ファイルを更新する。
- 永続レイアウトを変更した増分は、再オープン・rollback・savepoint・crash recovery を検証する。
- spike は計測値と採否理由を記録し、不採用コードを残さない。

---

# BR トラック: 有向ハイパーエッジ B-到達可能性

> 対応: コーナーストーン C-2 / D-1、research §3。
> 目的仮説: head/tail ロール付き有向ハイパーエッジは directed hypergraph そのもので、
> 「tail の**全**ノードが到達済みなら head に到達できる」という **AND 到達（B-到達）** は
> binary グラフの到達では表現できない。B-到達は入力線形で判定でき、Horn-SAT（Dowling–Gallier）と同一計算。
> 応用: 化学反応の合成可能性、ビルド依存、権限・型の導出。

## BR-0 トラック配線

### 目的

実装前にタスク管理と設計参照先を現行リポジトリへ接続する（HYP-0 と同型）。

### 変更

- `.agents/skills/quiver-implement/tasks/math-usecases.md` を新設し、本書 BR/MT の増分・読むべきファイル・
  完了条件を登録する。`.claude/skills/quiver-implement/tasks/math-usecases.md` から共有参照する。
- 両 `SKILL.md` に本トラックの分類・依存・進捗行を追加する（`.claude` と `.agents` を同内容で同期）。
- `docs/design/roadmap.md` に BR/MT の epic 表と親計画へのリンクを追加する。

### 完了条件

- BR/MT タスクを skill から一意に特定でき、親計画・本書・roadmap の ID と依存が一致する。

## BR-S B-到達線形性 spike

### 仮説

role で tail/head を分けた有向ハイパーエッジ上で、B-到達の全列挙は incidence チェーンを
一度舐めるだけで済み、入力（incidence 総数）に対し線形時間で走る。
binary reification（中間ノード経由の AND ゲート表現）版に対し、表現力と速度の両面で優位を示す。

### 比較・データセット

- 合成 Horn 網: hyperedge 数を 10^4 / 10^5 / 10^6 と振り、平均 tail 3・head 1 で生成する（傾き計測のため多点）。
- 比較対象: (a) 本命のカウンタ法（下記）、(b) binary reification 版（各 hyperedge を「AND ノード + tail→AND、
  AND→head エッジ」に展開して既存到達で解く）。
- seed ノード集合を与え、到達可能ノードの全列挙時間・1 操作あたり割当・logical page read を測る。

### 計測するアルゴリズム（カウンタ法）

各 hyperedge に「未到達 tail 数」カウンタを持たせ、ノード到達時に、そのノードが tail として参加する
hyperedge のカウンタをデクリメントし、0 になったら head をキューへ入れる（Dowling–Gallier の unit propagation と同型）。
カウンタは**クエリ内の一時状態**で永続化しない。tail 参加 hyperedge の列挙は既存の
`GetHyperedges(node, role = tailRole)`（incidence の NextInNode チェーン）で得る。追加索引は不要。

### 採否基準（数値の階層を明記）

- **必達（hard）**: 到達列挙時間が incidence 総数に対し線形（10^4→10^6 で傾き一定、超線形係数が出ない）。
  二重ループへ退化していないことの証明。未達なら実装が誤り。
- **必達（hard）**: カウンタ法の結果集合が reification 版と完全一致（AND 意味論の正しさ）。
- **努力目標（aspirational）**: 10^6・平均 tail 3 で <1s。マシン依存。未達でも線形なら記録して BR-1 へ。
- 走査中の managed allocation が出たら、比率にかかわらず BR-1 着手前に列挙子を修正する。

### 完了条件

- 決定記録に計測環境・多点の実測値・傾き・reification 比・階層別判定を残す。

## BR-1 B-到達オペレータと DSL

### 目的

seed ノード集合から、指定型・tail/head ロールの有向ハイパーエッジ網上の B-到達ノードを列挙する
物理オペレータと DSL 面を追加する。ストアは追加しない。

### 主な変更先（現行ツリーで確認して合わせる）

- `src/Quiver/Query/Logical/LogicalOp.cs`（新 logical op）
- `src/Quiver/Query/PhysicalPlanner.cs`（lower）
- `src/Quiver/Operators/`（新オペレータ。命名はオペレータ規約に合わせる）
- `src/Quiver/Client/GraphTraversal.cs` / `GraphTraversalSource.cs`（DSL 面）
- `tests/Quiver.Operators.Tests/` / `tests/Quiver.Client.Tests/`

### 実装

- BR-S で採用したカウンタ法を物理オペレータ化する。カウンタは `HyperedgeId` キーの一時辞書で持ち、
  hyperedge の tail 数は初回タッチ時に lazy 初期化してキャッシュする（全体で O(tail 次数総和) を保つ）。
- seed・hyperType・tailRole・headRole を受け取り、到達 `NodeId` を snapshot 可視性込みで列挙する。
- オペレータは `ref struct` enumerator を使い、1 行ごとの managed allocation を出さない
  （一時カウンタ辞書はクエリ全体で 1 回確保する固定費として許容し、行ごと確保と区別する）。
- DSL 作業名（実装時に HYP 命名原則で確定）: node 起点の `.ReachableBy(hyperType, tailRole, headRole)` 等。
  `Transversal` は使わない。方向はロールで表すため Out/In 接頭辞を使わない。

### テスト

- AND 意味論: head が到達するのは全 tail 到達時のみ（一部 tail 未到達で head 非到達）を検証する。
- サイクル・自己参照・複数 seed・到達不能ノードを検証する。
- 同一ノードが別 hyperedge の tail と head を兼ねる網、head が次の tail になる連鎖を検証する。
- snapshot visibility（別トランザクションの未 commit hyperedge が見えない）を検証する。
- reification 版との結果一致を統合テストで固定する（BR-S の oracle をテストへ昇格）。

## BR-2 最短 B-hyperpath と導出列

### 目的

B-到達に加法/ボトルネックコストを与え、目標ノードへの最小コスト導出列（発火した hyperedge の列）を返す。

### 実装

- BR-1 のカウンタ法を Dijkstra 化する（Gallo らの SBT 手続き）。コスト関数は sum（加法）と max（ボトルネック）の
  2 種を用意する（応用の幅のため。research §3）。
- 各 head のコストは「発火 hyperedge のコスト + tail コストの集約（sum なら総和、max なら最大）」で確定する。
- 導出列（どの hyperedge をどの順で発火したか）を復元して返す。作業名 `ShortestBHyperpath` /
  `ShortestDerivation`（命名は実装時確定）。
- **計算量の境界を docs/spec と XML コメントに明記する**: 一般の s-t 最短 hyperpath は NP-hard
  （Italiano–Nanni）で、多項式なのは B-path + 加法/単調コストの組だけ。実装が扱う範囲を利用者向けに述べる
  （管理系文言は入れない。理論的制約の説明として書く）。

### テスト

- 加法・ボトルネック双方で最小コストと導出列の正しさを、手計算できる小網で検証する。
- 複数の等コスト経路がある場合の決定性（タイブレーク規則）を固定する。
- 到達不能ターゲットで「導出列なし」を返すことを検証する。

## BR-3 統合・as-built・サンプル

### 目的

DSL / Match 面を仕上げ、利用者向け仕様とサンプルを揃えてトラックを閉じる。

### 実装・ドキュメント

- 必要なら Match 面（有向ハイパーエッジ網上の到達述語）を追加する。過剰な糖衣は入れない
  （HYP-3c の判断様式: 汎用 primitive で書けるなら固有糖衣を足さない）。
- `docs/spec/` に as-built を追記する（05_query に到達オペレータ、08_known_limits に NP-hard 境界と
  AND 意味論の契約）。`docs/design/development.md` の実装マップへ追記する。
- サンプル `samples/Quiver.Samples.<name>/` を 1 本追加する（親計画 §4 の基準）。題材は
  **化学反応の合成可能性**（tail=反応物, head=生成物, weight=化学量論の hyperedge に対し「この前駆体集合から
  合成可能な分子」＋「目標分子への最小コスト反応経路」）を第一候補とする。ビルド依存到達は代替題材。
  binary グラフでは reification が必須になる問いを主役に据え、決定的・自己完結・後始末込みで書く。

### 完了条件（トラック BR）

- kill criteria の実測値・階層分類・決定が本書の決定記録にある。
- 全スイート緑、PublicApi 承認済み、公開文書に管理系文言なし。
- サンプルが `dotnet run` で完走する。

---

# MT トラック: 制約最適化クエリ / 最小出典集合

> 対応: コーナーストーン C-3、research §4。
> 目的仮説: 「Match で選んだ部分グラフ + 制約 + 目的関数 → 最適解」を 1 クエリにする。第一弾は
> **最小出典集合（minimum hitting set / transversal）**＝「回答を接地する最小の Fact/Chunk 集合」。
> NP-hard だが回答単位の実サイズ（数百 fact）では厳密解が即答できる。RAG の grounded citation 最小化で実利。

## 命名の整合（着手前に必ず読む）

HYP の命名再調査では `Transversal` を**エンティティ / 走査名として**退けた（ハイパーグラフ理論の
transversal = hitting set と、`Traversal` との編集距離 1 の衝突が理由）。本トラックの
`MinimumTransversal` は**最適化クエリの動詞**であり、hitting set 問題を標準名で指す。衝突対象が異なるため
復活は意図的である。ただし実装時に次を守る:

- 走査 API（`Traversal` 系）と字面が紛れない配置にする（optimization/analytics 面へ置き、
  fluent 走査チェーンの途中動詞にしない）。
- 最終公開名はオーケストレータが HYP 命名原則で確定し、決定を本書へ残す。`MinimumHittingSet` 等の
  代替も俎上に載せる。

## MT-0 トラック配線

BR-0 と同じ配線を MT 分として行う（BR-0 で `math-usecases.md` を作成済みなら追記のみ）。

## MT-S 最小出典集合 spike

### 仮説

汎用 SMT/ILP ソルバを使わず、Quiver 内の小さな専用ソルバ（greedy 上界 + 分枝限定）で、
回答規模の最小 hitting set を厳密に、実用時間で解ける。候補生成は incidence チェーン走査で足りる。

### 比較・データセット

- 合成 RAG 回答: fact 500 / 出典（source ロールの Chunk）200、被覆度を数パターン振る。
- 比較対象: (a) greedy（ln n 近似）、(b) greedy 初期上界 + 分枝限定（厳密）、(c) 正解照合に
  Entail の充足判定 + サイズ二分探索、または外部 CP-SAT（テスト専用）。
- 厳密解の到達時間、greedy 比の解サイズ改善率、探索ノード数を測る。

### 採否基準（数値の階層を明記）

- **必達（hard）**: 分枝限定の解が、小規模（fact ≤ 30）での全探索/外部ソルバ oracle と厳密一致。
  最適性そのものの検証。未達なら solver が誤り。
- **努力目標（aspirational）**: fact 500 / 出典 200 で厳密解 <100ms。未達でも「厳密性は満たすが
  規模上限がある」と記録し、上限と greedy fallback の切替閾値を決めて MT-1 へ。
- Entail の既知限界（README の「深度上限 20 で Unknown」）が最適化ループで顕在化するかを確認し、
  顕在化するなら Entail 依存の照合を分枝限定側の自前検証に寄せる。

## MT-1 専用 transversal solver

### 目的

hyperedge 集合と被覆ロールを受け、全 hyperedge を被覆する最小ノード集合（最小出典集合）を
厳密に返す自己完結ソルバを追加する（ゼロ依存）。

### 主な変更先（現行ツリーで確認）

- `src/Quiver/`（optimization/analytics 面。走査層と混ぜない）
- `src/Quiver/IGraphTransaction.cs` 系（公開動詞の口）
- `tests/Quiver.Tests/`（solver 単体 + 実 DB 統合）
- `benchmarks/Quiver.Benchmarks/`（規模別の回帰 sentinel）

### 実装

- 候補生成: incidence チェーンを最小次数ノード起点で走査する（research §4）。BR-1 の tail 列挙基盤を再利用する。
- greedy（各段で未被覆 hyperedge を最も多く覆うノードを選ぶ）で初期上界を作り、分枝限定
  （次数最大の未被覆 hyperedge を選び、それを覆う候補で分岐）で厳密化する。上界で枝刈りする。
- 公開動詞（作業名）`MinimumTransversal(hyperType, coverRole)` は候補集合と被覆ロールを取り、
  最小ノード集合を返す。SIG スコアを目的関数係数に流用する拡張余地を残す（候補スコア最大化の重み付き版）。
- MT-S で決めた規模上限を超える入力では greedy 近似へ fallback し、近似である旨を返り値/ログで明示する
  （厳密性の嘘をつかない）。

### テスト

- 手計算できる小集合で最小性を検証する。全探索 oracle との一致（MT-S を昇格）。
- greedy fallback が近似解を返すこと、切替閾値の境界動作を検証する。
- snapshot visibility（未 commit fact を候補に含めない）を検証する。
- 空入力・単一 hyperedge・被覆不能ロールの縮退ケースを検証する。

## MT-2 制約フィルタ / 目的関数最適化（後続・実需 gate）

> **着手条件つき。** MT-1 完了後、実需（Match の数値制約プルーニング、または最小出典集合を超える
> 一般最適化クエリ）が具体化してから分割する。先回りで実装しない。

- **選択肢 A（制約フィルタ）**: Match に float[]/数値プロパティ上の線形制約を導入し、Entail の
  bound tightening 相当（RangeConstraint/SumConstraint の伝播）を**Quiver 内へ最小移植**して
  index range scan の境界計算に使う。「偽陰性あり」でもフィルタとしては健全（保守的緩和）。
- **選択肢 B（目的関数最適化）**: MT-1 の分枝限定へ目的関数を載せ、予算・基数制約付きの
  最大スコア部分集合選択を解く。IHS（MaxHS 系）は state-of-the-art だが自前 UNSAT コア抽出が要るため、
  まず分枝限定で足りるかを spike する。
- いずれも Entail は照合 oracle 専用。移植する部品と、外部参照に留める部品を着手時に線引きする。

---

# 後続トラック

> BR/MT の次に控える残りトラックは、**基礎寄り優先で再編した上で BR/MT と同粒度に分割済み**。
> 正本は [plans/math-usecases-foundational-tracks.md](math-usecases-foundational-tracks.md)
> （PV: Provenance 半環 / WC: WCOJ+hypertree / PB: TDA barcode / FCA: 形式概念分析 / HG: HodgeRank、
> 着手順序・依存・全体計画への転記手順を収録）。応用・研究要員（in-DB GNN / CQL / 高次力学 /
> 世界モデル軌跡ストア / テンソルネットワーク）は同書 §6 に理由付きスタブとして最下位に置く。
> 親計画 §1 のペース規律により、上位が完了に近づいた時点でオーケストレータが 1 本ずつ着手する。

---

# 決定記録

> 実装エージェントは spike と本実装の決定をここへ追記する（HYP の決定記録に倣う。計測環境・多点数値・
> 階層分類・続行/是正/撤回の別を残す）。着手までは空。
