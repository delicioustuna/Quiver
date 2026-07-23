# 数理ユースケース 実装タスク（基礎寄りトラック群）

> 親計画は [plans/math-usecases-track.md](math-usecases-track.md)。上位 2 トラック（BR/MT）は
> [plans/math-usecases-implementation-tasks.md](math-usecases-implementation-tasks.md) を正本とする。
> 一次資料は [plans/math-usecases-cornerstone.md](math-usecases-cornerstone.md) /
> [plans/math-usecases-research.md](math-usecases-research.md)。
>
> 本書は BR/MT の次に控える残りトラックを、**基礎寄り（＝エンジンの中核能力を底上げする汎用プリミティブ）を
> 応用ユースケース（化学・世界モデル・GNN 等のドメイン適用）より高い優先度**に置き直した上で、
> BR/MT と同じ粒度の実装タスクへ分割したものである（ユーザ方針、2026-07-07）。
>
> 親計画 §6 には全トラックを「分割済み・未着手」として反映済みである。
> これは着手承認や採否判断ではない。可変状態は tracked な計画書へだけ記録し、Skill には複製しない。

---

## 0. 概観：優先度の再編・着手順序・転記手順

### 0.0 現行アーキテクチャ基準

- コード上の entity は `Vertex` / `Edge` / `Nexus`、identity は `VertexId` / `EdgeId` / `NexusId` である。
- Nexus の星型 pattern は `NexusPattern`、走査は `GetNexuses` / `GetMembers` と対応する DSL / operator を使う。
- 現行エンジンは Single Writer + Snapshot Readers であり、各読み取りは開始時点の snapshot に束縛される。
- vector search は snapshot 可視な複数の immutable segment を横断する。HNSW の近傍リストは segment 内部の private state である。
- 現行のダイアディック処理は `ApplyDyadic` であり、旧 `EntityCandidateSet` を前提にしない。

### 0.1 基礎寄り優先の再編（本書が扱うトラックの序列）

「基礎度」＝そのトラックがエンジンの汎用能力（クエリ実行・注釈・既存構造の解析）を底上げする度合い。
応用シナリオ（特定ドメインへの当てはめ）は基礎度を下げる。LeWorldModel 系の応用は最下位に置く。

| ID | 対応 | 内容 | 基礎度 | 圧倒性 | コスト | 資産活用 | 依存 |
|---|---|---|---|---|---|---|---|
| **PV** | D-5 | Provenance 半環（走査注釈の代数、到達/最短路の一般化） | 最高（実行層の汎用注釈） | 中 | 低 | 高（走査 + MT） | 走査オペレータ層（完） |
| **WC** | C-1, C-1' | WCOJ + hypertree 分解（結合アルゴリズムと証明書付きプラン） | 最高（結合の中核） | 最高（漸近優位） | 高 | 中 | `NexusPattern`（完）+ ソート索引（新規） |
| **PB** | C-5 | パーシステントホモロジー barcode（ベクトル集合のトポロジー解析） | 高（ベクトル解析の汎用プリミティブ） | 高（物語 + 実利） | 中〜高 | 高（vector segment） | HNSW 近傍抽出 adapter（新規） |
| **FCA** | C-8 | 形式概念分析（incidence 構造からの概念束マイニング） | 高（構造マイニングの汎用プリミティブ） | 中 | 中 | 高（incidence） | incidence ストア（完） |
| **HG** | C-4 | HodgeRank / 離散 Hodge 分解（順位 + 矛盾スコア） | 中〜高（グラフ Laplacian 解法。入力は ApplyDyadic 応用寄り） | 中 | 低（疎 CG） | 高（ApplyDyadic、三角形は WC 共有） | ApplyDyadic（完）、curl は WC 望ましい |

> 最下位（応用・研究要員、詳細化を保留）は §6 に理由付きでスタブのみ置く。

### 0.2 着手順序と依存関係のまとめ

基礎度が最高でも実装コストと新規依存が重いトラックは後ろへ回す。**基礎度（優先度）と着手順序は別軸**である。

- **依存はいずれも「完了済み既存インフラ」に対してのみ**（相互依存は無く、独立並行は技術的に可能）。
  ただし親計画 §1 により**並列展開はしない**。1 本ずつ通す。
  - PV → 走査オペレータ層（完）。追加ストアなし。
  - PB → immutable vector segment と HNSW 検索（完）。snapshot 全体の近傍グラフを作る adapter は新規。
  - FCA → incidence ストア（完）。追加ストアなし。
  - HG → `ApplyDyadic`（完）。curl 残差の三角形列挙は素朴実装で足りるが、**WC 完了後はその三角形経路を再利用**できる。
  - WC → `NexusPattern`（完）＋ **新規のソート済み隣接 / trie 索引**（FormatVersion 影響を着手時に判定、親計画 §5）。最重量。
- **共有基盤の芽**（着手時に共通部品化を検討）:
  - 疎線形代数 / 共役勾配（CG）: HG が最初に必要とし、将来の高次力学（D-2）と共有。
  - 分解器（GYO / hypertree）: WC が作り、テンソルネットワーク縮約（D-4）と共有（研究 §2 の「Quiver.Decomposition」構想）。
  - hitting set 最小化: MT（既分割）の成果を PV の why-provenance 最小化が下流で再利用。
- **推奨着手順序（コスト・独立性・実利を織り込んだ実務順）**: **PV → PB → HG → FCA → WC**。
  - 理由: PV は最も安価で最も汎用（既存 Dijkstra を特殊化として飲み込む）ため最初。PB は独立・実利・名前照応で次。
    HG は安価だが curl の完全形が WC を待つため中盤。FCA は独立だが概念数爆発の見極めが要るため後半。
    WC は基礎度最高だが新規索引インフラと format 影響で最重量ゆえ最後（完了で HG の curl と D-4 を解錠）。
  - オーケストレータは基礎度優先（WC/PV を前へ）へ倒す裁量を持つ。倒す場合は WC のインフラ費用を先払いする判断を明記する。

### 0.3 状態記録と Skill routing（共通）

親計画 §7 に従い、Skill は tracked な正本への不変 router と実装前ゲートだけを持つ。

1. 採否、優先度、着手順、spike 結果は親計画または本書の決定記録へ追記する。
2. [plans/README.md](README.md) は現行計画への入口だけを持ち、個別状態を複製しない。
3. `.agents/skills/quiver-implement/tasks/math-usecases.md` と Claude 側 stub から本書の該当節を参照する。Skill に進捗行を追加しない。

各節末尾の転記ブロックは起案時の候補情報として保持するが、Skill 進捗行は使用しない。

---

# PV トラック: Provenance 半環（走査注釈の代数）

> 対応: コーナーストーン D-5、research §5。基礎度: 最高（クエリ実行層の汎用注釈）。
> 目的仮説: 走査オペレータに「注釈合成フック」を 1 個足し、半環をジェネリクス `ISemiring<T>` にすると、
> **到達可能性（ブール半環）・最短路（tropical 半環＝既存 Dijkstra の一般化）・why-provenance（出典集合の冪集合半環）・
> 信頼度（Viterbi 半環）が同一コードで出る**。「grounded citation の代数」として RAG 差別化の言語になり、
> MT（最小出典集合）の理論的上屋になる。

## PV-0 トラック配線

共通 router から本節を参照する。状態は本書にのみ記録し、Skill には追記しない。

## PV-S 注釈フック overhead spike

### 仮説

注釈合成フックを走査オペレータに常設しても、**注釈を使わない経路のオーバーヘッドはジェネリクスの
特殊化で消える**（≤5%）。既存の重み付き最短経路が tropical 半環の特殊化として再現できる。

### 比較・データセット・採否基準

- 既存の 1-hop / 多段走査 / 最短経路ベンチを、注釈フック導入前後で交互実行し中央値を比較する。
- **是正ゲート（corrective）**: 注釈なし経路の overhead ≤5%。超過なら注釈フックを型パラメタで分岐させ
  （`ISemiring<T>` の恒等半環でジェネリクス特殊化がフックを消すか、注釈無版を別経路にするか）再測定。
  この 1 点が満たせないと抽象が受け入れられないため、回収まで PV-1 を止める。
- **必達ゲート（hard）**: `ISemiring<T>` を tropical で具体化した最短経路が、既存 `WeightedShortestPath` と
  結果一致（一般化が既存の真の上位互換であることの証明）。

## PV-1 ISemiring と注釈フック（到達 / 最短路）

### 目的

走査に半環注釈の合流点を 1 個入れ、ブール（到達）と tropical（最短路）を同一コードで出せるようにする。

### 実装

- `ISemiring<T>`（`Zero` / `One` / `Add`（⊕＝代替経路の合流）/ `Mul`（⊗＝経路の連接））を追加する。
- 走査オペレータに注釈合成フックを足す。経路連接で ⊗、複数経路合流で ⊕ を適用する。
- ブール半環（到達）と tropical 半環（最短路）を実装し、**既存の重み付き最短経路が tropical の特殊化である**旨を
  設計文書と XML コメントへ明記する（管理系文言は入れない。数理的位置づけの説明として書く）。
- 注釈なし経路は恒等半環でフックが消える（PV-S の是正結果を実装へ固定）。

### テスト

- 到達（ブール）と最短路（tropical）が既存実装と一致することを検証する。
- 注釈なし経路の性能回帰が PV-S 基準内であることをベンチ sentinel で固定する。
- ⊕/⊗ の半環公理（結合律・分配律・零元/単位元）をプロパティテストで検証する。

## PV-2 why-provenance / 信頼度と再帰

### 実装

- 冪集合半環（why-provenance＝出典集合）と Viterbi 半環（信頼度）を追加する。
- 再帰（BFS / 到達）に必要な ω-連続半環は「不動点まで反復 + 吸収元で打ち切り」で実装する（research §5）。
- why-provenance の出力（出典集合）を **MT の `MinimumTransversal` へ渡す接続**を用意する
  （why-provenance → その最小化が最小出典集合。2 トラックを 1 本の柱の上下として結線）。

### テスト

- 冪集合・Viterbi が手計算できる小網で正しいことを検証する。
- 再帰半環が不動点で停止し、吸収元打ち切りが正しく効くことを検証する。

## PV-3 as-built + サンプル

- `docs/spec/`（05_query に半環注釈、08_known_limits に再帰半環の停止条件）へ as-built を追記。
- サンプル: RAG 回答の grounded citation を provenance 多項式として算出し、その最小化（MT 接続）まで見せる。
  親計画 §4 の基準（一気通貫・binary では書けない問い・決定的・後始末込み）を満たす。

## 全体計画への転記ブロック（PV）

- **§6 行**: `| **PV** | D-5 | Provenance 半環（走査注釈の代数、到達/最短路の一般化） | 中 | 低 | 高（走査 + MT） | P1 | 分割済み・未着手 |`
- **roadmap 行**: `| **PV** | Provenance 半環: ISemiring 注釈フック + 到達/最短路/why/信頼度、MT 接続 | 走査層 | P1 |`
- **Skill router**: 共通 `math-usecases.md` から本節を参照する。状態は本書にのみ記録する。

---

# PB トラック: パーシステントホモロジー barcode

> 対応: コーナーストーン C-5、research §7。基礎度: 高（ベクトル集合の汎用トポロジー解析）。
> 目的仮説: persistence module は A_n 型 quiver の表現で、barcode 分解は Gabriel の定理の系。
> Quiver は immutable vector segment ごとに HNSW を持つ。snapshot 全体の k-NN グラフを構成する adapter の要否を spike で検証する。
> 「埋め込み集合のクラスタ数と安定スケール」「ループ構造の有無」を**しきい値非依存**で barcode に返す。
> 名前照応の旗艦であり、クラスタリング・外れ値・埋め込み品質診断の実利を持つ。

## PB-0 トラック配線

共通 router から本節を参照する。状態は本書にのみ記録する。

## PB-S H0 barcode spike（段階 0）

### 仮説

H0 persistence は「距離順に辺を足す Kruskal + Union-Find」で済み、segment 内部の HNSW 近傍または公開 k-NN 検索から構成した疎 k-NN グラフを
入力にすれば実用時間でクラスタ併合の樹形図と安定性が出る。

### 比較・データセット・採否基準

- snapshot 可視な全 vector segment を横断して k-NN グラフを構成し、辺長ソート + Union-Find で H0 を計算する。
  private な HNSW 近傍を直接公開 API 化することは前提にせず、adapter の境界を spike で決める。
- **必達（hard）**: 小規模（N≤500）で全点対 naive Rips の barcode と H0 が一致（k-NN 近似前の厳密性確認）。
- **必達（hard）**: 合成 GMM の既知クラスタ構造の復元（クラスタ数一致率 ≥95%）。
- **努力目標（aspirational）**: N=10^4・d=384 の実埋め込みで H0 <300ms。未達でも近似誤差と時間を記録して PB-1 へ。
- k-NN 由来の疎 Rips は近似。**近似保証（または「N が小さければ全点対を SIMD 計算」の切替閾値）を記録**する。

## PB-1 H0 barcode API

### 実装

- 作業名 `PersistenceBarcode(vectorIndex, maxDim: 0, maxScale)` 相当を追加する。segment 横断の近傍抽出 → k-NN グラフ →
  辺長ソート → Union-Find でクラスタ併合の birth/death 区間と安定性を返す。
- 近似である旨（k-NN 濾過）と全点対切替閾値を返り値/ドキュメントで明示する（厳密性の嘘をつかない）。
- SIG の SIMD 距離カーネルを全点対経路で再利用する。

### テスト

- 小規模での naive Rips 一致（PB-S を昇格）、GMM 復元、外れ値（孤立点の長寿命成分）を検証する。

## PB-2 H1（ループ）

### 実装

- Ripser の 4 技法のうち (1) コホモロジー計算 + (2) clearing の簡易版を入れ、simplex を組合せ数系で
  エンコードして境界行列を実体化しない（research §7、Ripser §3）。N=10^3〜10^4 を狙う。
- `maxDim: 1` で H1 区間（ループの birth/death）を返す。zigzag は将来枠として触れるに留める。

### テスト

- 既知のループ構造（円環サンプリング）で H1 の 1 本の長寿命区間を検証する。
- H0 と H1 の同時計算で段階 0 の結果が変わらないことを検証する。

## PB-3 as-built + サンプル

- `docs/spec/`（06_vector に barcode API、08_known_limits に k-NN 近似の性質）へ as-built を追記。
- サンプル: 埋め込み集合のクラスタ数と安定スケールを**しきい値を決めずに** barcode で返す。名前照応
  （「箙の表現論でベクトルのトポロジーを解析する」）を短く述べる。

## 全体計画への転記ブロック（PB）

- **§6 行**: `| **PB** | C-5 | パーシステントホモロジー barcode（埋め込みのトポロジー解析） | 高 | 中〜高 | 高（HNSW） | P1 | 分割済み・未着手 |`
- **roadmap 行**: `| **PB** | TDA barcode: H0（Union-Find）→ H1（Ripser 簡易版）、HNSW 近傍濾過 | HNSW | P1 |`
- **Skill router**: 共通 `math-usecases.md` から本節を参照する。状態は本書にのみ記録する。

---

# HG トラック: HodgeRank / 離散 Hodge 分解

> 対応: コーナーストーン C-4、research §6。基礎度: 中〜高（グラフ Laplacian 疎解法。入力は SIG 応用寄り）。
> 目的仮説: SIG の対スコアを辺上の flow と見なすと、組合せ Hodge 分解
> `flow = gradient ⊕ curl ⊕ harmonic` により**大域ランキング（gradient のポテンシャル）とその信頼度
> （curl/harmonic 残差）が線形最小二乗 1 本で同時に出る**（Kemeny 最適化の NP 困難を回避）。

## HG-0 トラック配線

共通 router から本節を参照する。状態は本書にのみ記録する。

## HG-S 疎 CG spike

### 仮説

大域スコアは正規方程式 `L_0 s = -div Y`（グラフ Laplacian 系の疎線形系）を共役勾配（CG）で解けば得られ、
反復回数は N より十分小さい（疎性が効く）。

### 比較・データセット・採否基準

- 候補 10^4・辺 10^5 の合成データ（既知の埋め込み順位を持つ）で CG を回す。
- **必達（hard）**: CG 反復回数 ≪ N で収束（疎性が効く＝行列フリーの意味がある）。未達なら前処理/定式化が誤り。
- **必達（hard）**: 復元順位の Kendall τ が BTL 最尤推定と同等以上（順位指標としての妥当性）。
- **努力目標（aspirational）**: 収束 <500ms。未達でも記録して HG-1 へ。

## HG-1 グラフ Laplacian 解法 + 大域スコア

### 実装

- 行列フリー CG を実装する。グラフ Laplacian の行列-ベクトル積を **Edge 隣接の走査で評価**し、
  疎行列を明示構築しない（メモリ効率。コーナーストーン C-4）。前処理は不要か Jacobi で十分。
- 入力は `ApplyDyadic` が返す対スコア Y_ij（歪対称化）。作業名 `RankByHodge(candidates, dyadicScorer)` →（score[], …）。
  `GraphTraversal` / `TypedGraphTraversal` の `ApplyDyadic` 結果へ接続する。`System.Numerics.Tensors` の TensorPrimitives を使える。

### テスト

- 手計算できる小グラフで大域スコアの正しさ、CG の収束を検証する。
- `ApplyDyadic` 出力からの接続を統合テストで固定する。

## HG-2 curl / harmonic 矛盾スコア

### 実装

- curl 残差（三角形上の巡回和）と harmonic 成分を返す。三角形列挙は**素朴実装で足りるが、WC 完了後は
  WC の三角形クエリ経路を再利用する**（0.2 の共有基盤）。作業名の戻り値に inconsistency を足す。

### テスト

- 意図的に矛盾を注入した合成データで curl ノルムが有意に上がることを検証する（指標妥当性）。

## HG-3 as-built + サンプル

- `docs/spec/`（05_query に HodgeRank、08_known_limits に加法/歪対称化の前提）へ as-built を追記。
- サンプル: ペア比較集合から大域順位 + その信頼度（curl/harmonic）を同時に返す。「順位とその信頼できなさを
  1 本の最小二乗で出す」を主役にする。

## 全体計画への転記ブロック（HG）

- **§6 行**: `| **HG** | C-4 | HodgeRank / 離散 Hodge 分解（順位 + 信頼度） | 中 | 低（疎 CG） | 高（ApplyDyadic） | P1 | 分割済み・未着手 |`
- **roadmap 行**: `| **HG** | HodgeRank: 行列フリー CG で L_0 s=-div Y、curl/harmonic 矛盾スコア | ApplyDyadic（curl は WC 望） | P1 |`
- **Skill router**: 共通 `math-usecases.md` から本節を参照する。状態は本書にのみ記録する。

---

# FCA トラック: 形式概念分析（概念束マイニング）

> 対応: コーナーストーン C-8、research §11。基礎度: 高（incidence 構造の汎用マイニング）。
> 目的仮説: node × hyperedge の incidence 構造は formal context（対象 × 属性）そのもの。Galois 接続から
> 概念束を構成すると「共起する対象と属性の極大な組」を全列挙でき、暗黙スキーマ発見・共起パターンマイニング・
> タグ階層の自動構成・含意規則抽出に使える。閉集合 = closed itemset なのでデータマイニング的実利もある。

## FCA-0 トラック配線

共通 router から本節を参照する。状態は本書にのみ記録する。

## FCA-S 概念数分布 spike

### 仮説

incidence の edge 側チェーンをビット集合化すれば In-Close / CbO の閉包計算はビット AND の連鎖で書ける。
ただし概念数は指数爆発しうるため、iceberg lattice（support しきい値）の枝刈りが必須かを実データで見極める。

### 比較・データセット・採否基準

- 実データ（または実に近い合成 incidence）で概念数の分布を測る。
- **必達（hard）**: 小規模で素朴 CbO と概念集合が完全一致（列挙の正しさ）。
- **必達（hard）**: 概念数が実用外に爆発しないこと。**爆発するなら minsupport を必須引数にする**方針を確定してから API 化。
  この見極めなしに全列挙 API を出さない。

## FCA-1 In-Close / CbO 概念列挙

### 実装

- incidence の edge 側チェーン（メンバー集合）をビット集合化し、In-Close（CbO の辞書式順序 + canonicity テスト +
  ビット行列）で概念（extent=ノード集合, intent=hyperedge 集合）をストリーム列挙する。
- 作業名 `MineConcepts(hyperType, minExtent, minIntent)`。FCA-S の結論に従い support しきい値を必須引数にする。
- FTS のトークン-文書関係も formal context（語 × 文書）なので、語彙階層抽出への転用余地を設計文書に記す（実装は別途）。

### テスト

- 素朴 CbO 一致（FCA-S を昇格）、しきい値の枝刈り境界、空/単一概念の縮退を検証する。

## FCA-2 as-built + サンプル

- `docs/spec/`（05_query に概念束マイニング、08_known_limits に概念数爆発と support 必須）へ as-built を追記。
- サンプル: incidence からの暗黙スキーマ発見（共起する対象と属性の極大集合）を見せる。

## 全体計画への転記ブロック（FCA）

- **§6 行**: `| **FCA** | C-8 | 形式概念分析（incidence からの概念束マイニング） | 中 | 中 | 高（incidence） | P2 | 分割済み・未着手 |`
- **roadmap 行**: `| **FCA** | 形式概念分析: In-Close/CbO で概念束、minsupport 必須、暗黙スキーマ発見 | incidence | P2 |`
- **Skill router**: 共通 `math-usecases.md` から本節を参照する。状態は本書にのみ記録する。

---

# WC トラック: WCOJ + hypertree 分解（結合の中核）

> 対応: コーナーストーン C-1 / C-1'、research §1–2。基礎度: 最高（結合アルゴリズムそのもの）。
> ただし新規のソート済み索引インフラと FormatVersion 影響を伴い**最重量**のため、着手順序は最後に置く。
> 目的仮説: cyclic な結合（三角形等）で binary join は中間結果が Ω(N²) に膨れるが、WCOJ は出力サイズ上界
> （AGM bound）比例時間で走る（三角形で O(N^{1.5})）。これは実装の巧拙でなく**計算量クラスの差**であり、
> n 項リレーションをネイティブに持つ Quiver（hyperedge）だから成立する。hypertree 分解を足せば
> 「このクエリは幅 k だから多項式時間」という**計算量の証明書をプランに添付**できる。

## WC-0 トラック配線

共通 router から本節を参照する。**着手時に FormatVersion 影響（ソート済み隣接 / trie 索引の永続化要否）を最初に判定**し、
親計画 §5 に従って開発中 bump の扱いを決める。

## WC-S1 LFTJ crossover spike（binary 3-clique で先行）

### 仮説

Leapfrog Triejoin（LFTJ）の単変数 leapfrog（ソート順 iterator + seek）だけで三角形列挙を中間結果ゼロで走らせ、
ある N 以上で binary plan を上回る。**spike は Nexus 経路とは独立に binary Edge 3 本の三角形で開始できる**。

### 比較・データセット・採否基準

- 三角形列挙を N（辺数）を 10^4〜10^6 で振り、一様 + Zipf で LFTJ と既存 binary plan を比較する。
  Nexus incidence チェーンは未ソートなので、spike は「走査時ソート（小次数で十分）」で始める（research §1）。
- **必達（hard）**: cyclic（三角形）で N を振ると LFTJ が binary を上回る crossover が存在する。
  存在しなければ **「dense/cyclic 専用の隠し経路に留める」縮小スコープへ倒すか撤回**（親計画 §2.3）。
- **必達（hard）**: acyclic（path-2/path-3）で既存比 ≥0.8×（劣化 20% 以内。Free Join 不使用の許容線）。
- **是正/努力**: 中間結果メモリが binary ピーク比 ≤1/10（中間結果ゼロ性の実証。是正で回収可）。

## WC-1 Generic Join / LFTJ 物理オペレータ + 経路選択

### 実装

- ソート済み隣接（結合キー順の走査イテレータ）を用意する。B+Tree があるので `(RoleId, VertexId, NexusId)`
  キーの二次索引 か 走査時ソートのどちらで始めるかを WC-S1 の結果で決める。
- Generic Join（NPRR の簡略形、Ngo–Ré–Rudra *Skew Strikes Back* の再帰構造）の 1 オペレータを追加する。
  変数（ノード変数・hyperedge 変数）へ全順序を与え、各リレーションをその順序の trie と見なす。
- binary Edge を「アリティ 2 のリレーション」として同じ枠に載せる（lifting ビュー）。
- **オプティマイザに「結合ハイパーグラフが cyclic か」の判定を足し、cyclic→WCOJ / acyclic→既存 binary の
  ハイブリッド経路選択にする**（Free Join の但し書き: acyclic では素の WCOJ が binary に負ける、を回避）。

### テスト

- 三角形・4-clique・cyclic/acyclic 混在で結果が既存 Match と一致することを検証する。
- 経路選択（cyclic 判定）が正しく分岐することを検証する。

## WC-2 hypertree 分解 + Yannakakis（証明書付きプラン）

### 実装

- GYO 簡約（1 hyperedge にしか出ない頂点を消す / 他に包含される hyperedge を消す、を固定点まで）で
  acyclicity を判定し、acyclic なら join tree を得て Yannakakis（semi-join 2 パス）で実行する。
- 幅 ≥2 は hyperedge 数 ≤ 20 程度の全探索 + メモ化で det-k-decomp 簡略版を書く（クエリサイズは小さい）。
- `EXPLAIN` 相当に `width=k, method=Yannakakis/WCOJ` を載せる（計算量の証明書）。
- 分解器は internal な共有モジュール（研究 §2 の「Quiver.Decomposition」構想）に置き、将来のテンソルネットワーク
  縮約（D-4）と共有できる形にする。

### テスト

- acyclic star/path で GYO 判定と Yannakakis の結果一致・dangling tuple 除去を検証する。
- GYO + join tree 構築のオーバーヘッドがクエリ実行時間に対し無視できる（≤5%、是正ゲート）ことを測る。

## WC-3 as-built + サンプル

- `docs/spec/`（05_query に WCOJ / hypertree、08_known_limits に cyclic 判定と幅の意味）へ as-built を追記。
- サンプル: 三角形/cyclic クエリで既存 binary に対する劇的な高速化と、`EXPLAIN` の幅証明書を見せる。

## 全体計画への転記ブロック（WC）

- **§6 行**: `| **WC** | C-1, C-1' | WCOJ + hypertree 分解（漸近優位 + 証明書） | 最高 | 高 | 中（NexusPattern） | P2 | 分割済み・未着手 |`
- **roadmap 行**: `| **WC** | WCOJ（LFTJ/Generic Join）+ GYO/Yannakakis 二段オプティマイザ、証明書付きプラン | NexusPattern + ソート索引 | P2 |`
- **Skill router**: 共通 `math-usecases.md` から本節を参照する。状態は本書にのみ記録する。

---

# 6. 最下位：応用・研究要員（詳細化を保留）

> ユーザ方針により、基礎寄りプリミティブを優先し**応用ユースケース（特にドメイン適用・ML 推論・世界モデル）は
> 最下位**に置く。以下はスタブのみ残し、基礎トラックが一巡してから、実需が具体化したものだけ分割する。
> いずれも上の基礎トラックの再利用で書ける（新規基盤は最小）。

- **in-DB GNN / 箙表現格納（C-6, research §9）**: edge に d×d 行列プロパティを置き message passing を
  表現論的に定式化。ML 推論寄りで応用度が高い。押し出し `PushForward` は SIG の内積ループ + 行列 codec で書けるが、
  実利は基礎トラックに劣る。cellular sheaf（C-8/D 系）と格納・乗算基盤を共有する。
- **圏論的データ移行 CQL（C-7, research §10）**: 研究寄り・可換関係式の管理が重い。現実的入口は Δ（引き戻し =
  射影ビュー）のみ。実需が出るまで保留。
- **高次力学 / 単体複体上のダイナミクス（D-2）**: HG の高次版（Hodge Laplacian）。HG の CG 基盤を共有。応用寄り。
- **世界モデル / 潜在ダイナミクスの軌跡ストア（D-3, LeWorldModel 接続）**: ユーザが名指しで例に挙げた応用。
  最下位。Koopman/DMD は HG/CG 基盤の再利用で書けるが、Quiver 本体の基礎度は低い。
- **テンソルネットワーク縮約順序（D-4）**: WC の分解器（Quiver.Decomposition）の物理応用。WC 完了後に安価に載る。ニッチ。

これらを分割する場合も、転記ブロック（§0.3）と kill criteria の三階層分類（親計画 §2.2）を同じ様式で付ける。

---

# 決定記録

> 実装エージェントは spike と本実装の決定をここへ追記する（HYP の決定記録に倣う。計測環境・多点数値・
> 階層分類・続行/是正/撤回の別を残す）。着手までは空。
> 親計画 §9 の見解は未検証の予備評価であり、このタスク定義の採否、優先度、順序を変更しない。
