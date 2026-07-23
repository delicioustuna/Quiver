# Quiver 数理ユースケース コーナーストーン資料

> 起案日: 2026-07-04。本ファイルは「Quiver = 箙 (quiver, 表現論の用語) をデータベースにする」という
> 名前の含意に照応する数理的ユースケース群を、後続エージェントが実装検討の起点にできるよう
> 論文・実装参照・Quiver 内の接続点・spike の kill criteria まで落とした資料である。
>
> 位置づけ: plans/usecase-ideas.md C 節の各案を「どの論文のどこを見て、Quiver の何に接続し、
> 何を最初に測るか」まで具体化したもの。実用系 (A/B 節) は usecase-ideas.md を参照。
> role 付き hyperedge の現行実装名は Nexus である。historical な正本計画は plans/hyperedge-track.md。
>
> 重要な前提 (このリポジトリの既定方針):
> - 「推論より実地検証」— 各案は spike で kill criteria を数値固定してから本実装可否を判断する
>   ([[empirical-verification-over-reasoning]])。
> - 依存を増やさない。外部数理ソルバ (Z3 / CPLEX / GAP など) は「参照実装・正解照合用」であり、
>   本体は Pure C# 自前実装が原則。Entail (D:\csharp\Entail) が既にこの方針で SMT 的機能を
>   自作している先例。
> - コメント・公開 docs にタスク番号や案 A/B を残さない ([[feedback-no-task-numbers-in-xml-docs]])。

---

## 0. 外部資産の棚卸し (このリポジトリ群で既にあるもの)

後続エージェントが「ゼロから作る」と誤解しないよう、流用可能な既存実装を先に列挙する。

### D:\csharp\Entail — 充足可能性・冗長性判定の予備実装

- **正体**: 汎用 SMT ソルバを使わず、制約伝播 + ブランチ探索で「新制約 P_new が既存集合から
  冗長 (= P_1 ∧ … ∧ P_N ∧ ¬P_new が UNSAT) か」を判定する Pure C# ライブラリ。
  README の定式化がまさに「充足不能性判定」であり、下記 C-3 (最適化クエリ) の中核部品になる。
- **見るべきファイル**:
  - `src/Entail.Core/Constraints/` — 制約型: `RangeConstraint` (区間), `LinearConstraint` (線形),
    `SumConstraint` (総和), `CardinalityConstraint` (N 中 K), `IfThenConstraint` (含意),
    `ChoiceConstraint` (カテゴリ), `CompoundConstraints` (Conjunction/Disjunction)。
    → **QF_LRA (線形実算術) + カテゴリ + 基数の断片を既にカバー**。Quiver の float[] プロパティ上の
    線形制約はここに直接載る。
  - `src/Entail.Core/Solver/Propagator.cs` — 4 フェーズ固定点伝播 (bound tightening → 選言単位伝播
    → Sum 結合 → Cardinality 結合)。制約充足の中核。
  - `src/Entail.Core/Solver/FmFastEngine.cs` — 1-step Fourier–Motzkin 消去。線形不等式系の
    実行可能性判定エンジン。`SumBitsetKey` で変数集合をビットセット化し支配関係を O(1) 判定。
  - `src/Entail.Core/Solver/RedundancyChecker.cs` — 全ブランチ矛盾探索 (DPLL 的)。
  - `README.md` の「既知の検出限界」節 — 部分和推論の未対応、深度上限 20 での Unknown 返却。
    **本格的な最適化には最適化目的関数と分枝限定が未実装**である点を明記している。
- **Quiver への接続**: Match で選んだ部分グラフの数値プロパティに制約を課し、Entail の
  Propagator/FmFastEngine で実行可能性を判定 → C-3 の「制約充足部分グラフクエリ」の即戦力。
  最適化 (目的関数最大化) には分枝限定 or IHS ループ (後述) の追加が必要。

### D:\csharp\LeWorldModel — Pure C# autograd + SIGReg 世界モデル

- **正体**: JEPA 系世界モデル (LeWM, arXiv:2603.19312) の Pure C# 再現。逆モード自動微分 `Tensor`、
  ViT-Tiny encoder + Transformer predictor、SIGReg 崩壊防止正則化を依存ゼロで実装する計画。
- **見るべきファイル**:
  - `docs/解説.md` — SIGReg の理論 (Cramér–Wold の定理 + Epps–Pulley 正規性検定)。
    **潜在ベクトル `Z ∈ R^{N×D}` を等方ガウスに保つ正則化**であり、Quiver に貯める埋め込み集合の
    「分布健全性の検定」として転用できる (C-4 と接続)。
  - `docs/計画書/00_概要と方針.md` — autograd の設計方針、依存ゼロ原則。
  - `src/LeWorldModel.Core` — PointMassEnv デモ (状態遷移の潜在ダイナミクス)。
- **Quiver への接続**: 世界モデルの潜在状態遷移列 `z_t → z_{t+1}` は「時刻ノード + 遷移エッジ +
  行動ロール」の (hyper)graph そのもの。Quiver を**軌跡ストア + 近傍検索インデックス**として
  使う道が D 節。SIGReg の統計は Quiver 側の埋め込み品質監視にも効く。

### D:\csharp\Quiver — 本体の既存資産

- graph (`Vertex` / `Edge` + 双方向走査)、vector (immutable `VectorSegmentIndex` + segment ごとの HNSW)、
  FTS (BM25 + HybridSearch)、`ApplyDyadic` (ダイアディックスコアリング)、
  Nexus (role 付き incidence、`NexusPattern`、`GetNexuses` / `GetMembers`)。
- 現行エンジンは Single Writer + Snapshot Readers であり、公開 identity は
  `VertexId` / `EdgeId` / `NexusId`（Generation 付き）である。
- HNSW の近傍リストは segment 内部の private state である。
  C-5 (TDA) には snapshot 可視な複数 segment から k-NN グラフを構成する adapter が必要になる。

---

## C-1. 最悪ケース最適 join (WCOJ) — 「特定ケースで漸近的に圧倒するクエリ」

### 何が起きるか
三角形パターン `R(a,b) ∧ S(b,c) ∧ T(c,a)` のような cyclic クエリは、binary join を
どう並べ替えても中間結果が Ω(N²) に膨張する。WCOJ は中間結果を作らず AGM bound
(出力サイズの上界) に比例した時間で走る。三角形なら O(N^{1.5})。
**これは実装の巧拙ではなく計算量クラスの差**であり、n 項リレーションをネイティブ格納する
Quiver (hyperedge) だからこそ成立する差別化になる。

### 論文・実装参照 (どこを見るか)
- **AGM bound**: Atserias–Grohe–Marx "Size Bounds and Query Plans for Relational Joins"。
  fractional edge cover LP の最適値が出力サイズ上界を与える定理。**この LP を解く部分に Entail の
  LinearConstraint / FmFastEngine が流用できる** (fractional cover は線形計画)。
- **Leapfrog Triejoin (LFTJ)**: arXiv:1210.0481 (Veldhuizen)。
  → 見るべきは §3–4 の「各変数を全リレーション横断で昇順シークし、最大値へ leapfrog する」中核ループ。
  ソート済みトライ索引さえあれば実装は数百行。中間結果ゼロ・低メモリが売り。
  https://arxiv.org/abs/1210.0481 / 解説: https://relational.ai/resources/leapfrog-triejoin-a-simple-worst-case-optimal-join-algorithm
- **Free Join** (SIGMOD 2023, arXiv:2301.10841, Wang–Willsey–Suciu): WCOJ と binary join を統一する
  枠組み。**実務の acyclic クエリでは素の WCOJ が binary join に負ける**という重要な但し書きを
  与えるのがこの論文。Quiver は「cyclic/dense のときだけ WCOJ 経路、acyclic では既存 binary」という
  ハイブリッド選択をすべき、という設計指針の根拠になる。
  https://arxiv.org/abs/2301.10841 / https://www.mwillsey.com/papers/freejoin

### Quiver 内の接続点
- `NexusPattern` (星型パターン) の複数結合が自然な実装座標。
- 前提: 各リレーション/hyperedge をロール (列) ごとにソートした trie/索引で引けること。
  B+Tree は既にあるので、結合キー順の走査イテレータを用意できるかが鍵。
- クエリオプティマイザに「結合ハイパーグラフが cyclic か」の判定を足し、経路を分岐させる (C-1' 参照)。

### spike の kill criteria (先に数値固定)
- 三角形クエリ (Quiver 上の自己結合 or 3 型 join) で、既存 binary plan と LFTJ の実行時間 crossover 点を
  N (辺数) を振って測る。**N=10⁴ 前後で LFTJ が binary を上回れば本命**。上回らなければ
  「dense/cyclic 専用の隠し経路」に留める。
- メモリ: LFTJ の中間結果ゼロ性を実測 (binary の中間結果ピーク比 ≤ 1/10 を目安)。

---

## C-1'. hypertree 分解 + Yannakakis — 「証明書付きクエリプラン」

### 何が起きるか
結合ハイパーグラフの **hypertree width (幅)** を計算し、acyclic (幅 1) なら Yannakakis で
入力+出力線形時間、幅 k なら O(N^k) の保証が付く。「このクエリは幅 2 だから多項式時間で解ける」
という **計算量の証明書をクエリプランに添付**できる — 組み込み DB では前例がない可観測性。

### 論文・実装参照
- **acyclicity 判定**: GYO reduction (Graham–Yu–Özsoyoğlu)。「1 つの hyperedge にしか現れない頂点を除去
  → 他に包含される hyperedge を除去」を空になるまで反復。空になれば acyclic。実装は極小 (数十行)。
- **Yannakakis algorithm**: acyclic CQ を join tree 上の semi-join 2 パス (bottom-up で dangling tuple
  除去 → top-down) で解く。「A Backtracking-based algorithm for hypertree decomposition」
  (Gottlob–Samer) が分解計算の実装参照。ツール `detkdecomp` が正解照合用。
- Gottlob らの "Hypertree Decompositions and Tractable Queries" (arXiv:cs/9812022) が理論の正本。
  https://arxiv.org/pdf/cs/9812022

### Quiver 内の接続点
- C-1 と同じ `NexusPattern` コンパイラ。プラン生成時に GYO を回して acyclic 判定 →
  acyclic なら Yannakakis 経路、cyclic なら C-1 の WCOJ 経路、という**二段オプティマイザ**。
- 出力: `EXPLAIN` 相当に「width=k, method=Yannakakis/WCOJ」を載せる。

### kill criteria
- GYO + join tree 構築のオーバーヘッドが、クエリ実行時間に対し無視できる (≤5%) こと。
- acyclic な star/path クエリで Yannakakis が naive nested-loop に対し N を振って優位を示すこと。

---

## C-2. 有向 hyperedge = Horn 節 / B-到達可能性 — 「表現力で圧倒するクエリ」

### 何が起きるか
hyperedge の head/tail 意味論 (HYP 計画の有向モデル) は **directed hypergraph** そのもの。
B-connectivity (全ての tail が到達済みのとき head に到達) は **線形時間**で、これは
**Horn-SAT の充足** (Dowling–Gallier) と同一。「AND 依存の到達可能性」— binary グラフの到達
クエリでは原理的に表現できない問いが 1 動詞になる:
- ビルド依存 (全入力が揃えばターゲットがビルド可能)
- 型推論・権限導出 (前提が全て成り立てば結論が導出)
- レシピ/化学反応の合成可能性 (全反応物が揃えば生成物が得られる)
重み付ければ最短 B-hyperpath で「最小コストの導出列」まで出る。

### 論文・実装参照
- **Gallo–Longo–Pallottino–Nguyen "Directed Hypergraphs and Applications"** (Discrete Applied Math, 1993)
  が正本。B-path / F-path / B-connectivity の定義と線形時間到達アルゴリズム、加法的コスト関数での
  最短 B-hyperpath。survey: ScienceDirect S0304397516002097。
- **Dowling–Gallier** (1984): Horn 充足の線形時間アルゴリズム。B-到達と同型なのがポイント。
- 注意: 一般の最短 s-t hyperpath は NP-hard (Italiano–Nanni)。**加法コスト等の劣モジュラ条件下でのみ
  多項式**。実装は「準備完了カウンタ付き forward chaining」= 各 hyperedge に未充足 tail 数を持たせ
  0 になったら発火するワークリスト (Dijkstra 変種)。数十〜百行。
- 応用の実例: "Shortest Hyperpaths in Directed Hypergraphs for Reaction Pathway Inference"
  (Krieger–Kececioglu 2023) — 反応経路推論。細胞シグナル hypergraph の最短 hyperpath ヒューリスティクス
  (PMC9134692) も実装の勘所が載る。

### Quiver 内の接続点
- HYP の有向 hyperedge (tail ロール / head ロール) に対する新走査オペレータ:
  `BReachable(seedNodes)` / `ShortestBHyperpath(from, to)`。
- forward chaining は incidence チェーンを 1 回舐めるだけなので、既存の incidence 走査を再利用可能。
- C-3 の充足可能性 (Entail) と接続: Horn 断片は Entail の IfThenConstraint 連鎖で表現でき、
  「B-到達 = Horn 充足」の等価性を実装レベルで確認できる。

### kill criteria
- N (hyperedge 数) 線形のスケーリングを実測 (二重ループに退化していないこと)。
- binary reification (中間ノード経由) 版と比較し、表現力 (書けるクエリの範囲) と速度の両面で優位を示す。

---

## C-3. 最適化クエリ (SMT/ILP 組み込み) — Entail を核にした本命

### 何が起きるか
「Match で選んだ部分グラフ + float[] プロパティ上の線形/基数制約 + 目的関数 → 最適解」を
1 クエリにする。二大応用:
1. **最小出典集合 (minimum hitting set)**: RAG で「回答を接地する最小の Fact/Chunk 集合」。
   出典ロール付き hyperedge 集合に対する最小 hitting set = NP-hard だが回答単位 (数百) なら瞬時。
   命名調査で退けた Transversal がクエリ動詞 `MinimumTransversal` として復活する照応も良い。
2. **制約充足部分グラフ最適化**: 「予算 ≤ B かつ各カテゴリ最低 1 個を満たす最大スコアの選択」等。
   構成最適化・リソース割当・スケジューリングをグラフクエリとして書く。

### 論文・実装参照
- **Entail が土台** (§0 参照)。QF_LRA + カテゴリ + 基数の充足判定は既にある。**足りないのは目的関数付き
  最適化**。以下のどちらかで拡張:
  - **分枝限定 (branch-and-bound)**: Entail の Propagator を下界計算に使い、最良優先探索。ILP 自作の王道。
  - **Implicit Hitting Set (IHS)**: MaxHS 系。SAT/伝播で「まだ充足されない核 (unsat core)」を取り出し、
    ILP/MaxSAT で最小 hitting set を反復的に解く。**hitting set 最適化にはこれが state-of-the-art**。
    参照: Moreno-Centeno–Karp "The Implicit Hitting Set Approach"、Fazekas–Bacchus–Biere
    "Implicit Hitting Set Algorithms for MaxSMT" (IJCAR18)。PySAT の `hitman` (Hitman) が
    リファレンス実装の読みどころ (minimal/minimum hitting set 列挙)。
    https://pysathq.github.io/docs/html/api/examples/hitman.html
- 正解照合用に Z3 / CP-SAT を「テスト時のみ」使い、Pure C# 実装の解と突き合わせる (依存は増やさない)。

### Quiver 内の接続点
- Match の結果集合 → Entail の ConstraintModel に変数化 (各候補 = BinaryVariable、
  数値プロパティ = NumericVariable) → 最適化ループ。
- `MinimumTransversal(hyperedges, coverRole)` を hyperedge クエリ動詞として公開 (usecase C-3 の
  RAG citation 最小化)。
- SIG スコアを目的関数係数に流用 (候補のスコアを最大化する部分集合選択)。

### kill criteria
- 回答規模 (候補 10²〜10³) で最小 hitting set が 100ms 未満で厳密解に到達すること。
- Entail の現行「深度上限 20 で Unknown」の限界 (README 記載) が最適化ループで顕在化しないか確認。
  顕在化するなら分枝限定側に倒す。

---

## C-4. HodgeRank / 離散 Hodge 分解 — SIG トラックの数学的出口

### 何が起きるか
`ApplyDyadic` のダイアディックスコア (ペア比較) を**辺上の flow** と見なすと、組合せ Hodge 分解で
`flow = gradient (大域順位) ⊕ curl (局所矛盾) ⊕ harmonic (大域矛盾)` に一意分解される。
効果: ペア比較集合から (1) 大域ランキング と (2) **その順位がどれだけ信用できるか (curl/harmonic ノルム)**
が同時に、しかも**線形最小二乗 1 本**で出る (Kemeny 最適化の NP 困難を回避)。

### 論文・実装参照
- **Jiang–Lim–Yao–Ye "Statistical Ranking and Combinatorial Hodge Theory"** (2011) が正本。
  https://arxiv.org/pdf/0811.1067
  → 見るべきは: gradient 部分 = グラフ上の最小二乗 (`min ‖ D s − flow ‖`, D は勾配作用素) で
  大域スコア s を解く。残差を curl (三角形まわりの循環) と harmonic に分ける。
- 実装は疎最小二乗 (共役勾配法) + 三角形列挙のみ。外部依存不要。
- 高次拡張: **Hodge Laplacian on simplicial complexes** ("Signal Processing on Higher-Order Networks",
  arXiv:2101.05510)。hyperedge を単体に持ち上げれば k 次 Hodge Laplacian へ。

### Quiver 内の接続点
- `ApplyDyadic` の出力 (候補ペアのスコア) を flow ベクトルとして受け、グラフ (Edge) 上で分解。
- 新 API: `HodgeRank(edgeFlows)` → (globalScore per node, inconsistency metrics)。
- グラフラプラシアンは Edge の隣接から構成でき、既存の走査で行列-ベクトル積が書ける
  (疎行列を明示構築せず incidence 走査で CG を回す = メモリ効率的)。

### kill criteria
- N ノードのペア比較で CG が反復回数 ≪ N で収束すること (疎性が効く)。
- curl ノルムが「意図的に矛盾を入れたデータ」で有意に上がることを合成データで確認 (指標の妥当性)。

---

## C-5. パーシステントホモロジー (TDA) — 名前照応の本丸

### 何が起きるか
persistence module は数学的に **A_n 型 quiver の表現**であり、barcode への区間分解は
**Gabriel の定理** (有限表現型 ⇔ ADE Dynkin 図形) の系として一意性が保証される。
つまり「Quiver という名の DB が、箙の表現論でベクトルデータのトポロジーを解析する」という
名前と機能が完全一致する唯一のユースケース。
実利: 埋め込み集合の「クラスタが何個あり、どのスケールで安定か」「ループ状構造の有無」を
barcode で返す。しきい値非依存のクラスタリング・外れ値検出・埋め込み品質診断。

### 論文・実装参照
- **Ripser** (Bauer, "Ripser: efficient computation of Vietoris–Rips persistence barcodes",
  J. Applied and Computational Topology 2021, arXiv:1908.02518)。
  https://arxiv.org/abs/1908.02518 / https://github.com/Ripser/ripser
  → 移植時に効く 4 つの最適化を README/論文から読む:
  (1) **cohomology で計算** (Vietoris–Rips の低次では大幅高速)、(2) **clearing/twist** (Chen–Kerber、
  使わないサイクルを計算しない)、(3) **implicit coboundary** (境界行列を明示構築しない)、
  (4) **apparent pairs** (全順序から離散勾配場を作り自明ペアを即消去)。
  Pure C# 移植は「境界行列の R=DV 分解 (column reduction over F_2)」が骨格。F_2 なのでビット演算主体。
- 理論の照応 (persistence = quiver 表現): "Persistence modules" の区間分解定理。Gabriel の定理。

### Quiver 内の接続点
- immutable vector segment ごとの HNSW を入力候補にできる。
  ただし近傍リストは private で、snapshot 全体の k-NN グラフは公開されていないため、
  Vietoris–Rips 濾過には segment 横断 adapter または公開 k-NN 検索からの再構成が要る。
- 新 API: `PersistenceBarcode(vectorIndex, maxDim, maxScale)` → barcodes (次元別 birth/death 区間)。
- 発展: zigzag persistence も A_n 表現 → 時間発展する埋め込み集合 (D 節と接続) のトポロジー追跡。

### kill criteria
- N=10⁴〜10⁵ の埋め込みで、k-NN グラフ (HNSW 経由) からの H0/H1 barcode 構築が実用時間 (数秒) に入るか。
- 全点対 Rips (naive) との barcode 一致を小規模 (N≤500) で検証してから近似 (k-NN 濾過) の誤差を測る。

---

## C-6. 箙表現そのものの格納 / in-DB GNN — 名前を字義通りにする

### 何が起きるか
node に R^d (既存 float[])、edge に線形写像 (d×d 行列プロパティ) を置けば、DB の中身が文字通り
**quiver representation**。message passing = 表現に沿った押し出し (edge の行列を隣接ベクトルに適用して
集約)。in-database GNN 推論を表現論の言葉で定式化できる。ADE 型スキーマなら Gabriel の定理で
直既約分解が有限分類になる。

### 論文・実装参照
- **QPA (Quivers and Path Algebras)**, GAP パッケージ。表現の直既約分解・Auslander–Reiten quiver の
  計算アルゴリズムの参照 (正解照合用、本体には移植しない)。manual: oyvinso.folk.ntnu.no/QPA/manual.pdf
  → 「100 生成元程度で数分、それ以上は timeout」とあるので **重い計算は小規模スキーマ限定**が現実解。
- message passing 側は LeWorldModel の autograd (§0) を流用すれば学習付き GNN まで射程に入る。

### Quiver 内の接続点
- edge プロパティに d×d 行列 codec を追加 (float[] の拡張)。
- `PushForward(representation)` オペレータ = 1-hop message passing。`ApplyDyadic` の経路と隣接。
- 実務価値は C-5/C-4 に劣る (やや衒学的) ため優先度は低。「名前の物語」の完成度要員。

### kill criteria
- まず小規模 (Dynkin A_3〜A_5) で直既約分解が理論と一致するデモが書けるか (機能実証)。
- 実用性能目標は設定せず、in-DB GNN 1-hop が既存 `ApplyDyadic` 経路のオーバーヘッド内に収まるかだけ確認。

---

## C-7. 圏論的データ移行 (CQL / functorial data migration)

### 何が起きるか
スキーマ = quiver 上の自由圏 (+ 可換関係式)、インスタンス = 集合値関手、スキーマ写像 F に沿った
移行 = **Δ_F (引き戻し) ⊣ Σ_F (左 Kan 拡張) / Π_F (右 Kan 拡張)** の随伴三つ組。
マイグレーション/ビューの正しさが**随伴性から証明**される。「スキーマは箙である」を標語でなく機能に。

### 論文・実装参照
- **CQL (Categorical Query Language)**, Spivak–Wisnesky。https://categoricaldata.net/CQL/ +
  GitHub CategoricalData/CQL (Java 参照実装、Kan 拡張の計算エンジンが読みどころ)。
- "Functorial Data Migration: From Theory to Practice" (arXiv:1502.05947)。
  → Δ は単なる射影 (安価)、Σ/Π は Kan 拡張 (Σ は余極限 = disjoint union + quotient、Π は極限)。
- Quiver のスキーマ (ラベル/型) を圏として扱えるかがハードル。可換関係式の管理が重い。

### Quiver 内の接続点
- スキーマ変換・ビュー定義の理論的裏付けとして。実装優先度は最も低い (研究寄り)。
- 現実的には「Δ (引き戻し = スキーマに沿った射影ビュー) だけ実装」が費用対効果の入口。

### kill criteria
- Δ_F (射影ビュー) が既存クエリ合成で書けることの確認から。Σ/Π は実需が出るまで保留。

---

## C-8. 形式概念分析 (FCA)

### 何が起きるか
node × hyperedge の incidence 構造は **formal context** (対象 × 属性の二部関係) そのもの。
Galois 接続から**概念束 (concept lattice)** を構成 = 「共起する対象と属性の極大な組」を全列挙。
用途: 暗黙スキーマ発見、共起パターンマイニング、タグ階層の自動構成、KG の含意規則抽出。

### 論文・実装参照
- **In-Close / Close-by-One (CbO)** 系。Andrews "In-Close, a fast algorithm for computing formal concepts"。
  概説: https://upriss.github.io/fca/fcaalgorithms.html
  → In-Close は incremental closure + ビット行列探索。CbO の辞書式順序で各概念を一度だけ列挙。
  実装は小さく (行列 + 再帰)、外部依存なし。bit-array 版 (arXiv:2111.00003) が高速。
- incidence 表現と formal context が一対一なので、hyperedge ストアからの変換は素直。

### Quiver 内の接続点
- Nexus の incidence ストアを formal context として読み、`ConceptLattice(vertexType, nexusType)` を計算。
- FTS のトークン-文書関係も formal context になる (語 × 文書) → 語彙階層の自動抽出に転用可。

### kill criteria
- 概念数は指数爆発しうる。実データで概念数の分布を測り、iceberg lattice (support しきい値) での
  枝刈りが要るか判断してから API 化。

---

## D. 近接分野への応用 (物理・化学・力学系) — グラフ DB の状態管理としての側面

> グラフ DB のユースケース選定の一環。数理構造が「そのまま」科学計算のデータモデルになる例。
> LeWorldModel のような潜在ダイナミクスの状態管理に Quiver が使える側面を含む。

### D-1. 化学反応ネットワーク / 代謝経路 (有向 hypergraph の典型)
- 反応 `Reaction(reactants…, products…, catalyst)` は directed hypergraph が唯一の自然表現
  (binary では化学量論が落ちる)。**C-2 (B-到達) が「この基質集合から合成可能な生成物」= 経路探索**に直結。
- 参照: 反応ネットワークの hypergraph/Petri net 表現は同型 (S-invariant ↔ hypergraphic oriented matroid)。
  "Petri net approach to persistence in CRNs" (Angeli–De Leeuw–Sontag)、
  Krieger–Kececioglu の反応経路最短 hyperpath (C-2 と同じ)。
- Quiver 接続: hyperedge (tail=反応物, head=生成物, weight=化学量論) + `ShortestBHyperpath` +
  Fact ベクトルで「類似反応検索」。BOM/レシピも構造同型 (usecase-ideas B-6)。

### D-2. 高次相互作用の力学系 (単体複体上のダイナミクス)
- 3 体以上の相互作用 (同期・拡散) は単体複体 + Hodge Laplacian で記述 (C-4 高次版)。
- 参照: "Signal Processing on Higher-Order Networks" (arXiv:2101.05510)、
  "Topology and dynamics of higher-order multiplex networks" (arXiv:2308.14189)。
- Quiver 接続: hyperedge = 単体、Hodge Laplacian の作用を incidence 走査で行列フリーに評価。
  拡散/同期シミュレーションの状態 (各単体上の値) を Quiver に持たせ、時間発展を記録。

### D-3. 世界モデル/潜在ダイナミクスの軌跡ストア (LeWorldModel との接続)
- 潜在状態遷移 `z_t → z_{t+1}` (行動 a_t 条件付き) は「時刻ノード + 遷移エッジ + 行動ロール」の graph。
- **Koopman 作用素 / DMD**: 非線形ダイナミクスを潜在空間の**線形**作用素 A で近似 (最小二乗 1 本)。
  参照: EDMD (Extended DMD)。LeWM の predictor を Koopman 的線形作用素に差し替える道もある。
  → Quiver 接続: 軌跡を貯め、A を最小二乗で推定 (C-4 の CG 基盤と共通)、近傍検索で「似た状態」想起。
- **spectral/consistency**: cellular sheaf Laplacian (Hansen–Ghrist "Toward a Spectral Theory of
  Cellular Sheaves", arXiv:1808.01513) は「ノード/エッジ上のデータの整合性」を測る枠組み。
  分散センサ・マルチビューの一貫性検定に。Quiver のエッジに制限写像 (行列) を持たせれば sheaf 化でき、
  C-6 (箙表現格納) と同じインフラに乗る。
- SIGReg (LeWM 解説.md) の等方ガウス検定は、Quiver に貯めた埋め込み集合の分布健全性監視に転用可 (C-4 隣接)。

### D-4. テンソルネットワーク縮約順序 (hypertree width の物理応用)
- 量子回路シミュレーションの計算量 = テンソルネットワーク line graph の **treewidth**。
  C-1' の hypertree 分解インフラが**そのまま**縮約順序最適化に使える。
- 参照: "Benchmarking treewidth as a practical component of tensor-network quantum simulation"
  (arXiv:1807.04599)、cotengra (hypergraph 分割による縮約木構築)。
- Quiver 接続: テンソル網を hyperedge (テンソル=hyperedge, 添字=node) で格納し、C-1' の分解器で
  縮約順序を出す。ニッチだが「hypertree 分解が物理計算に効く」実例。

### D-5. データ来歴 (provenance semiring) — 科学計算の再現性
- クエリ結果が「どの入力からどう導出されたか」を semiring 注釈で追跡 (Green–Karvounarakis–Tannen
  "Provenance Semirings" PODS07)。導出木の葉のタグの積の和。
- Quiver 接続: Fact hyperedge の出典ロールと組み合わせ、RAG の grounded citation を semiring provenance
  として形式化 (C-3 の hitting set と相補)。B-到達 (C-2) の導出列がそのまま provenance 木になる。

---

## 実装優先順位の提言 (2026-07-04 時点、後続エージェントへの申し送り)

「圧倒するクエリ」× 「実装コスト」× 「Quiver 資産の活用度」で評価:

| 案 | 圧倒性 | 実装コスト | 既存資産活用 | 総合 |
|---|---|---|---|---|
| C-3 最適化 (Entail 核) | 高 (実利直結) | 中 (Entail 拡張のみ) | 高 (Entail + SIG + hyperedge) | **本命** |
| C-2 有向 hyperedge/Horn | 高 (表現力) | 低 (forward chaining) | 高 (incidence 走査) | **本命** |
| C-5 TDA (barcode) | 高 (名前照応) | 中〜高 (Ripser 移植) | 高 (HNSW 近傍グラフ) | **本命 (物語)** |
| C-1/C-1' WCOJ+hypertree | 最高 (漸近優位) | 高 (LFTJ + trie 索引) | 中 (`NexusPattern` 依存) | 有望 (Nexus 成熟後) |
| C-4 HodgeRank | 中 (SIG 出口) | 低 (疎最小二乗) | 高 (SIG) | 有望 (軽い) |
| C-6/C-7/C-8 | 中〜低 (衒学寄り) | 中〜高 | 中 | 研究・物語要員 |
| D-1〜D-5 応用 | — | 上記の再利用 | — | ショーケース/docs 素材 |

推奨着手順:
1. **C-2 (有向 hyperedge / B-到達)** — Nexus の有向モデルの正当な出口。実装最小。D-1 化学反応と直結。
2. **C-3 (Entail 最適化)** — Entail に分枝限定 or IHS を足して `MinimumTransversal`。RAG citation 最小化で実利。
3. **C-4 (HodgeRank)** — SIG の出口として軽量に。C-3 と共有する疎最小二乗 (CG) 基盤を先に作る。
4. **C-5 (TDA barcode)** — HNSW 近傍グラフを濾過に使う spike。Quiver の名前を体現する旗艦機能。
5. **C-1/C-1' (WCOJ)** — `NexusPattern` 成熟後。最も強い漸近優位だが前提が重い。

各案とも本実装前に上記 kill criteria を数値固定して spike すること ([[empirical-verification-over-reasoning]])。

---

## 参照リンク一覧

- Leapfrog Triejoin: https://arxiv.org/abs/1210.0481
- Free Join (SIGMOD 2023): https://arxiv.org/abs/2301.10841 / https://www.mwillsey.com/papers/freejoin
- Hypertree Decompositions and Tractable Queries: https://arxiv.org/pdf/cs/9812022
- Directed Hypergraphs (Gallo et al.) survey: https://www.sciencedirect.com/science/article/pii/S0304397516002097
- Shortest Hyperpaths for Reaction Pathway (Krieger–Kececioglu 2023): https://par.nsf.gov/servlets/purl/10530392
- Cell signaling hyperpaths (PMC): https://pmc.ncbi.nlm.nih.gov/articles/PMC9134692/
- Implicit Hitting Set for MaxSMT (Fazekas–Bacchus–Biere): https://kfazekas.github.io/papers/FazekasBacchusBiere-IJCAR18.pdf
- PySAT Hitman (hitting set reference): https://pysathq.github.io/docs/html/api/examples/hitman.html
- Statistical Ranking & Combinatorial Hodge Theory (HodgeRank): https://arxiv.org/pdf/0811.1067
- Signal Processing on Higher-Order Networks: https://arxiv.org/pdf/2101.05510
- Ripser: https://arxiv.org/abs/1908.02518 / https://github.com/Ripser/ripser
- QPA (GAP): https://oyvinso.folk.ntnu.no/QPA/manual.pdf
- CQL / Functorial Data Migration: https://categoricaldata.net/CQL/ / https://arxiv.org/pdf/1502.05947
- In-Close / FCA algorithms: https://upriss.github.io/fca/fcaalgorithms.html
- Cellular Sheaf Laplacian (Hansen–Ghrist): https://arxiv.org/abs/1808.01513
- Treewidth in tensor-network simulation: https://arxiv.org/abs/1807.04599
- Provenance Semirings (Green–Karvounarakis–Tannen): https://web.cs.ucdavis.edu/~green/papers/pods07.pdf
- Chemical Reaction Networks / Petri nets (Angeli–De Leeuw–Sontag): http://www.sontaglab.org/FTPDIR/angeli_leenheer_sontag_math_biosciences_MBS-D-06-00188R1.pdf
- Koopman / EDMD linear predictors: (EDMD, arXiv:1810.01479 ほか)
