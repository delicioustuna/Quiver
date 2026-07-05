# ユースケース アイデアシート (参考)

> 起案日: 2026-07-04。ブレスト記録であり、トラック計画ではない (実装判断は都度)。
> hyperedge の正本計画は plans/hyperedge-track.md。数学系アイデアは本シート C 節に集約。

## A. float[] 資産の横展開 (hyperedge 非依存)

graph + vector + FTS を単一ファイル in-process で持つ .NET 製エンジンがほぼ存在しない、
という点を差別化の核とする。

1. **エージェント長期記憶 (episodic memory)** — 会話イベント/観測をノード、関係をエッジ、
   想起はベクトル類似 + 時間減衰・重要度スコアリング (SIG のダイアディック演算が直用可)。
   エージェントごとに 1 つの .quiver ファイルという配布形態。
2. **ローカル推薦エンジン** — item embedding (content-based) × 行動グラフ (協調フィルタ) の
   ハイブリッドを 1 クエリで。graph + vector 両持ちならでは。
3. **時系列パターンマッチ / 異常検知** — センサー窓を float[] にして kNN、正常からの距離 = 異常度。
   機器トポロジをグラフで併置できるのが既存ベクトル DB との差。SIG の低オーバーヘッドが効く。
4. **オーディオ指紋・音響類似検索** — MFCC / chroma / 埋め込みで効果音・サンプル検索、重複検出。
5. **ゲーム AI の記憶** — NPC episodic memory + 社会関係グラフ。Godot C# / MonoGame に
   組み込める embedded DB は稀。セーブデータ = 単一 .quiver。
6. **写真/メディアライブラリ** — CLIP 埋め込み + 人物/アルバムグラフ + EXIF/キャプション FTS。
7. **kNN 分類器 / ローカル特徴量ストア** — ラベル付きベクトルの最近傍分類 (モデル不要の
   オンライン学習をアプリに後付け)。

## B. Hyperedge が効く実用ユースケース

「n 項ファクト + ロール + 出典が第一級」「hyperedge 自体にベクトル/FTS/SIG が付く」
「メンバー集合不変により同一性・重心が well-defined」の 3 点が効く領域。

1. **episodic memory の格上げ** — Event(actor, action, target, location, session, time) は本質的に n 項。
   hyperedge ベクトル索引でイベント粒度の想起、OtherMembers で関係者展開。SIG で時間減衰。
2. **セキュリティ・インシデント分析** — Alert(process, host, user, file, connection, rule)。
   CommonHyperedges(hostX, userY) が調査ワークフローに直撃。類似インシデント検索 = hyperedge KNN。
3. **時制付き KG / 矛盾検出** — asOf ロールで同一 subject/object 対の Fact 並存を表現。
   MergeHyperedge で冪等取込 + CommonHyperedges で旧 Fact 発見 → 「文書 A と B で食い違う」を提示。
   grounded citation の次段階。
4. **会議・コラボレーション分析** — 会議/コードレビュー/共著。共同参加ネットワークは
   ハイパーグラフ理論の古典的応用。HIF エクスポートで HyperNetX/XGI 分析系に直結。
5. **動画・メディアのシーン検索** — Scene(characters, location, objects, timeRange) + シーン埋め込み。
   「ノード埋め込みでは表現できないイベント粒度の検索」の最直感的デモ。
6. **化学反応・レシピ・BOM** — Reaction(reactants, products, catalyst) はハイパーグラフが唯一の
   自然表現。HasMember / HasArity / 材料埋め込みで代替候補。製造 BOM トレーサビリティ = 出典ロール構造。
7. **不正検知** — Purchase(buyer, item, seller, paymentMethod, device)。不正リング =
   メンバーを共有する hyperedge クラスタ。SIG スコアリング + hyperedge KNN + co-membership 展開。

優先順位観 (2026-07-04 時点): A-1/B-1 (エージェント記憶) と B-3 (temporal KG) が本命。
どちらも hyperedge ベクトル索引と MergeHyperedge を最初に引っ張る。B-4〜6 はデモ・docs 素材向き。

## C. 数学的構造を正面に出すユースケース

「Quiver = 箙 (表現論)」の名に照応する機能群。定理に裏打ちされた
「特定ケースで圧倒するクエリ」候補と、名前との照応が美しい機能の 2 系統。

### C-1. 最悪ケース最適 join (AGM bound / WCOJ)

hyperedge ネイティブ格納は worst-case optimal join の前提条件そのもの。
三角形など cyclic なパターンで binary join plan は Ω(N²)、WCOJ (Leapfrog Triejoin 系) は
O(N^{3/2}) — AGM bound (Atserias–Grohe–Marx) の fractional edge cover が上界を与え、
NPRR がそれを達成する。さらに hypertree width 有界なら Yannakakis で多項式時間 —
「このパターンは幅 2 なので速い」という **証明書付きクエリプラン** が出せる。
HyperedgePattern の複数結合 (HYP-4 の先) が自然な実装座標。

### C-2. 有向 hyperedge = Horn 節、B-到達可能性

head/tail 意味論は directed hypergraph (Gallo–Longo–Pallottino–Nguyen) そのもの。
B-connectivity は線形時間、= Horn-SAT (Dowling–Gallier)。
「AND 依存の到達可能性」(ビルド依存、型推論、権限導出、レシピ実行可能性、
反応ネットワークの合成可能性) は binary グラフの到達クエリでは表現不能。
重み付き最短 B-hyperpath でコスト最小導出も。実装は forward chaining 1 本で小さい。

### C-3. Minimum transversal (hitting set) + SMT/ILP 組み込み

「回答を接地する最小の出典集合」= 出典ロール hyperedge 集合に対する最小 hitting set。
NP-hard だが実サイズ (fact 数百) なら ILP/MaxSAT で即答。RAG の citation 最小化として実利が直結。
汎用形: Match + float[] プロパティ上の線形算術制約 (QF_LRA) + 目的関数 →
「制約充足部分グラフの最適化」を 1 クエリに。命名で退けた Transversal が
クエリ動詞 (MinimumTransversal) として復活する照応も良い。

### C-4. HodgeRank / 離散 Hodge 分解 (SIG の数学的出口)

SIG のダイアディックスコアは辺上の flow。組合せ Hodge 分解
(gradient ⊕ curl ⊕ harmonic) で大域ランキング + 非一貫性の定量化 (curl ノルム) が
証明書付きで得られる (Jiang–Lim–Yao–Ye)。実装は疎最小二乗 1 本。
hyperedge → 単体複体持ち上げで高次 Hodge Laplacian へ拡張可。

### C-5. パーシステントホモロジー (TDA) — 名前照応の本丸

persistence module は A_n 型 quiver の表現であり、barcode 分解は
Gabriel の定理 (有限表現型 ⇔ ADE Dynkin) の系。HNSW が既に近傍グラフを持つため
Vietoris–Rips 濾過が安価に構築できる。埋め込み集合の「クラスタ数とそのスケール安定性」を
barcode で返す組み込み関数。zigzag persistence も同じく A_n 表現。
「Quiver という名の DB が箙の表現論でベクトルデータを解析する」という一貫した物語になる。

### C-6. quiver 表現そのものの格納 / in-DB GNN 評価

node に R^d (既存 float[])、edge に線形写像 (d×d matrix プロパティ) を置けば
quiver 表現が第一級データ。message passing = 表現に沿った押し出しで、
in-database GNN 推論に表現論的定式化が付く。ADE 型スキーマなら直既約分解が有限分類。

### C-7. 圏論的データ移行 (Spivak, CQL 系)

スキーマ = quiver 上の自由圏 (+ 可換関係式)、インスタンス = 集合値関手、
スキーマ写像 F に沿った移行 = Σ_F ⊣ Δ_F ⊣ Π_F (左 Kan ⊣ 引き戻し ⊣ 右 Kan)。
マイグレーション/ビューの正しさが随伴で証明される。「スキーマは箙である」を文字通りに。

### C-8. 形式概念分析 (FCA)

node × hyperedge の incidence 構造は formal context。Galois 接続から概念束を構成し、
閉集合マイニング = 暗黙スキーマ発見・共起パターン抽出。incidence 表現と定義が一対一。

優先順位観 (2026-07-04 時点): 「圧倒するクエリ」系は C-1 (理論保証付き漸近優位) と
C-2 (表現力優位・実装小)、名前照応系は C-5。C-3 は SMT 統合の入口として RAG 実利と両取り。

## D. テストケース観点の補足 (2026-07-05)

> 各ユースケースをトラック化する際に必要になるテストの種を A/B/C の ID で対応付けて記録する。
> エンジン本体の性能ゲート (WAL 増幅・走査倍率・crash recovery) は
> plans/hyperedge-implementation-tasks.md が正本であり、ここには重複させない。

### A 系 (float[] 横展開)

- **A-1 エージェント記憶**: 時間減衰スコアの単調性 (類似度同点なら新しいイベントが上位)。
  追記と想起の並行実行で snapshot 下の結果集合が安定すること。ベクトル上書き (記憶更新) 後の
  recall — HNSW overwrite 再リンクは MVP 制限として既知なので、劣化許容値を先に数値固定する。
  セッション途中のプロセス kill → 再オープンで直前 commit まで復元。
- **A-2 ローカル推薦**: graph 側 (行動) と vector 側 (embedding) の更新が交錯したときの
  ハイブリッドスコア整合。embedding 未設定ノード・候補ゼロの degrade (例外ではなく空/低スコア)。
  k > 候補数の境界。
- **A-3 時系列異常検知**: 高 churn (窓の create + delete 連続) での tombstone 蓄積と
  vacuum 後の recall 維持。既知異常を埋めた合成系列で異常度順位が再現すること。
- **A-4 音響類似**: 高次元 (数百〜千次元) payload の slab cache 境界。
  近接重複だらけの密クラスタでの recall@k。
- **A-5 ゲーム AI 記憶**: セーブ = 単一ファイルの copy round-trip (静止状態のコピーで完全一致)。
  強制終了直後のロード。buffer pool 上限を絞った低メモリ環境での動作。
- **A-6 メディアライブラリ**: FTS + vector + graph の 3 系統ヒットの重複除去と順位安定性。
  プロパティ型混在 (EXIF 数値/文字列/日時) の round-trip。
- **A-7 kNN 分類器**: 同距離タイの決定性。ラベル pre-filter (FlatOnly 経路) と全体 KNN の
  結果一貫性。逐次追加による分類境界の更新。

### B 系 (hyperedge)

- **B-1 イベント記憶**: 同一 node の複数ロール (actor = target の自己言及イベント)。
  session ロールの高カーディナリティ (1 session に 10^3 イベント) での
  GetHyperedges / OtherMembers。hyperedge プロパティへの時間減衰 SIG。
- **B-2 インシデント分析**: CommonHyperedges の交差意味論 (全指定 node を含むものだけを返し、
  部分一致を返さない)。次数が極端に偏る入力 (host 10^5 件 vs user 10 件) で最小次数起点に
  なること。type フィルタ併用の全組み合わせ。
- **B-3 時制付き KG**: MergeHyperedge の同一性 = 「型 + ロール付きメンバー集合」の
  メンバー順序無依存を明示テスト。同一 Fact を並行 2 tx が Merge して直列化後に 1 件に
  なること。asOf 違いは別 Fact として並存。同一文書再処理の完全冪等性 (件数不変)。
- **B-4 会議分析**: HIF export → import round-trip (ロール・プロパティ・アリティ保存)。
  空 hypergraph・孤立 node・全 node 共有の縮退形。
- **B-5 シーン検索**: メンバー node のベクトル更新後も hyperedge ベクトル (重心採用時) が
  作成時値のまま、という staleness 契約の明示テスト (親計画の注意書きをテストに固定する)。
- **B-6 反応・BOM**: 同一ロール複数メンバー (reactants n 個) の全列挙。HasArity 境界
  (min = max、範囲外)。高 fan-in 材料 node の DeleteNode カスケード (10^3〜10^4 反応が
  同一 tx で消える) — エンジン側は HYP-6c の高次数カスケード計測と同根、アプリ観点では
  「意図しない大量削除」への警告手段の要否を判断する。
- **B-7 不正検知**: 共有 device / payment ハブ (10^4 hyperedge のメンバー) の chain 走査性能。
  co-membership 展開で同一ペアが複数 hyperedge 経由で重複排出されるか否かの仕様固定とテスト。

### C 系 (数学的構造) — oracle 比較を原則にする

C 系は「小さい入力なら正解が手計算または naive 参照実装で分かる」領域なので、
乱数入力 + 参照実装との一致 (oracle テスト) を基本形にし、性質テストを併用する。

- **C-1 WCOJ**: 三角形 / 4-cycle パターンで binary join plan と結果集合の完全一致。
  AGM タイトな敵対的入力 (Loomis–Whitney 型) で N を倍化した時間比から漸近勾配を実測。
  hypertree 分解の証明書は被覆条件・連結性条件そのものを検証する。
- **C-2 B-到達可能性**: 充足可能 / 不能の Horn インスタンスを naive 前向き連鎖と一致。
  自己ループ・循環依存・空 tail。重み付き最短 B-hyperpath の最適性を小インスタンス全探索と比較。
- **C-3 最小 transversal**: fact 数 ≤ 20 の brute force と最適値一致。複数最適解のタイ処理の
  決定性。実行不能ケースの報告形式。数百 fact での応答時間 sanity。
- **C-4 HodgeRank**: 純 gradient flow (完全一貫) で curl 成分 ≈ 0、純 curl flow
  (じゃんけん 3-cycle) で gradient 成分 ≈ 0 の合成データ検証。分解 3 成分の直交性と再構成残差。
- **C-5 パーシステントホモロジー**: 円周サンプルで顕著な H1 bar が 1 本、球面で H2、という
  教科書ケース。入力摂動 ≤ ε で barcode の bottleneck 距離 ≤ ε (安定性定理) の統計的検証。
  HNSW 近傍グラフ由来の濾過と exact Rips 濾過の barcode 差の許容値を先に固定。
- **C-6 quiver 表現**: d×d 行列プロパティの round-trip。押し出し (message passing) 1 step を
  dense 行列積の参照計算と一致。
- **C-7 圏論的移行**: Σ ⊣ Δ ⊣ Π の単位・余単位 round-trip を小スキーマで検証。
  手作りスキーマ写像の移行結果を期待インスタンスと突合。
- **C-8 FCA**: 教科書 context で概念束の全概念一致。閉包演算子の冪等性・単調性・外延性の
  性質テスト。

横断メモ: 本命の A-1 / B-1 / B-3 のうち、B-3 の「Merge 冪等性 + 並行 Merge」は
MergeHyperedge 実装 (バックログ) の最初のテストになる。B-6 のカスケードは
HYP-6c に計測タスクとして反映済み (2026-07-05)。
