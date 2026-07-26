# 数理ユースケース横断 spike

> 実験日: 2026-07-24。
> 対象は [親計画 §9](math-usecases-track.md#9-リポジトリ適合性の予備評価と独立検証) の予備評価である。
> 本書の結果は本流への採用、優先度変更、公開 API の承認を意味しない。

## 実験境界

spike は `benchmarks/Quiver.Benchmarks/Experimental/MathUseCases/` に隔離し、製品 API、永続形式、依存関係を変更しない。

各実験は固定 seed の合成データを使い、正当性を独立した oracle と照合してから性能を測る。

時間は Release 構成でウォームアップ後に交互実行し、中央値を採る。

割当量は `GC.GetAllocatedBytesForCurrentThread` の差分で測る。

## 実験仮説

| 対象 | 実験 | 正解照合 | 判定 |
|---|---|---|---|
| BR-1 | tail incidence カウンタによる B-到達を規模別に実行する | 全 hyperedge を固定点まで再走査する素朴法 | 結果集合の完全一致と、incidence 数に対する傾きの安定性を必達とする |
| BR-2 | 加法コストとボトルネックコストの最短導出を解く | 小網で hyperedge 部分集合を全探索する | 導出木コストと共有を一度だけ数える導出部分グラフコストを区別できることを必達とする |
| MT-1 | greedy 上界つき分枝限定で minimum hitting set を解く | 候補 24 以下での部分集合全探索 | 厳密解一致を必達、fact 500 かつ候補 200 の絶対時間を努力目標とする |
| PV | 注釈なし走査と静的半環フックつき走査を交互実行する | tropical 半環の距離を独立した Dijkstra と照合する | 結果一致を必達、注釈なし overhead 5% 以下を是正ゲートとする |
| HG | 行列を構築しない Laplacian CG でペア比較順位を復元する | BTL 最尤推定と既知の潜在順位を比較する | 反復回数が頂点数より十分小さいことと順位品質を必達とする |
| FCA | NextClosure で形式概念を列挙する | 小規模 context の全属性部分集合を閉包する | 概念集合一致を必達とし、contranominal context で爆発条件を特定する |
| PB H0 | 全点対辺と疎 k-NN 辺から Kruskal barcode を作る | 全点対距離版 | 小規模で barcode 一致を必達とし、GMM の安定な併合 gap を測る |
| PB H1 | Vietoris–Rips 複体の境界行列を F2 上で簡約する | 円環と充填円板の既知 Betti 数 | 長寿命 H1 の検出と充填後の消滅を必達とする |
| WC | 型つき三角形を materializing binary join と sorted intersection で列挙する | 出力 tuple の完全一致 | cyclic 入力の crossover と中間結果削減を必達とする |
| P3 | 押し出し、引き戻し、高次 Laplacian、DMD、縮約順序の最小 kernel を実行する | 手計算可能な小例 | 技術的実行可能性だけを判定し、実需と公開契約は判定対象外とする |

## 事前に修正した比較条件

通常の binary BFS は reified hyperedge の AND 条件を保存しないため、BR-1 の oracle に使わない。

正しい reification は AND node に未充足入力数を持つ必要があり、カウンタ法と同じ状態機械になる。

HG の入力は現行 `ApplyDyadic` の出力を直接使わない。

現行演算は各候補と一つの参照ベクトルを比較して top-k を返すため、HodgeRank が必要とする疎な候補対 flow とは契約が異なる。

## 決定記録

### 2026-07-24 横断検証

実行環境は .NET 10.0.9、Windows 10.0.26200、x64、16 logical processors（AMD64 Family 25 Model 33）である。

小規模な正当性検査は一回の実行で反復した。

交互測定で IQR が 5% を超えた性能項目はプロセスを再起動して三回測り、次表にはプロセス中央値を記す。

10,000×384 の製品 vector adapter は一回の cold process で測った。

300 ms の努力目標に対して三桁以上遅かったため、再測定ではなく経路の棄却に用いた。

| 対象 | 正当性 | 実測 | 判定 |
|---|---|---|---|
| BR-1 | ランダム小網 40 件で固定点 oracle と完全一致した。現行 `GetNexuses` / `GetMembers` adapter は snapshot 分離を保った | 1 万 / 10 万 / 100 万 hyperedge は 0.069 / 0.713 / 6.765 ms。100 万で 300 万 incidence、2.25 ns/incidence、6,000,117 B/query | 線形性は必達通過。クエリ一時配列が約 6 B/hyperedge を確保するため、本実装前に pool 化を是正する |
| BR-2 | 小網の hyperedge 部分集合全探索と照合した | 共有導出例で加法 derivation tree は 15、ボトルネックは 10、unique hyperedge subgraph は 10 | SBT 型の加法コストは共有部分を重複計上する。公開契約を derivation tree と共有 subgraph のどちらにするか決めるまで本実装へ進めない |
| MT-1 | ランダム 60 件で全探索と最適解サイズが一致し、greedy 3 に対する厳密解 2 の反例も再現した | 支配候補が多い 500×200 は 3.339 ms、探索 1 node。対称な 500×200 は 20,000 node と約 109.1 ms を使っても最適性証明を完了しなかった | solver kernel は正しい。入力サイズだけの切替では足りず、node/time budget と `IsOptimal` を返す結果契約が必須 |
| PV | tropical 半環は 400 頂点、2,800 辺で Dijkstra と完全一致した | 現行 20,000-edge 走査で paired overhead の三プロセス中央値は 0.05%。baseline と hook はともに 480,152 B/scan | 静的ジェネリックの恒等半環は是正ゲート通過。既存走査の割当を増やさない |
| HG | exact gradient を残差 4.69e-17 で復元し、矛盾注入で curl RMS が 0 から 2.5 へ増えた | 10,000 頂点、100,000 比較辺を 20 CG 反復、6.823 ms で解いた。Kendall τ は Hodge 0.8479、BTL 0.8498 | 疎 CG と矛盾指標は成立。`τ >= BTL` は 0.0019 未達であり、BTL が生成モデルに合う入力では不適切な必達条件。順位差の許容帯と矛盾診断の追加価値へ基準を修正する |
| FCA | ランダム context 40 件で全属性部分集合 oracle と概念集合が完全一致した | 20 次 contranominal context は 1,048,576 concepts。`minExtent=4` でも 1,047,225 concepts（99.87%）が残る | minsupport 必須だけでは停止性を守れない。`maxResults`、time budget、cancellation、truncation 表示も公開契約に含める |
| PB H0 | 220 点の全点対 barcode と k-NN barcode は k=4 で一致し、900 点の 3-GMM を復元した | in-memory 3-GMM は 10.322 ms。製品 API で 10,000×384、k=16 の 139,802 辺から barcode を作る core は 10.826 ms | Union-Find core と sparse H0 は成立 |
| PB adapter | 製品 API の snapshot-wide 検索結果から 5 個の分離 component を復元した | 10,000 回の公開 `KnnSearch` に 265,053.6 ms、thread allocation 332,008,567,392 B | 公開 k-NN の N 回呼出しは棄却。HNSW private state は公開せず、候補 traversal を受けて scratch を共有する index 層の batch neighbor-graph primitive を先に spike する |
| PB H1 | 16 点円環で infinite H1 を 1 本検出し、中心点を加えた cone では 0 本になった | 明示境界行列の F2 簡約による小規模機能実証 | 技術は成立するが、Ripser 型の implicit coboundary、clearing、規模別性能は未検証。H0 と同時採用しない |
| WC | 10×10 の型つき三角形 tuple が materializing binary join と sorted intersection で完全一致した。GYO は path/star を acyclic、triangle を cyclic と判定した | domain 16 / 32 / 64 / 128 の speedup は約 1.95× / 1.75× / 10.9× / 23.6×。domain 128 の割当は binary 16,777,272 B、intersection 0 B | adversarial cyclic 形では crossover と中間結果削減を確認。走査時 sort を含む製品 Match との比較ではないため、索引と planner を含む統合 gate は未通過 |
| P3 | 押し出し、引き戻し合成、高次 Laplacian の半正定値性、2×2 DMD、縮約順序を手計算例と照合した | DMD 誤差 4.51e-16、star の induced width は 8 から 1 へ低下した | 最小 kernel の技術的実行可能性だけを確認。実需と公開契約はコード spike では検証できず、DEFER を維持する |

### 計画前提の反証

通常の binary reification に通常の BFS を適用すると、一つの tail だけで AND node へ到達して head を誤って導出した。

BR-1 の比較 oracle は binary BFS ではなく固定点法とする。

現行 `ApplyDyadic` は各候補と一つの参照ベクトルを比較する top-k 演算であり、候補対の疎 flow を返さない。

HG は `ApplyDyadic` の直接出力ではなく、比較対象二つの identity と歪対称 score を持つ pair-flow adapter を必要とする。

Entail の現行実装は最適化目的関数を持たない。

さらに `RedundancyChecker.MaxDepth` は 20 であり、上限到達時には `Unknown` 列挙値ではなく `NotRedundant` 相当の経路へ倒れるため、MT の厳密 oracle には使わない。

H0 barcode 自体はしきい値を固定せず階層を返すが、単一の cluster 数を返すには最大 gap などの選択規則が別途要る。

PB の公開契約は barcode と cluster-count heuristic を別結果として扱う。

### 2026-07-26 KnnSearchBatch 実装判定

`KnnSearchBatch`をprimary vectorの単一走査へ変更し、250件、384次元、32 query、k=10で個別検索32回と比較した。

個別検索は175.403 ms、35,217,736 B、batchは6.694 ms、875,152 Bだった。

結果は完全一致し、26.20倍、割り当て97.52%減を確認したため本流へ採用する。

### 続行条件

本結果は各トラックの採用を決めない。

次の本流候補へ進む前に、BR は最短導出の目的関数、MT は budget と最適性証明、HG は pair-flow 入力、FCA は有限列挙契約、PB は index 層 batch adapter、WC は製品 planner 統合を個別計画へ反映し、ユーザ判断を得る。
