# 数学的ユースケース 研究資料 (コーナーストーン)

> 起案日: 2026-07-04。plans/usecase-ideas.md C 節 (数学的構造を正面に出すユースケース) を起点に、
> 一次資料の Web 調査と手元資産 (Quiver / Entail / LeWorldModel) の照合を行った検討用資料。
> **後続エージェントが各テーマの実装検討を始めるときの足掛かり**として書かれている。
> 実装判断は都度 (spike → kill criteria 実測 → 本実装 の順を守ること。「推論より実地検証」)。

## 0. 手元資産の棚卸し

各テーマの「実装の道筋」はここで定義する資産名を参照する。

### Quiver (D:\csharp\Quiver) — 本体

- **VersionedNexusStore / IncidenceStore**:
  ロール付き incidence モデル。incidence は `NexusId | VertexId | RoleId | NextInVertex | NextInNexus`
  の固定レコードで、「Vertex ごとのチェーン」「Nexus ごとのメンバーチェーン」の 2 本を貫通。
  メンバー集合は作成時確定 (immutable-first)。有向は head/tail ロールで表現。
- **GraphKernel**: `VisitNeighbor(source, target, edgeId)` 契約の走査カーネル。BFS/最短経路/Dijkstra は
  binary view を供給すれば無改造で動く。
- **Match / GraphPattern**: 線形 Vertex-Edge-Vertex パターン + 星型 `NexusPattern`。
- **B+Tree / ページング / Clock buffer pool**: 8KB ページ、順序付き走査が可能。
- **VectorSegmentIndex + HNSW**: immutable vector segment ごとの float[] 格納と private な近傍リスト。
  snapshot 可視な複数 segment の検索結果を統合するが、snapshot 全体の近傍グラフは公開しない。
- **ApplyDyadic**: traversal の候補集合 × float[] プロパティのダイアディックスコアリング。
  エンジンオーバーヘッド 0.148µs/候補。
- **Single Writer + Snapshot Readers / redo-only WAL / savepoint**: 読み取り開始時点の snapshot による可視性。

### Entail (D:\csharp\Entail) — 充足可能性の予備実装

制約集合に対する**冗長性判定器** (P_new が既存制約から導出されるか = 既存 ∧ ¬P_new が UNSAT か)。
汎用 SMT ソルバ非依存のスクラッチ実装。テーマ 4 (SMT 統合) の直接の種。

- `Entail.Core/Constraints/`: Range (数値区間) / Choice (カテゴリ) / Sum (線形和) /
  Cardinality (N 中 K) / IfThen / BinaryFix / Conjunction / Disjunction。
  否定の構築規則を各制約が持つ (例: `x∈[a,b]` の否定 = `x<a ∨ x>b` の選言)。
- `Solver/Propagator.cs`: 4 フェーズ固定点伝播 (個別 bound tightening → 選言の単位伝播 →
  同一変数集合 Sum 結合 → 同一変数集合 Cardinality 結合)。
- `Solver/RedundancyChecker.cs`: 全ブランチ矛盾探索 (DPLL 風ブランチ探索、MaxDepth=20)。
- `Solver/FmFastEngine.cs`: 1-step Fourier–Motzkin 派生 + SumBitsetKey による支配インデックス
  (同一変数集合の派生は最も厳しい Limit のみ保持、N>32 の Sum は bailout)。
- **持っていないもの** (テーマ 4 で必要になる差分): 目的関数と分枝限定 (最適化)、
  UNSAT コア抽出 (conflict 分析)、部分和推論 (異なる変数集合をまたぐ導出 — README 既知限界)、
  深度上限の撤廃または動的化。
- **偽陽性なし・偽陰性あり** (保守的) という性質は「クエリフィルタの健全な緩和」として好都合:
  制約でプルーニングし切れなくても結果は正しい側に倒れる。

### LeWorldModel (D:\csharp\LeWorldModel) — 世界モデル状態管理の適用先

LeWM 論文 (Maes–Le Lidec–Scieur–LeCun–Balestriero, arXiv:2603.19312) の Pure C# 再現。
JEPA: `z_t = enc(o_t)`, `ẑ_{t+1} = pred(z_t, a_t)`、損失は予測 L2 + SIGReg (等方ガウス正則化) の 2 項。
CEM プランニング。テーマ 12b の適用先。Quiver 側から見ると:

- 潜在状態 z_t は float[] — Quiver のベクトル資産がそのまま状態表現になる
- 遷移 (z_t, a_t, z_{t+1}) は本質的に n 項ファクト — hyperedge の適用形
- CEM プランニングは「似た状態からの過去の良い行動系列」で warm-start できる — HNSW の適用形

---

## 1. 最悪ケース最適 join (WCOJ) — hyperedge 格納が前提条件

### 目的と主張

三角形など cyclic な結合パターンで、binary join プランは中間結果が Ω(N²) に膨れるのに対し、
WCOJ は出力サイズの理論上界 (AGM bound) に比例した時間で走る (三角形なら O(N^{3/2}))。
これは実装の巧拙ではなく**定理による漸近優位**であり、n 項リレーション (hyperedge) を
ネイティブに持つエンジンだけが実装できる。

### 一次資料

- AGM bound: Atserias–Grohe–Marx, *Size Bounds and Query Plans for Relational Joins* (FOCS 2008)。
  出力サイズ ≤ Π|R_e|^{x_e} (x = fractional edge cover、LP 双対で AGM 上界)。
- NPRR: Ngo–Porat–Ré–Rudra (PODS 2012) — 上界を達成する最初のアルゴリズム。
- **Leapfrog Triejoin** (実装の教科書): Veldhuizen, [arXiv:1210.0481](https://arxiv.org/abs/1210.0481)。
  「理解も実装も簡単、最適性証明も短い」と自称し実際そう。LogicBlox の実戦実装。
- **Free Join** (最新形): Wang–Willsey–Suciu (SIGMOD 2023), [arXiv:2301.10841](https://arxiv.org/abs/2301.10841)。
  binary join と WCOJ を統一する枠組み (COLT データ構造 = hash trie の遅延構築)。
  acyclic クエリで binary に負けない WCOJ という実用上の急所を解決している。
  [SIGMOD Record 解説](https://dl.acm.org/doi/10.1145/3665252.3665259) も併読。

### 実装の道筋 (何を見ながらどこを)

1. **LFTJ 論文の §3 (leapfrog join) をまず単変数で実装する**。必要なのは
   「ソート順 iterator + seek(値) 」だけ。Quiver では B+Tree の順序付き走査がこれに相当する。
   IncidenceStore のチェーンは挿入順で**ソートされていない**ので、
   (a) role 別ソート済み二次索引 (B+Tree キー `(RoleId, VertexId, NexusId)`) を足すか、
   (b) 走査時ソート (小さい次数なら十分) で始めるかの選択になる。spike は (b) で足りる。
2. trie の「レベル」= 変数順序。`NexusPattern` の変数 (Vertex 変数・Nexus 変数) に
   全順序を与え、各リレーションをその順序の trie と見なす。Generic Join
   (NPRR の簡略形、Ngo–Ré–Rudra *Skew Strikes Back* が読みやすい) の再帰構造で書くと小さい。
3. binary Edge も「アリティ 2 のリレーション」として同じ枠に載せる
   (計画書の lifting ビューがここで効く)。三角形クエリは Edge 3 本でも作れるので、
   **spike は Nexus 経路とは独立に binary 3-clique で開始できる**。
4. 本実装フェーズでは Free Join の COLT (列遅延 hash trie) を検討。
   Quiver はページ常駐ストアなので「trie をどこまで具現化するか」が設計の勘所。

### kill criteria 案 (spike 時に数値を固定)

- 三角形列挙 (エッジ数 N=10^6、一様 + Zipf): WCOJ 経路が既存 Match (binary plan) 比 **≥5×**。
- acyclic クエリ (path-2/path-3) で既存比 **≥0.8×** (劣化 20% 以内、Free Join 不使用の許容ライン)。

### 規模感・依存

spike は独立 (binary で可能)。`NexusPattern` は実装済み。Quiver のクエリ層に
新オペレータ 1 個 (GenericJoinOperator) + ソート済み隣接の用意。

---

## 2. Hypertree 分解 + Yannakakis — 証明書付きクエリプラン

### 目的と主張

結合パターンのハイパーグラフが acyclic (hypertree width 1) なら Yannakakis で
O(入力 + 出力)、幅 k 有界なら多項式時間。オプティマイザが「このパターンは幅 2 なので
この計画で速い」という**構造的証明書**を出せる。テーマ 1 と補完的
(cyclic → WCOJ、低幅 → 分解 + Yannakakis)。

### 一次資料

- Gottlob–Leone–Scarcello, *Hypertree Decompositions and Tractable Queries*,
  [arXiv:cs/9812022](https://arxiv.org/pdf/cs/9812022) — 定義と複雑性の正本。
- GYO 簡約 (acyclicity 判定): 「1 つの hyperedge にしか現れない頂点を消す /
  他に包含される hyperedge を消す」を固定点まで。空になれば acyclic。実装は 30 行程度。
- Yannakakis (VLDB 1981): semi-join で上下 2 パスして dangling tuple を落としてから join。
- 分解の計算: det-k-decomp (Gottlob らのバックトラック法、
  [解説スライド](https://databasetheory.org/sites/default/files/2016-07/HD-QA.pdf))。
  クエリパターンは高々数十 hyperedge なので厳密計算で十分。

### 実装の道筋

1. **GYO 簡約から書く** (Match コンパイラ内の解析パス)。acyclic なら join tree が
   同時に得られ、Yannakakis 実行計画に直結する。
2. 幅 ≥2 は「hyperedge 数 ≤ 20 程度の全探索 + メモ化」で det-k-decomp の簡略版を書けば足りる。
   汎用ソルバは不要 (クエリサイズは小さい)。
3. テーマ 12c (テンソルネットワーク) と**同じ分解エンジンを共有できる** — 「Quiver.Decomposition」
  的な internal モジュールに tree/hypertree 分解を置く価値がある。

### kill criteria 案

- 星型 2 個連結の acyclic パターン (N=10^6) で Yannakakis 経路が素朴プラン比 ≥3× (dangling 多発データ)。

---

## 3. 有向 hyperedge = Horn 節 — B-到達可能性・最短 B-hyperpath

### 目的と主張

head/tail ロールの有向 hyperedge は directed hypergraph そのもので、
「tail の**全**ノードが到達済みなら head に到達できる」という **AND 到達 (B-到達)** は
binary グラフの到達では表現不能。B-到達は**入力サイズ線形**で判定でき、これは
Horn-SAT (Dowling–Gallier) と同一の計算。応用: ビルド依存・権限導出・型推論・
レシピ/反応の合成可能性 (テーマ 12a に直結)。

### 一次資料

- Gallo–Longo–Pallottino–Nguyen, *Directed Hypergraphs and Applications*
  (Discrete Applied Math 1993, [Semantic Scholar](https://www.semanticscholar.org/paper/Directed-Hypergraphs-and-Applications-Gallo-Longo/158b6f53220b212027c3ffcea56d062d61f9ffd5)) — 正本。
  B-path / B-connectivity の定義、線形時間到達、加法的コストの最短 B-hyperpath (SBT 手続き)。
- サーベイ: Ausiello–Laura, *Directed hypergraphs: Introduction and fundamental
  algorithms* ([ScienceDirect](https://www.sciencedirect.com/science/article/pii/S0304397516002097))。
- **要注意の境界**: 一般の s-t 最短 hyperpath は NP-hard (Italiano–Nanni)。
  ポリノミアルなのは B-path + 加法的/単調コストの組。応用寄りの実装例は
  細胞シグナル伝達の最短 hyperpath 研究
  ([Krieger–Kececioglu](https://pmc.ncbi.nlm.nih.gov/articles/PMC9134692/)) が実装込みで参考になる。

### 実装の道筋

1. アルゴリズムはカウンタ法 1 本: 各 hyperedge に「未到達 tail 数」カウンタを持ち、
   ノード到達時にデクリメント、0 になったら head をキューへ。Dowling–Gallier の
   unit propagation と同じ構造。**IncidenceStore の NextInVertex チェーン (role=tail) の
   走査がそのまま「ノード→関与 hyperedge」列挙**なので、追加索引は不要。
2. 最短 B-hyperpath は上記を Dijkstra 化 (Gallo らの SBT)。コスト関数は
   sum (加法) と max (ボトルネック) の 2 種を用意すると応用が広い。
3. DSL は `ReachableBy(nexusType, tailRole, headRole)` /
   `ShortestDerivation(...)` のような動詞になる (命名は hyperedge-track の原則に従い再検討)。
4. Entail の Phase 2 (選言の単位伝播) と概念的に同じ計算であることを意識しておく —
   将来「グラフ上の Horn 推論」と「制約伝播」を統合するときの接合点。

### kill criteria 案

- 合成 Horn 網 (hyperedge 10^6、平均 tail 3): B-到達全列挙が **入力線形** (10^6 で <1s / 傾き一定)。

---

## 4. SMT/制約統合 — Entail を核に、MinimumTransversal から入る

### 目的と主張

(a) Match に float[]/数値プロパティ上の算術制約 (QF_LRA 断片) を導入しプルーニングに使う。
(b) 最適化クエリの第一弾として **MinimumTransversal (最小 hitting set)** =
「回答を接地する最小の出典集合」を厳密に解く。NP-hard だが回答単位の実サイズ (数百 fact) では
厳密解が即答できる。命名調査で退けた Transversal がクエリ動詞として復活する。

### 一次資料

- **Implicit Hitting Set (IHS) 法**: Moreno-Centeno–Karp
  ([Operations Research 2013](https://pubsonline.informs.org/doi/abs/10.1287/opre.1120.1139))、
  MaxSAT への適用 = MaxHS (Davies–Bacchus)。SAT ソルバで core を掘り、
  MIP で最小 hitting set を解く分業構造
  ([IJCAR18 の SMT 版](https://kfazekas.github.io/papers/FazekasBacchusBiere-IJCAR18.pdf))。
- 実装の手本: [PySAT Hitman](https://pysathq.github.io/docs/html/api/examples/hitman.html)
  (最小/極小 hitting set 列挙器のリファレンス実装)。
- Fourier–Motzkin と QF_LRA: Entail の FmFastEngine が既に 1-step FM を持つ。
  完全な FM は二重指数だが、bound propagation + 1-step 派生は健全な近似
  (Entail README「偽陽性なし」の設計と同じ立場)。

### 実装の道筋

1. **MinimumTransversal は IHS 構造を使わずとも書ける**: 出典最小化の実サイズなら
   (a) greedy (ln n 近似) を初期上界に、(b) 分枝限定 (次数最大の fact を選ぶ分枝) で厳密化。
   hyperedge の incidence チェーン走査 (最小次数ノード起点) が候補生成そのもの。
   まずこれを Quiver 内の小さな専用ソルバとして書き、汎用化はその後。
2. **Entail の転用先は Match の制約フィルタ**: `Has(x, p => p.Score + q.Weight <= 10)` 級の
   線形制約を Entail の RangeConstraint/SumConstraint に写像し、Propagator の bound tightening を
   候補域の縮小 (index range scan の境界計算) に使う。「偽陰性あり」でもフィルタとしては健全。
3. Entail 側の拡張課題 (Quiver から要求が出た順に):
   目的関数 + 分枝限定 (最適化)、UNSAT コア抽出 (IHS の core 掘りに必要)、
   MaxDepth=20 の撤廃 (restart/学習なしでの深掘りは危険 — conflict 学習の導入検討)。
4. Cardinality 制約が既にあるので「サイズ ≤ k の hitting set が存在するか」を
   Entail の充足判定 + k の二分探索で解く構成も可能 (spike 比較対象として安価)。

### kill criteria 案

- fact 500 / 出典 200 の合成 RAG 回答で厳密 MinimumTransversal **<100ms** (greedy 比の解サイズ改善率も記録)。

---

## 5. Provenance 半環 — 出典管理の代数 (RAG との親和が最も高い理論)

### 目的と主張

Green–Karvounarakis–Tannen の半環 provenance は「クエリ結果の各タプルに、
それがどの入力からどう導出されたかの多項式 (how-provenance) を注釈する」枠組み。
bag 意味論・確率 DB・信頼度伝播・why-provenance がすべて**半環の取り替え**で出る。
Quiver の Fact hyperedge (出典ロール) と MinimumTransversal (テーマ 4) の理論的上屋であり、
「grounded citation の代数」として RAG 差別化の言語になる。

### 一次資料

- Green–Karvounarakis–Tannen, *Provenance Semirings*
  ([PODS 2007](https://web.cs.ucdavis.edu/~green/papers/pods07.pdf)) — 正本。
- グラフ DB への適用: [The Semiring-Based Provenance Framework for Graph Databases](https://www.researchgate.net/publication/366286831_The_Semiring-Based_Provenance_Framework_for_Graph_Databases)、
  [Efficient Provenance-Aware Querying of Graph Databases](https://dl.acm.org/doi/pdf/10.1145/3534540.3534689)。
- Datalog 拡張 (再帰): [Revisiting Semiring Provenance for Datalog](https://arxiv.org/pdf/2202.10766)。

### 実装の道筋

1. 最小形: 走査オペレータに「注釈合成フック」を 1 個足す — 経路の連接 = ⊗、
   代替経路の合流 = ⊕。半環をジェネリクス (`ISemiring<T>`) にすれば
   (a) 到達可能性 = ブール半環、(b) 最短路 = tropical 半環 (= 既存 Dijkstra の一般化)、
   (c) why-provenance = 出典集合の冪集合半環、(d) 信頼度 = Viterbi 半環、が同一コードで出る。
   **既存の重み付き最短経路が tropical 半環の特殊化である**ことを設計文書に明記すると座りが良い。
2. why-provenance (出典集合) を返すクエリ → その最小化がテーマ 4 の MinimumTransversal。
   2 テーマは 1 本の柱の上下として実装順を組む。
3. 再帰 (BFS/到達可能性) では ω-連続半環が要る — 実装上は「不動点まで反復 + 吸収元で打ち切り」。

### kill criteria 案

- 注釈フック追加後、注釈なし経路のオーバーヘッド **≤5%** (ジェネリクス特殊化で消えることの確認)。

---

## 6. HodgeRank / 離散 Hodge 分解 — ApplyDyadic の数学的出口

### 目的と主張

`ApplyDyadic` のダイアディックスコア (候補対のスコア) は有向グラフの辺上の flow と見なせる。
組合せ Hodge 分解 flow = gradient ⊕ curl ⊕ harmonic により、
**大域ランキング (gradient 成分のポテンシャル) と、その信頼度 (curl/harmonic 残差)** が
最小二乗 1 本で同時に得られる。Kemeny 最適化 (NP-hard) と違い線形代数で済むのが要点。

### 一次資料

- Jiang–Lim–Yao–Ye, *Statistical ranking and combinatorial Hodge theory*
  ([arXiv:0811.1067](https://arxiv.org/pdf/0811.1067)) — 正本。§2 (グラフ Helmholtzian)、
  §5 (最小二乗定式化) を実装の指針にする。
- 高次化 (hyperedge → 単体複体): Schaub ら *Signal Processing on Higher-Order Networks*
  ([arXiv:2101.05510](https://arxiv.org/pdf/2101.05510)) — Hodge Laplacian L_k = B_k^T B_k + B_{k+1} B_{k+1}^T
  の実装視点サーベイ。テーマ 12d と共通資料。

### 実装の道筋

1. 入力: `ApplyDyadic` が返す対スコア Y_ij (歪対称化)。グラフ勾配 (grad s)_ij = s_j − s_i に対し
   min_s Σ w_ij (Y_ij − (grad s)_ij)² を解く — 正規方程式は **L_0 s = −div Y**
   (グラフ Laplacian 系の疎線形系)。共役勾配 (CG) で解く。前処理は不要か Jacobi で十分。
2. curl 残差は三角形 (2-clique) 上の巡回和。三角形列挙は**テーマ 1 の WCOJ 三角形クエリの再利用**。
3. API 形: `RankByHodge(candidates, dyadicScorer)` → (score[], inconsistency)。
   `GraphTraversal` / `TypedGraphTraversal` の `ApplyDyadic` 結果に接続する。
4. 疎行列 CG は Pure C# で 200 行程度。System.Numerics.Tensors の TensorPrimitives が使える
   (LeWorldModel の autograd 資産とも共有可能)。

### kill criteria 案

- 候補 10^4・辺 10^5 の合成データで CG 収束 **<500ms**、既知の埋め込み順位との Kendall τ が
  BTL 最尤推定と同等以上。

---

## 7. パーシステントホモロジー (TDA) — 名前照応の本丸

### 目的と主張

persistence module は **A_n 型 quiver の表現**であり、barcode への区間分解は
Gabriel の定理の系。Quiver は immutable vector segment ごとに HNSW を持つが、近傍リストは private である。
Vietoris–Rips 濾過には snapshot 全体の k-NN グラフを構成する adapter が要る。
「埋め込み集合のクラスタ数と安定スケール」を barcode で返す。
機能価値と同時に「Quiver という名の DB が箙の表現論でベクトルを解析する」物語になる。

### 一次資料

- **Ripser** (実装の最高峰): Bauer, *Ripser: efficient computation of Vietoris–Rips
  persistence barcodes* ([Springer](https://link.springer.com/article/10.1007/s41468-021-00071-5) /
  [GitHub](https://github.com/Ripser/ripser))。読むべき技法 4 点:
  (1) **コホモロジー計算** (双対で列簡約が激減)、(2) **clearing** (Chen–Kerber; 使われない
  サイクル計算の回避)、(3) **暗黙的余境界行列** (組合せ数系で simplex を整数エンコードし
  行列を実体化しない)、(4) **apparent pairs** (離散勾配場による零コスト対の即時判定)。
- 0 次元だけなら Union-Find (= 単一リンケージ樹形図) で済む — **最初の spike はここ**。

### 実装の道筋

1. **段階 0**: H_0 persistence = 距離順に辺を足す Kruskal + Union-Find。segment 内部の近傍または
   公開 k-NN 検索から snapshot 全体の k-NN グラフを構成し、辺長ソートする。
   private state の直接公開は前提にせず、adapter の境界を spike で決める。
   実装 1 日規模。API 形: `PersistenceDiagram(vectorIndex, maxDim: 0)`。
2. **段階 1**: H_1 (ループ)。Ripser の 4 技法のうち (1)(2) だけ入れた簡易版でも
   N=10^3〜10^4 は動く。simplex は k-枝の組合せ数系エンコード (Ripser §3)。
3. 厳密な Rips は全対距離が要る。kNN グラフ由来の**疎 Rips** は近似になる —
   近似保証を明記するか、N が小さければ全対を SIMD で計算 (SIG の距離カーネル再利用)。
4. zigzag persistence (向きが交互の A_n) は将来枠。動的データの「時間変化する barcode」に対応。

### kill criteria 案

- N=10^4・d=384 の実埋め込みで H_0+H_1 barcode **<5s** (段階 1)、H_0 のみなら **<300ms** (段階 0)。
- 既知クラスタ構造 (合成 GMM) の復元精度: クラスタ数一致率 ≥95%。

---

## 8. Cellular sheaf Laplacian — 「一貫性」の物理

### 目的と主張

グラフの各ノード/辺にベクトル空間と制限写像を割り当てる cellular sheaf 上の
Laplacian は、「局所データの大域一貫性」を測る作用素。調和的な断面 = 完全に整合する割当。
センサ融合・意見力学・分散推定の統一言語で、Quiver では「辺が線形写像を運ぶ」
(テーマ 9 と同じ格納形) の応用形。ノード float[] が既にあるので追加格納は辺行列のみ。

### 一次資料

- Hansen–Ghrist, *Toward a Spectral Theory of Cellular Sheaves*
  ([arXiv:1808.01513](https://arxiv.org/abs/1808.01513)) — 正本。
- 入門: Hansen, [*A gentle introduction to sheaves on graphs*](https://www.jakobhansen.org/publications/gentleintroduction.pdf) — 実装者はこちらから。

### 実装の道筋

1. 格納: 辺プロパティに制限写像 2 本 (F_{u→e}, F_{v→e}、d×d float[])。sheaf Laplacian は
   L_F = δ^T δ (δ は制限写像の差)。乗算はテーマ 6 と同じ疎 CG 基盤に載る。
2. クエリ形: `HarmonicExtension(labeledNodes)` — 少数ノードの既知値から残りを調和拡張
   (= Dirichlet 問題、Zhu らのラベル伝播の sheaf 一般化)。半教師分類・欠損補完が 1 クエリ。
3. 一貫性スコア: `‖δ x‖` をノード/辺別に返せば「どの観測が矛盾源か」の診断になる
   (テーマ 12b のセンサ融合と接続)。

---

## 9. Quiver 表現の第一級格納 — in-DB GNN と表現論

### 目的と主張

node に R^d (既存 float[])、edge に線形写像 (d'×d 行列プロパティ) を置くと
DB の中身が文字通り quiver representation になる。message passing は表現に沿った押し出しで、
in-database GNN 推論に表現論的定式化が付く。ADE 型スキーマなら Gabriel の定理により
直既約分解が有限分類。

### 一次資料

- 計算実装の参照: **QPA** (GAP パッケージ、[マニュアル](https://oyvinso.folk.ntnu.no/QPA/manual.pdf))。
  表現の直既約分解・AR quiver の計算アルゴリズムが実装済み (有限体上)。
  実数値では厳密分解は数値的に脆いので、**分解は「教材/診断」用途、推論 (押し出し) が実用**
  という切り分けを最初にしておく。
- 直既約の再帰構成: [arXiv:1310.2757](https://arxiv.org/pdf/1310.2757)。

### 実装の道筋

1. 行列プロパティ codec (float[] + shape) を PropertyStore に足す — これだけで格納は完了。
2. 押し出し演算: `PushForward(startNodes, steps)` — 各ステップで辺行列を乗じて集約 (sum/mean)。
   SIG の SIMD 距離カーネルと同系の内積ループ。ONNX 級の汎用推論は**非目標**とし、
   「グラフ構造と一体の線形層」に限定する (LeWorldModel との役割分担)。
3. テーマ 8 (sheaf) と格納・乗算基盤が同一。実装順は 9 → 8 が素直。

---

## 10. 圏論的データ移行 (CQL / Spivak)

### 目的と主張

スキーマ = quiver 上の自由圏 (+ 可換関係式)、インスタンス = 集合値関手、
スキーマ写像 F に沿った移行 = Σ_F ⊣ Δ_F ⊣ Π_F (左 Kan ⊣ 引き戻し ⊣ 右 Kan)。
マイグレーション/ビューの正しさが随伴で保証される。「スキーマは箙である」を機能にする。

### 一次資料

- リファレンス実装: [CQL IDE](https://categoricaldata.net/CQL/) ([GitHub](https://github.com/CategoricalData/CQL)、Java) — 左/右 Kan 拡張の計算を含む。
- Spivak–Wisnesky, *Functorial Data Migration: From Theory to Practice*
  ([arXiv:1502.05947](https://arxiv.org/pdf/1502.05947))。

### 実装の道筋 (慎重に)

左 Kan 拡張の計算は本質的に chase (存在規則の飽和) で、停止性が非自明。
**フルの CQL 互換は追わない**。現実的な切り出しは:
(a) スキーマ写像 (ラベル/型のリネーム・畳み込み) の宣言 + Δ (引き戻し = ビュー) のみ実装、
(b) Σ は「余極限 = ID 付け替えの union-find」で書ける無関係式ケースに限定。
これだけでも「検証可能なスキーマ進化」として未リリース期のクリーンブレイク運用と相性が良い。

---

## 11. 形式概念分析 (FCA) — incidence の Galois 接続

### 目的と主張

node × hyperedge の incidence 構造は formal context そのもの。概念束 (Galois 接続の閉集合対) の
列挙は「暗黙のスキーマ発見」「共起パターンの極大集合」を返す。閉集合 = 頻出アイテム集合の
closed itemset と同値なのでデータマイニング的実利もある。

### 一次資料

- **In-Close** 系が実装の定番: Andrews, [*In-Close, a fast algorithm for computing formal concepts*](https://www.semanticscholar.org/paper/In-Close,-a-fast-algorithm-for-computing-formal-Andrews/fd9c562143c9505778b15299caf58fa2ebcf86bb)。
  CbO (Close-by-One) の канonicity テスト + ビット行列。アルゴリズム比較は
  [upriss の FCA アルゴリズム一覧](https://upriss.github.io/fca/fcaalgorithms.html)。

### 実装の道筋

IncidenceStore のエッジ側チェーン (GetMembers) をビット集合化 → In-Close の閉包計算は
ビット AND の連鎖。出力は概念 (extent=ノード集合, intent=hyperedge 集合) のストリーム。
`MineConcepts(hyperType, minExtent, minIntent)` のような動詞。指数爆発があるので
minsupport 相当の足切りを必須引数にする。

---

## 12. 物理・化学への応用 (グラフ DB としての近接分野展開)

### 12a. 化学反応ネットワーク (CRN) — 有向 hyperedge の教科書的実在物

反応 `2A + B → C` は係数付き有向 hyperedge そのもの (tail={A,A,B}, head={C})。
CRN は Petri net と同型 ([Petri net によるモデリングのサーベイ](https://www.math.unipd.it/~baldan/Papers/Soft-copy-pdf/BioPN.pdf)、
[hypergraph 表現の議論](https://arxiv.org/pdf/2507.09943))。Quiver でできること:

- **合成可能性クエリ = テーマ 3 の B-到達**: 「この前駆体集合から合成可能な分子は何か」
  「目標分子への最小コスト反応経路」(最短 B-hyperpath)。逆合成解析の基本形であり、
  実装はテーマ 3 と完全共有。細胞シグナル伝達での実例:
  [Krieger–Kececioglu](https://pmc.ncbi.nlm.nih.gov/articles/PMC9134692/)。
- 保存量 (P/T-invariant) = 化学量論行列の核
