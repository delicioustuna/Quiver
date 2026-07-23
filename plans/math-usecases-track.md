# 数理ユースケース トラック 親計画書（実装委譲用）

> 起案日: 2026-07-07。現行アーキテクチャ追従: 2026-07-23。
> HYP トラック（現在の Nexus 機能）完了を承けて起こした後続トラック。
> 本書は「Quiver = 箙（quiver）をデータベースにする」という名前に照応する数理ユースケース群を、
> **実装エージェントとオーケストレーションエージェント（いずれも本セッション外の Opus 等）へ委譲する**
> ための親計画である。
> BR と MT の実装増分は [plans/math-usecases-implementation-tasks.md](math-usecases-implementation-tasks.md)、
> PV、PB、HG、FCA、WC の実装増分は
> [plans/math-usecases-foundational-tracks.md](math-usecases-foundational-tracks.md) を正本とする。
>
> 位置づけ:
> - 各案の目的・論文参照・Quiver 内接続点・spike の kill criteria の一次資料:
>   [plans/math-usecases-cornerstone.md](math-usecases-cornerstone.md) /
>   [plans/math-usecases-research.md](math-usecases-research.md)。
> - 現行実装の契約は [docs/spec/](../docs/spec/) を正本とし、未実装の目標は本計画と実装タスクへ分けて記録する。
> - 実装前提となる既存資産（Nexus、Entail、LeWorldModel、vector segment、ApplyDyadic）は
>   コーナーストーン §0 を参照。

---

## 0. 現行アーキテクチャとの対応

本計画の数理用語と Quiver の実装語彙を次のように対応づける。

| 数理上の対象 | 現行 Quiver の対象 | 主な接続点 |
|---|---|---|
| node / vertex | `Vertex` / `VertexId` | `IReadTransaction`、`IWriteTransaction`、`GraphTraversal<VertexId>` |
| binary edge | `Edge` / `EdgeId` | Edge 隣接、`ExpandOp`、`GraphKernel` |
| role 付き hyperedge | `Nexus` / `NexusId` | `GetNexuses`、`GetMembers`、`ExpandToNexusOp`、`ExpandMembersOp` |
| 星型 hyperedge pattern | `NexusPattern` | `GraphPattern.Nexus`、`MatchCompiler` |
| vector 近傍検索 | immutable vector segment ごとの HNSW | `VectorSegmentIndex`、`KnnSearch` |
| ダイアディックスコア | `ApplyDyadic` | `ApplyDyadicOp`、`TypedGraphTraversal.ApplyDyadic` |

現行エンジンは Single Writer + Snapshot Readers であり、読み取り API は開始時点の snapshot に束縛される。

`VertexId`、`EdgeId`、`NexusId` は Generation を含む公開 identity であり、物理 Sequence を公開結果や一時集合の同一性に使わない。

Nexus のメンバー集合は作成時確定で、incidence は独立した MVCC entity ではなく Nexus header の可視性に従う。

vector index は snapshot 可視な複数の immutable segment から検索する。
各 HNSW の近傍リストは segment 内部の private state であり、全 snapshot を表す単一の公開 k-NN グラフではない。

この節は接続点の正本ではない。
実装時は [docs/spec/](../docs/spec/) の as-built と現行コードを読み、計画書に残る旧識別子を根拠に互換 API を追加しない。

---

## 1. 委譲モデル

本トラックは三者で回す。役割を混ぜない。

| 役割 | 担当 | 責務 |
|---|---|---|
| 起案（本書の作成者） | 本セッション | 親計画、実装タスク分割、共通規約の確定 |
| オーケストレーション | 別 Opus | トラック選択、spike 結果の検証、撤回/是正/続行の判断、実装エージェントへの割当、決定記録の追記 |
| 実装 | 実装エージェント | 増分ごとの実装・テスト・ベンチ・spike。`/quiver-implement <track>` から着手 |

### 着手のペース（オーケストレータへの拘束）

- **並列展開しない。優先度（＝価値）の高いものから 1〜2 件だけ着手する。** 本書の
  トラック一覧（§6）は全体像であって同時着手指示ではない。1 件を「実装 → spike →
  検証ゲート通過 → as-built + サンプル」まで通してから次へ進む。
- BR と MT は `math-usecases-implementation-tasks.md`、基礎寄りの後続候補は
  `math-usecases-foundational-tracks.md` に分割済みである。
  分割済みであることは着手承認を意味しない。
  オーケストレータは本書の順序、外部検証、ユーザ承認から着手対象を決める。

---

## 2. 検証と撤回の規律

「推論より実地検証」（[[empirical-verification-over-reasoning]]）を本トラックの一次規律とする。
各案は数理的に魅力的だが、実装コストと実測性能で沈むものがある前提で進める。

### 2.1 判断のタイミング（どの局面で何を決めるか）

1. **着手前（gate 0）**: そのトラックの kill criteria の数値を先に固定し、各数値を §2.2 の
   三階層のどれに置くかを分類してオーケストレータが記録する。分類前に本実装へ進まない。
2. **spike 直後（gate 1）**: 交互実行で中央値を測り、固定した数値と突き合わせる。ここで
   続行 / 是正 / 撤回のいずれかを決める（§2.3）。
3. **各増分完了時（gate 2）**: 共通完了条件（build 緑・対応テスト緑・PublicApi 承認・
   永続変更なら再オープン/rollback/savepoint/crash recovery）を満たしてから後続へ。
4. **トラック完了時（gate 3）**: 統合性能ゲート（製品 API 経由での再測定。HYP-6c の前例）と
   as-built docs・サンプル完走をもって完了とする。

### 2.2 数値に対する温度感（kill criteria の三階層）

**すべての kill criteria の数値が同じ重みではない。** 各数値を着手前に次の三階層のどれかへ分類する。
分類は「その数値を外したとき何が壊れるか」で決める。

- **必達ゲート（hard / kill）**: 外したら**その設計アプローチが誤り**であることを意味する数値。
  漸近優位の有無、線形性（傾き一定）、正解一致率など、**アルゴリズムの正当性そのもの**を測るもの。
  未達なら撤回するか、事前に定義した縮小スコープ（例: 「dense/cyclic 専用の隠し経路に留める」）へ倒す。
  例: WCOJ が binary を N を振っても上回らない → cyclic 専用経路化 or 撤回。
  例: B-到達列挙が入力に対し線形でない（傾きが N とともに増える）→ 二重ループへの退化＝実装が誤り。
- **是正ゲート（corrective）**: **価値は実証済みだがレイアウト/実装が現状は未達**という数値。
  未達なら殺さず、是正タスクを新設して再測定する。HYP-2c（WAL 倍率未達）→ HYP-2d（incidence 再設計）→
  HYP-6c（製品 API 再測定で合格）の前例がこれ。閾値超過が固定費で説明でき超線形でないなら是正で回収できる。
- **努力目標（aspirational）**: **主目的が既に達成されていれば、残差を記録して出荷してよい**数値。
  絶対時間の目標（「<100ms」「<5s」等）はしばしばここに入る。未達でも機能の存在価値が消えないなら、
  実測値と残差要因を記録し DEFER する。FTS で「全形状 ≤5× 未達だが GA 既達 → 停止」とした前例、
  ZD/VP/CR で「VP-1/CR-2 実測未達 → 撤回・DEFER（再開条件を計画書に明記）」とした前例がこれ。

> 具体例（有向ハイパーエッジ B-到達の spike、コーナーストーン C-2 / research §3）:
> 「hyperedge 10^6・平均 tail 3 で B-到達全列挙が入力線形（<1s）」という kill criteria は、
> **線形性（傾き一定）＝必達ゲート**（未達は二重ループ退化＝設計誤り）、
> **絶対値 <1s ＝努力目標**（マシン依存。未達でも線形なら機能は成立、記録して次工程へ）に分解する。
> この分解を着手前に記録するのがオーケストレータの仕事である。

### 2.3 続行 / 是正 / 撤回の分岐

| gate 1 の結果 | 該当階層 | 行動 |
|---|---|---|
| 全数値クリア | — | 本実装へ続行 |
| 是正ゲート未達 | corrective | 是正タスクを新設し、原因分解（費用内訳の計測）→ 再測定。回収まで後続を止める |
| 努力目標のみ未達 | aspirational | 実測値・残差要因・DEFER 条件を記録して続行 or 出荷。撤回はしない |
| 必達ゲート未達 | hard | 縮小スコープへ倒すか撤回。撤回時は §2.4 |

### 2.4 撤回・DEFER プロトコル

撤回や DEFER を選んだら、必ず次を残す（ZD/VP/CR トラックの前例に倣う）。

- 実測値（何を測って幾つだったか）と、どの階層のどの数値を外したか。
- 撤回の理由（設計が誤りなのか、コスト対効果が合わないのか）。
- **再開条件**（何が変われば再着手するか。例: 「bulk 実需が出たら BulkLoader 経路を第一候補に」）。
- 実験ループ契約（§3）に従い spike の不採用コードを削除する。撤回した機能名・タスク番号を
  ソース/テスト/ベンチ/公開 docs に残さない（§3 の管理系文言排除）。

---

## 3. 全タスク共通ルール

HYP トラックの共通ルールを継承する。実装エージェントとオーケストレータの双方に拘束する。

### 3.1 管理系文言をコード・公開文書に残さない

[[feedback-no-task-numbers-in-xml-docs]] を厳守する。**タスク番号・案 A/B・トラック名などの管理系文言は、
`plans/` と `docs/design/`（代替案の定義文脈）にのみ置いてよい。** 次には一切入れない。

- `src/` / `benchmarks/` / `tests/` のソースコメント・XML コメント。
- 公開識別子（クラス名・メソッド名・CLI フラグ・index 名・診断 ID の説明文言）。
- `docs/spec/` / `docs/api/` / README などの利用者向け公開ドキュメント。

本書が使う `BR-*` / `MT-*` 等の ID は計画上の管理番号であり、この規約により**コードには現れない**。
命名は HYP の命名原則（`Relation`/`Link`/`Transversal` の扱い、`As*` ビュー / `To*` 実体生成、
DSL 動詞は戻り値エンティティの複数形）に従う。
`Transversal` は HYP でエンティティ/走査名として退けたが、最適化クエリの動詞
`MinimumTransversal`（hitting set の標準名）としての復活は意図的な例外である（衝突対象が異なる。
実装タスク書の該当節で明示的に整合を取る）。

### 3.2 コメントの粒度は「なぜ」を補完する

過去コミットと同じ粒度を守る。**コードが「何をしているか」ではなく「なぜそうしているか」を書く。**

- 自明な処理に逐語コメントを付けない。非自明な設計判断・トレードオフ・不変量・前提条件を書く。
- **ベンチマークとテストも同様**。「この閾値の根拠」「なぜこのデータ形状で測るか」「なぜこの
  エッジケースを検証するか」を残し、アサーションの逐語説明はしない。
- 利用者向け情報は XML コメントへ、設計者向け情報はソースコメントへ分離する
  （[csharp-xml-comment] スキルの原則。情報を捨てず適切な場所へ置く）。

### 3.3 日本語文書スキルの参照方針

コメント・ドキュメント（XML コメント、docs/spec 追記、サンプルの説明文）を書くときは
[japanese-tech-writing] スキルの規範（一文一行、パラグラフライティング、ツッコミどころの除去、
LLM っぽい空句の禁止、冗長の排除）に従う。**ただし重いスキルなので頻繁には読まない。**
新規に長文の設計文書や spec を書き起こす直前に一度参照し、以降の細かな修正では読み直さない運用とする。

### 3.4 依存を増やさない（外部ソルバは照合専用）

`Quiver` コアはゼロ依存を維持する（現状 `System.IO.Hashing` のみ）。

- 数理アルゴリズムは Pure C# 自前実装が原則。外部数理ソルバ（Z3 / CP-SAT / GAP / Entail 等）は
  **テスト時の正解照合（oracle）専用**であり、製品ビルドの依存には加えない。
- 別リポジトリ Entail（D:\csharp\Entail）は設計の参照実装であって、`ProjectReference` の対象ではない。
  必要な部品は Quiver 内へ最小移植する（research §4 の方針）。

### 3.5 実験ループ（spike の始末）

HYP と同じ実験ループ契約に従う。

1. 比較対象・データセット・採否基準を先にコミットする。
2. spike の最小実装は `benchmarks/Quiver.Benchmarks/Experimental/` に置く。
3. Release 構成で交互実行し、中央値と割当量を記録する（IQR>5% はプロセス再起動込みで 3 回以上）。
4. 結果と決定を実装タスク書の「決定記録」へ追記する。
5. **不採用コードを削除し、採用した契約だけを本実装へ渡す。**

---

## 4. サンプル実装の方針（良いユースケースサンプルとは）

各トラックの完了物には、機能を「物語として一気通貫で見せる」実行可能サンプルを 1 本付ける。
基準は既存の [samples/Quiver.Samples.Nexuses/](../samples/Quiver.Samples.Nexuses/) を範とする。
そのサンプルが良い理由を分解して、本トラックの方針とする。

- **実ユースケースの一気通貫を 1 ファイルで見せる。** 取込 →（複数の読み経路で）読み戻し → 実利の回収、
  までを 1 本の `Program.cs` で走らせる。抜粋やスニペットではなく `dotnet run` で動く完成物にする。
- **「この機能でしか自然に書けない問い」を主役にする。** ハイパーエッジのサンプルは「出典ロール付き
  n 項ファクトの grounded citation を join なしで取る」を見せた。数理トラックも同様に、**binary グラフや
  既存機能では原理的に書けない問い**（AND 依存の到達、最小出典集合、順位と信頼度の同時算出、
  しきい値非依存のクラスタ構造）を中心に据える。機能の羅列デモにしない。
- **複数の API 面を通す。** ハイパーエッジ例は 型なし DSL / Match / 型付き API の三経路で同じ対象を
  読み、ローレベル API とも一致することを示した。新機能も、公開した各面（ローレベル動詞・DSL 糖衣・
  Match 統合があれば全部）を最低 1 回ずつ通す。
- **決定的・自己完結・後始末込み。** 外部モデルやネットワークに依存しない決定的ダミー
  （例: `HashEmbedder` の文字ヒストグラム）で動かし、一時ディレクトリを `finally` で消す。
  CI とオフラインで再現できることを最優先する。
- **数理トラック固有の推奨題材**（コーナーストーン D 節と接続）:
  - 有向ハイパーエッジ到達 → 化学反応の合成可能性 or ビルド依存の到達（「この前駆体集合から何が作れるか」）。
  - 制約最適化 → RAG 回答の最小出典集合（「回答を接地する最小の Chunk 集合」）。
  - HodgeRank → ペア比較からの大域順位 + 矛盾スコア（「順位とその信頼度を同時に返す」）。
  - TDA barcode → 埋め込み集合のクラスタ数と安定スケール（「しきい値を決めずにクラスタ構造を返す」）。
- **サンプルにも管理系文言を入れない（§3.1）。** コメントは「なぜこの題材か」を短く述べるに留め、
  タスク番号や設計案の識別子を書かない。

---

## 5. FormatVersion と公開バージョンの扱い

数理トラックの一部（WCOJ の sorted 索引、TDA の barcode キャッシュ等）はオンディスク format を
変えうる。開発中の `FormatVersion` 運用と公開 SemVer の分離は
[docs/design/development.md](../docs/design/development.md)（「開発中の FormatVersion 運用」節）を正本とする。
要点のみ再掲する。

- 開発中は増分ごとに `FormatVersion.Current` を自由に bump してよい（単調・clean break・移行なし）。
  1 トラックで複数回上がり、main へ着くまでに数バージョン進んでよい。**中間バージョンは温存しない。**
- **公開バージョン（`Directory.Build.props` の `VersionPrefix`）は format bump 回数に追随しない。**
  SemVer の意味論だけで上下する。
- GA 直前に pre-release 期の format 履歴を 1 本のベースラインへ畳む（v1 の前例）。
- 計画書では「on-disk format V5」と「公開 0.2.0」を明示的に書き分け、同じ数として扱わない。

---

## 6. トラック一覧と優先順位

コーナーストーン §実装優先順位（圧倒性 × 実装コスト × 既存資産活用）を継承する。
状態はすべて未着手。
BR と MT は `math-usecases-implementation-tasks.md`、PV、PB、HG、FCA、WC は
`math-usecases-foundational-tracks.md` に分割済みである。

| ID | 対応 | 内容 | 圧倒性 | コスト | 資産活用 | 優先 | 状態 |
|---|---|---|---|---|---|---|---|
| **BR** | C-2, D-1 | 有向ハイパーエッジ B-到達可能性 / 最短 B-hyperpath | 高（表現力） | 低 | 高（incidence 走査） | **P0** | 分割済み・未着手 |
| **MT** | C-3, C-5 | 制約最適化クエリ / 最小出典集合（MinimumTransversal） | 高（実利） | 中 | 高（incidence + SIG） | **P0** | 分割済み・未着手 |
| HG | C-4 | HodgeRank / 離散 Hodge 分解（順位 + 信頼度） | 中 | 低（疎最小二乗 CG） | 高（ApplyDyadic） | P1 | 分割済み・未着手 |
| PB | C-5 | パーシステントホモロジー barcode（名前照応） | 高（物語 + 実利） | 中〜高（Ripser 移植） | 高（vector segment） | P1 | 分割済み・未着手 |
| WC | C-1, C-1' | WCOJ + hypertree 分解（漸近優位 + 証明書） | 最高（漸近） | 高（LFTJ + trie） | 中（NexusPattern 依存） | P2 | 分割済み・未着手 |
| PV | D-5 | Provenance 半環（出典代数、MT の上屋） | 中 | 低（注釈フック） | 高（走査 + MT） | P2 | 分割済み・未着手 |
| FCA | C-8 | 形式概念分析（incidence からの概念束マイニング） | 中 | 中 | 高（incidence） | P2 | 分割済み・未着手 |
| — | C-6/7, D-2/3/4 | 箙表現格納 / CQL / 単体複体力学 / 軌跡ストア | 中〜低 | 中〜高 | 中 | P3 | 研究・物語要員 |

推奨着手順（コーナーストーン申し送りを継承）:

1. **BR（有向ハイパーエッジ B-到達）** — HYP 有向モデルの正当な出口。実装最小、D-1 化学反応と直結。
2. **MT（制約最適化 / 最小出典集合）** — RAG citation 最小化で実利。BR の incidence 走査基盤を再利用。
3. 以降（HG → PB → WC）は上位 2 件の完了度を見てオーケストレータが分割・投入する。
   HG と MT は疎最小二乗（CG）基盤を共有しうるため、MT 着手時に共通部品化を検討する。

---

## 7. Skill とタスク参照の配線

`quiver-implement` Skill は実装手順と正本へのルーティングだけを保持する。
進捗、着手対象、検証結果は git 管理下の本書と実装タスク書へ記録し、gitignore 対象の Skill には書かない。

- `.agents/skills/quiver-implement/tasks/math-usecases.md` を数理トラック共通の不変 router とする。
- `.claude/skills/quiver-implement/tasks/math-usecases.md` は Agents 側 router と tracked な計画書を参照する。
- 両 `SKILL.md` は byte-for-byte 同一に保ち、数理トラックの正本一覧と実装前ゲートだけを定義する。
- `docs/design/roadmap.md` は本書への入口だけを持ち、個別タスクの可変状態を複製しない。

---

## 8. 完了の定義（トラック単位）

- kill criteria の実測値と、三階層分類・続行/是正/撤回の決定が実装タスク書に記録されている。
- 全スイート緑（`dotnet build Quiver.slnx` + 対応テスト）、PublicApi 承認済み。
- `docs/spec/` に as-built を追記済み、公開文書に管理系文言が無い。
- 実行可能サンプル 1 本が §4 の基準を満たして完走する。
- 統合性能ゲート（製品 API 経由の再測定）を通過、または努力目標未達分を残差として記録済み。

---

## 9. リポジトリ適合性の予備評価（未検証）

この節は 2026-07-23 時点の Codex による予備評価であり、トラックの採否、優先度、着手順を決定しない。

別の検証エージェントは、一次資料、現行実装、spike 条件を独立に確認し、採用、修正、棄却の判断を決定記録へ残す。

| 対象 | 予備評価 | 理由 |
|---|---|---|
| BR-1 B-到達 | 現行基盤へ接続できる可能性が高い | 双方向 incidence、snapshot 可視性、Logical IR、物理 planner、Traversal DSL が存在する。比較 oracle の妥当性は別途検証が必要 |
| BR-2 最短導出 | 目的関数と公開契約の検証が必要 | B-tree 型の再帰コストと、共有を含む最小 B-path を区別する必要がある可能性がある |
| MT-1 最小出典集合 | 専用 solver として接続できる可能性がある | incidence から候補集合を作れる。厳密解と近似 fallback の結果契約、規模別性能は未検証 |
| PV provenance | overhead spike が必要 | 既存走査のホットパスへ注釈合成を加えるため、注釈なし経路の回帰を測る必要がある |
| HG HodgeRank | 現行の Edge 走査と ApplyDyadic へ接続できる可能性がある | 旧 `EntityCandidateSet` は存在しないため、現在の traversal 結果を入力にする契約が必要 |
| FCA | incidence を入力にできる可能性がある | 列挙結果数の爆発と support 契約は spike での確認が必要 |
| PB H0 | 入力アダプタの設計が必要 | HNSW 近傍は private かつ segment 単位であり、snapshot 全体の k-NN グラフを直接公開していない |
| PB H1 | 実装規模と検証方法の精査が必要 | Ripser 相当の低次 persistent homology は独立したアルゴリズム実装になる |
| WC | query/index 基盤の追加範囲を精査する必要がある | 現行 IR に汎用 join、ソート済み trie iterator、EXPLAIN 相当の契約がない |
| P3 候補 | 実需と公開契約の検証が必要 | 現時点の文書は研究スタブであり、個別の実装境界を持たない |
