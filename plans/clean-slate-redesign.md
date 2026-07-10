# 白紙再設計の構想

> レビュー用ドラフト (2026-07-07)。
> 前提: 十分な工数を確保でき、後方互換は不要 (スクラップ&ビルド可)、既存ユーザはいない。
> コンセプト (単一ファイル、ゼロ依存、Pure C#、in-process) は維持する。

## 文書の目的と判断の枠

この文書は、v1 として完成した現行エンジンを白紙から設計し直せるとしたら、何をどこまで変えるかを示す。
実装計画ではなく、方向の採否をレビューするための構想である。
各提案には、現状の何が問題か、何に置き換えるか、得るものと代償、採否を決める実測項目を付す。

定量的な優劣は本文では仮説にとどめ、採否は spike の実測で決める。
列指向 property 再設計では descope 判断を推論で二度誤り、実測で覆した。
その経験から、性能とコストの判断は「数値の採否条件を先に固定した最小 spike」を通すことを本構想全体の運用とする。

## 残すもの

現行の次の判断は、白紙からやり直しても同じ結論になると考えるため変更しない。

- 単一ファイル + in-process + 排他ファイルロックと、single-writer / multi-reader の並行性契約
- `EntityRef` (kind + generation + sequence のパッキング)、CRC32C ページチェックサム、トークンインターン
- API の形 (GraphDatabase / トランザクション / トラバーサル DSL)、SourceGen 型付きモデル、Rag / Hosting / OpenTelemetry のアドオン分離
- 8 KB ページ、二次インデックスの構造としての B+Tree、単一ファイル内のテナント多重化

再設計で最大の資産になるのはコードではなく、v1 のテストスイートと性能ゲートである。
したがって進め方は「v1 の API とテスト契約を受け入れ基準に据え、その下のカーネルを差し替える」形を取る。
これにより、作り直し版の検証体系を新規に設計する必要がなくなる。

## 提案 1: COW シャドウページングへのカーネル置換

### 現状と問題

現行カーネルは ARIES 型 WAL (物理 page image) と record 単位 MVCC (xmin/xmax + version チェーン + sidecar + vacuum) を両方持つ。
この両立が複雑さの根になっている。
全文検索だけが論理 WAL + 補償レコードという例外系を持ち、B+Tree には Full / Suppressed / RedoOnly の 3 ジャーナリングモードがある (docs/spec/03_mvcc.md、docs/spec/04_records_index.md)。
また page image WAL は書き込み増幅が構造的に大きく、全文取込で増幅 ~11× (バッチ経路で 4.7×)、過去には WAL 単発肥大 (146 GB、コアレス + checkpoint で解決済み) というクラスの事故を生んだ。

### 置き換え案

LMDB 型の **copy-on-write シャドウページング**に置き換える。
書き込みは触れたページを新しい物理ページへ複製して行い、commit は dirty ページ群の書き出しと meta ページの原子的フリップで完了する。
リーダは commit 時点のルートを参照し続けるため、開始後の変更を観測しない。

この方式が成立する根拠は、Quiver の並行性契約が最初から single-writer であることにある。
並行ライタを持たないため、ライタ間のページ競合という COW の弱点が発生しない。

### 得るもの

- **MVCC の単純化**：スナップショット分離が page 単位で成立し、xmin/xmax、CommittedTxRegistry、version チェーン、および version 回収としての vacuum が不要になる。`EntityRef` の generation は残す。generation が守るのは可視性ではなく、再利用 slot への stale ID 参照の検出だからである (設計者回答 1-1)。
- **インデックスの MVCC 化**：二次インデックスと全文にもスナップショット分離が自動で付く。現在これらはグローバルインデックスロックによる直列化で代用している。
- **WAL リプレイ型リカバリの消滅**：ARIES の redo / undo パスと全文の論理 undo + 補償レコード機構が丸ごと消える。起動時には、二重 meta ページの検証と有効 root の選択という定数時間の処理だけが残る (設計者回答 1-2)。
- **WAL サイドカーの消滅**：稼働中も含めて常に単一ファイルになり、コンセプトがむしろ強くなる。page image WAL 由来の増幅事故クラスも構造から消える。

### 代償

- commit がシーケンシャル追記 1 fsync から「dirty ページ書き出し + meta 書き込み」の 2 fsync になり、小粒トランザクション連打のレイテンシが悪化しうる。並行に commit するスレッドが複数あるときは group commit (合流) で fsync を償却できるが、単発 commit のレイテンシ自体は縮まない (設計者回答 1-5)。
- 長寿命リーダが古いページを pin し、空きページ回収を止める。ただし現行でも最古のトランザクションが WAL 切り詰めを pin しており (docs/spec/08_known_limits.md)、契約としては悪化しない。
- ランダム小書き込みでは、触れた B+Tree のルートまでのパスが commit ごとに COW され、ページ単位の増幅が出る。page image WAL も 8 KB 全画像を記録するため増幅の桁は同等と推定するが、これは推定にすぎない。

### 採否を決める実測項目

- RAG 取込 (batch=10) の書き込み増幅が現行 ~4.7× 以下であること
- 単発プロパティ更新 commit の p50 レイテンシが、group commit なしの素の値で現行の 2 倍以内であること
- 長寿命リーダ保持下のファイル成長が、現行の WAL 肥大と同等以下であること

## 提案 2: CSR node group + 列指向レイアウト

### 現状と問題

レコード層は Neo4j 系の設計で、固定長ノードレコード + 双方向連結リスト隣接 + property チェーンからなる。
連結リスト隣接は 1 ホップごとにランダムページアクセスを発生させ、property チェーンも同様にポインタ追跡になるため、走査系のキャッシュ効率が構造的に低い。
最後発の hyperedge ストアが versioned heap + inline property 領域に到達したのは、チェーン方式のこの弱点を避けた結果であり、同じ教訓をレコード層全体に適用する余地がある。

### 置き換え案

ノードを **node group** (数万件単位) に区切り、隣接を **CSR** (compressed sparse row)、プロパティを列ブロックで持つ。
CSR は in-place 更新に弱いため、immutable セグメント + 小さな delta ストアの二層にし、checkpoint 時にマージする。
走査が「ポインタ追跡によるランダムアクセス」から「連続領域のシーケンシャルスキャン」に変わるため、k-hop 展開とラベル / プロパティ述語つきスキャンの帯域が上がる。
これは Kuzu が組み込みグラフ DB で実証した方向である。

### 代償

- 点更新は delta 経由になり、読み取りはセグメントと delta のマージビューになる。
- マージコストが checkpoint に乗る。主用途の RAG はバッチ取込中心のため償却が効くと予想するが、要実測。

### 採否を決める実測項目

- 述語つき 2-hop 走査のスループット下限 (現行 v1 比 2.0 倍以上)
- 点更新レイテンシの回帰上限
- マージを含む checkpoint 時間の上限

## 提案 3: 全文とベクトルのセグメント化

### 現状と問題

既知の限界のうち、BM25 コーパス統計の陳腐化、HNSW 上書き時のトポロジ劣化、全文の論理 WAL + 補償レコード機構 (docs/spec/08_known_limits.md) は、いずれも「mutable な単一インデックスへ追記し続ける」設計の症状である。

### 置き換え案

Lucene 型の**追記専用セグメント**方式に置き換える。
セグメントは書き切って fsync し、カタログへの登録で原子的に可視化する。
削除は tombstone で表現し、バックグラウンドマージで回収する。
HNSW もセグメント単位で immutable に構築し、マージ時に再構築する。

### 得るもの

- セグメントの可視化がカタログ登録の原子性だけで完結するため、全文用の Suppressed / RedoOnly ジャーナリングと論理 undo が不要になる (提案 1 と併用すればこの層の WAL 例外系は完全に消える)。
- BM25 統計は、検索時にライブセグメント群の統計を合算して用いるため、スナップショット渡しに由来する陳腐化が消える。tombstone された文書は merge まで df / N に数え続けるが、その誤差上限は merge policy の tombstone 比率上限で規定できる (設計者回答 3)。
- HNSW の上書き劣化と、その補修系 (tombstone 比率トリガーの自動 rebuild) が設計から消える。

### 代償

- 検索が全セグメント問い合わせ + top-k マージになり、セグメント数に比例してレイテンシが伸びる。緩和は階層マージによるセグメント数の上限維持。
- マージ I/O が新たな書き込み増幅源になる。取込時の増幅をマージ時の増幅に移す取引であり、総量は要実測。

### 採否を決める実測項目

- セグメント 4 個保持時の検索 p50 が現行実測 (7.77 ms) +10% 以内 (8.55 ms 以下) であること
- マージ込みの総書き込み増幅が現行以下であること
- HNSW recall@10 が既存ゲート 0.95 を維持すること

## 提案 4: push 型ベクトル化実行

### 現状と問題

クエリ実行は Volcano (row-at-a-time) である。
列スキャン集約が SnapshotState を直接参照してオペレータパイプラインを迂回している事実 (docs/spec/03_mvcc.md) は、現行でも row-at-a-time が性能の足かせだったことの兆候といえる。

### 置き換え案

バッチ (数千行) 単位の push 型パイプラインに置き換え、多ホップパターンには中間結果の直積展開を避ける factorized 表現を導入する。
GraphKernel (BFS / 最短路) はそのまま残す。

この提案は提案 2 に従属する。
行レイアウトのままベクトル化しても、列アクセスのコストで利得が相殺されるためである。

### 採否を決める実測項目

- 述語つき 2-hop クエリの throughput が、提案 2 完了時点の engine 比 1.5 倍以上であること
- ダイアディック演算ゲート (0.148 µs/候補) の維持

## 提案 5: 実行時契約の近代化

独立に価値があるが、提案 1 と併せて実施すると安全に完結する項目群である。

- **トランザクション文脈の明示化**：`[ThreadStatic]` を廃し、状態をトランザクションハンドル自身に持たせる。現行では `await` 跨ぎで WAL ロギングが暗黙にスキップされうる (docs/spec/08_known_limits.md) が、明示ハンドル化後は `await` 跨ぎ自体を安全にし、同一ハンドルの複数 thread からの並行使用を検出可能な例外に変える。
- **single-writer のエンジン強制**：現在アプリケーション責務である書き込み直列化を、エンジン内蔵の write gate で強制する。2 本目の書き込みトランザクションはブロックまたは即時例外にする。
- **MMF の固定チャンク多重マップ**：ファイル拡張時の unmap / remap を廃し、64 MB 固定チャンクを積み増す方式にする。
- **バッファプールの廃止**：既定 256 frame (約 2 MB) のプールは OS ページキャッシュとの二重化になっている。MMF 上の `Span<byte>` 直読みと epoch ベースの回収に置き換える。COW 下ではリーダの見るページが不変になるため、この直読みが安全に成立する (提案 1 への依存)。

## 依存関係と着手順序

提案 1 (カーネルを COW にするか ARIES を継続するか) が最初の分岐である。
提案 2 から 5 の設計はこの決定に従属するため、他に先行して spike で決着させる。
提案 2 と 3 は、カーネル決定後は互いに独立に進められる。
提案 4 は提案 2 の完了が前提、提案 5 は提案 1 に相乗りする。

価値の内訳は二系統に分かれる。
提案 2 と 4 は主用途 (ローカル RAG) の走査性能に効く。
提案 1 と 3 は複雑さの恒久削減であり、WAL 増幅、リカバリ、補修系という事故クラスを構造から取り除く。

## リスクと進め方

全面書き直しの最大のリスクは、動いている v1 を失って長期間成果が出ない状態に陥ることである。
緩和は次の二点に集約する。

1. v1 のテストスイートと性能ゲートを受け入れ基準に据え、同一の API 契約の下でカーネルを差し替える。
2. 提案 1 と 2 は、数値の採否条件を先に固定した spike から始め、採否条件をすべて満たしたことを本流着手の条件にする。spike の粒度は列指向 property 再設計の spike (3 条件すべての PASS を確認してから本流取り込み) を踏襲する。

## 実装担当からの合意要求

この節は、設計者へ渡すための実装担当レビューである。
原案の方向性には賛成するが、本流実装へ入る前に、数値条件の意味と失敗時の扱いを合意しておく必要がある。

最大の論点は、本文で採否条件として扱う数値をすべて同じ重みで扱うかどうかである。
実装担当の立場では、採否を止める数値は、原則として**必達条件**でなければならない。
不合格でも設計判断で押し切れる数値は、評価指標または努力目標と呼び分ける。

設計者回答により、分類は次の四つで確定する。

- **必達**：不合格なら、その案は本流へ入れない。設計を変えて再 spike するか、現行方式を継続する。
- **条件付き必達**：不合格なら、そのままでは本流へ入れない。ただし、設計者が契約変更、運用制約、別の補償策を明示し、その条件込みで再計測して合格すれば採用を再検討できる。
- **努力目標**：不合格でも採用を止めない。実装優先度、チューニング対象、既知の限界への記載に使う。
- **参考値**：採否には使わない。後続設計や利用者説明のために記録する。

この分類を先に決めないまま spike を走らせると、結果が出た後に「これは必達ではなく参考だった」と解釈が変わる。
その状態では、spike は判断材料ではなく、既に選んだ結論を補強する儀式になってしまう。

## 設計者回答で確定した前提

提案 1 の COW シャドウページングは、白紙再設計の最初の分岐である。
ただし、COW は record-level MVCC と ARIES recovery を大きく単純化するが、すべての現在の問題を自動的に消すわけではない。
設計者回答により、次の前提を採用する。

- `EntityRef` の generation は残し、sequence 再利用も続ける。
  COW が解くのは snapshot visibility であり、古い ID が再利用 slot を指す ABA 問題は generation で検出する。
- 「リカバリの消滅」は、ARIES の redo / undo recovery の消滅に限定する。
  COW でも、起動時に二重 meta page から有効 root を選ぶ処理、committed 高水位を超える末尾領域の扱い、offline diagnostics としての free list 検証は残る。
- free page は reader epoch で再利用を制御する。
  commit C で解放された page は、活動中の全 reader の snapshot commit id の最小値が C を超えるまで再利用しない。
- root catalog は単一に集約する。
  Node、Relationship、Property、B+Tree、全文セグメントカタログ、ベクトルセグメントカタログ、hyperedge、token store、free list root、committed 高水位を一つの root catalog で切り替える。
- group commit は単発 commit p50 の採用判定に使わない。
  当該条件が不合格の場合、既定経路を合格扱いにできるのは root catalog 差分を追記するハイブリッド方式へ設計変更して再 spike した場合だけである。
  opt-in の緩和 durability モードは別契約として追加できるが、既定 full durable 経路の不合格は救済しない。

提案 2 の CSR node group では、`RelationshipId` の意味を最初に決める必要がある。
CSR は走査に強いが、Quiver は relationship を第一級 entity として公開している。
設計者回答により、`RelationshipId` は stable な `EntityRef` とし、sequence から (node group, ordinal) を引く locator sidecar を持つ。
relationship property は source 側 node group の ordinal 整列列に置き、削除は deletion bitmap、挿入は delta store、逆方向走査は forward / backward CSR の二重保持で扱う。
この契約は、性能 spike の前に `plans/csr-relationship-contract.md` へ書き下す。

提案 3 の全文とベクトルのセグメント化では、BM25 統計の「厳密」の意味を確認する。
設計者回答により、「常に厳密」という表現は撤回する。
採用する方式は検索時合算であり、ライブセグメント群の N、df、総文書長を検索時に合算して BM25 に与える。
tombstone された文書は merge まで df / N に数え続け、その誤差上限は merge policy の tombstone 比率上限で規定する。

提案 5 の実行時契約の近代化は、COW 採用を待たずに進められる項目を分けたい。
`[ThreadStatic]` 廃止と engine 内 write gate は現行 ARIES のままでも事故を減らす。
設計者回答により、`[ThreadStatic]` 廃止、write gate、並行使用検出は COW spike に先行する独立タスクとして扱う。

## spike 実施契約

spike は製品実装の下書きではなく、採否を決めるための実験である。
設計者回答により、次の手順を採用する。

1. 比較対象、データセット、採否条件、条件の重みを先に文書へコミットする。
2. 現行 v1 の baseline を、同じ runner、同じマシン、同じ runtime 設定で取り直す。
3. prototype は `benchmarks/Quiver.Benchmarks/Experimental/` または `sandbox/` に置き、製品コードへ直結させない。
4. Release 構成で warm-up 後に baseline と候補案を交互実行する。
5. p50、p95、書き込み bytes、ファイル増加量、managed allocation、計測環境を記録する。
6. sample のばらつきが p50 の 5% を超える場合は、プロセス再起動を含めて 3 run 以上を取り、run ごとの p50 の中央値で判定する。
7. 結果を `docs/benchmarks/YYYY-MM-DD_CleanSlate_<名前>.md` に記録し、本書の「決定記録」へ採否だけを追記する。
8. 不採用 prototype は削除する。
   採用する場合も、prototype をそのまま昇格せず、確定した契約だけを本実装タスクへ渡す。

spike の PASS は、本流実装の許可であって、本流実装の完了ではない。
本流へ進む場合は、改めて storage format、public API 影響、crash contract、diagnostics、known limits、benchmark gate を実装タスクへ分解する。

## 提案 1 の COW spike

最初に実施する spike は COW カーネルである。
この spike は B+Tree だけで閉じない。
meta page、root catalog、page allocator、free list、reader epoch、dirty page commit protocol を含める。

最低限の prototype は、次の範囲を持つ。

- 二重 meta page を持ち、commit id、root catalog page、free list root、checksum を記録する。
- dirty page は新しい物理 page へ書き、commit 時に root catalog と meta page を切り替える。
- reader は開始時の meta page と root catalog を保持し、writer commit 後も同じ snapshot を読み続ける。
- writer は single-writer gate の下でだけ動く。
- free page は reader epoch の下限を越えるまで再利用しない。
- B+Tree は root-to-leaf path を COW し、property index 相当の insert、update、lookup を実行できる。
- 最小の record store を持ち、単発 property 更新と RAG 取込に近い batch insert を再現する。

採否条件は次の表で確定する。

| 条件 | 分類 | 判定 |
|---|---|---|
| commit 済み状態だけが再オープン後に見える | 必達 | crash injection の全ケースで、未 commit root が可視にならない |
| meta page の torn write に耐える | 必達 | 片方の meta page が破損しても、checksum と commit id で直前の有効 root を選べる |
| reader epoch 中に旧 page を再利用しない | 必達 | 長寿命 reader が開始時 snapshot を読み続け、同時 writer の commit 後も読み値が変わらない |
| RAG 取込 batch=10 の総書き込み増幅 | 必達 | 現行実測の 4.7 倍以下 |
| 単発 property 更新 commit の p50 | 条件付き必達 | group commit なしで現行の 2 倍以内。不合格なら、既定経路は合格扱いにせず、ハイブリッド方式へ設計変更して必達条件込みで再 spike するか、COW を断念する。opt-in の緩和 durability モードは別契約として追加できるが、既定経路の不合格は救済しない |
| 長寿命 reader 保持下のファイル成長 | 必達 | 現行の WAL 肥大ケースと同等以下。測定時間、reader 保持時間、write rate を固定して比較する |
| managed allocation | 努力目標 | hot path で 0 B/op を目指す。不合格でも採用は止めないが、本流タスクで修正対象にする |
| free list 再利用効率 | 参考値 | 実測値を記録し、後続の vacuum 相当設計に渡す |

この表のうち、必達と条件付き必達が不合格なら、COW を本流へ入れない。
条件付き必達を満たせない場合に限って、設計者は「利用者向け durability / latency 契約をどう変えるか」を明示できる。
契約変更なしに性能不合格を受け入れることはしない。

## 提案 2 の CSR node group spike

CSR node group は COW 採否後に行う。
ただし、性能計測の前に、relationship entity の契約を文書で固定する。

事前に決める項目は次の通りである。

- `RelationshipId` が CSR segment 内の ordinal を指すのか、別の indirection table を指すのか。
- relationship property を列として node group に同居させるのか、relationship heap に分けるのか。
- 削除済み relationship を tombstone で残すのか、delta store で隠すのか。
- source 方向と target 方向の CSR を二重に持つのか、片方向を導出するのか。
- type filter と property predicate をどの列、どの bitmap、どの index で評価するのか。

採否条件は次の表で確定する。

| 条件 | 分類 | 判定 |
|---|---|---|
| 述語つき 2-hop 走査 throughput | 必達 | 現行 v1 比 2.0 倍以上。努力目標は 5 倍 |
| `RelationshipId` direct lookup | 必達 | 現行 API 契約を満たし、ID から存在確認、property 読み取り、削除済み判定ができる |
| 点更新レイテンシ | 条件付き必達 | 現行 v1 比 3 倍以内。不合格なら、RAG 向け batch profile 専用として scope を狭めるか判断する |
| checkpoint merge 時間 | 条件付き必達 | delta 100 万エントリのマージが 2.0 秒以内 (NVMe、Ryzen 7 5700X)。不合格なら merge policy を再設計する |
| delta store の読み取り増幅 | 努力目標 | 代表 workload で記録し、merge threshold の初期値に使う |

この spike は、走査性能だけで採用してはならない。
relationship entity の公開契約を守れない案は、RAG 走査が速くても Quiver の汎用グラフエンジンとしては不採用にする。

## 提案 3 の全文とベクトルのセグメント spike

全文とベクトルのセグメント化は、COW 採否後に提案 2 と並行できる。
ただし、統計と deletion の意味を先に固定する。

採否条件は次の表で確定する。

| 条件 | 分類 | 判定 |
|---|---|---|
| セグメント 4 個保持時の全文検索 p50 | 必達 | 現行実測 7.77 ms +10% 以内、すなわち 8.55 ms 以下 |
| マージ込みの総書き込み増幅 | 条件付き必達 | 現行以下。不合格なら、merge policy とセグメント上限を変えて再測定する |
| HNSW recall@10 | 必達 | 既存 gate 0.95 を維持する |
| セグメント数と top-k merge の寄与 | 参考値 | p50 の内訳として記録し、merge threshold に使う |

性能表とは別に、検索時合算統計は必達の機能条件として検証する。
ライブセグメント群の N、df、総文書長を検索時に合算し、tombstone 文書を merge まで統計に含むことは既知の誤差として扱う。
検索可視性、segment catalog、merge、crash/recovery、hybrid search の snapshot 契約は、性能 spike の前に `plans/fulltext-vector-segment-contract.md` に固定する。

この設計では、BM25 統計を「全体として常に厳密」とは書かない。
保証するのは、スナップショット渡しに由来する統計陳腐化をなくし、tombstone 由来の誤差を merge policy の上限内に収めることである。

## 提案 4 の push 型ベクトル化実行 spike

push 型実行は、提案 2 の columnar / CSR 契約が固まった後に行う。
行レイアウトのまま実行器だけを差し替える spike は、採否判断として扱わない。

採否条件は次の表で確定する。

| 条件 | 分類 | 判定 |
|---|---|---|
| 述語つき 2-hop throughput | 必達 | 提案 2 完了時点の engine 比 1.5 倍以上 |
| ダイアディック演算 gate | 必達 | 0.148 µs/候補を維持する |
| 中間結果の peak memory | 条件付き必達 | 多対多 3-hop の代表パターンで、非 factorized 実行比 50% 以下。不合格なら factorized 表現だけを落とし、push 型バッチ実行のみで再判定する |
| operator fusion の効果 | 参考値 | 採否ではなく後続最適化の優先度に使う |

提案 4 は提案 2 の従属タスクであり、単独で本流化しない。

## 提案 5 の実行時契約近代化

提案 5 は二つに分ける。
一つは COW に依存しない安全性改善であり、もう一つは COW と組み合わせて初めて成立する実装簡素化である。

COW に依存しない項目は、先行実施候補にする。

- `[ThreadStatic]` を廃し、transaction handle に文脈を持たせる。
- `await` 跨ぎを安全にし、同一 transaction handle の複数 thread からの並行使用を検出可能な例外にする。
- engine 内 write gate で single-writer を強制する。

COW に依存する項目は、提案 1 採用後の本流タスクにする。

- MMF の固定チャンク多重マップ。
- buffer pool 廃止。
- MMF 上の `Span<byte>` 直読み。
- epoch ベースの page 回収。

採否条件は次の表で確定する。

| 条件 | 分類 | 判定 |
|---|---|---|
| 並行 writer の拒否または待機 | 必達 | 2 本目の write transaction が未定義動作に入らない。既定は待機、timeout 時は `TransactionException`、即時拒否はオプション |
| transaction handle の並行使用検出 | 必達 | 暗黙に WAL logging が欠落する経路が存在せず、同一ハンドルの複数 thread からの並行使用が例外になる |
| 既存同期 API の互換 | 必達 | 公開 API のシグネチャは変えない。挙動変更は未定義動作だった並行 writer と handle 並行使用の定義化に限定する |
| MMF chunk size | 参考値 | 64 MB を初期値とし、file growth と address space の実測で調整する |

この提案は、COW spike の成否を待つ部分と待たない部分を切り分ける。
実装担当としては、`[ThreadStatic]` 廃止と write gate は COW の採否に関係なく先に進めたい。

## 条件付き必達の不合格時プロトコル

条件付き必達は、必達より弱い条件ではない。
不合格のまま本流へ入れない点は必達と同じである。
違いは、即不採用ではなく、設計者へ判断を戻す余地をあらかじめ持つ点にある。

条件付き必達が不合格になった場合は、次の手順を適用する。

1. 実装担当は、その場で条件の意味を変えない。
2. 判断は設計者へ戻す。
3. 設計者は、事前に本書へ宣言してある選択肢から、契約変更、運用制約、scope 縮小、設計変更のいずれかを選ぶ。
4. 補償策を選んだ場合は、その補償策込みの条件で再計測し、合格したときに限り採用を再検討する。
5. 契約変更なしに性能不合格を受け入れない。
6. 選んだ扱いは、本流へ進む前に利用者向け契約、既知の限界、既定設定のどこに現れるかまで書く。
7. 結果は決定記録へ残す。

事前宣言済みのフォールバックは次の通りである。

| 不合格になった条件 | とる対処 |
|---|---|
| 提案 1: 単発 commit p50 が現行の 2 倍超 | 既定経路は合格扱いにしない。実質の選択肢は、root catalog 差分ジャーナル + 定期 meta フリップのハイブリッドへ設計変更し、必達条件も含めて再 spike すること、または COW を断念して ARIES を継続することである。opt-in の緩和 durability モードは別契約として追加できるが、既定の full durable 経路の不合格は救済しない |
| 提案 2: 点更新 p50 が現行の 3 倍超 | CSR レイアウトを汎用の既定にせず、RAG 向け batch profile 専用へ scope を狭めるかを設計者が判断する。狭めない場合は不採用にする |
| 提案 2: merge 100 万エントリが 2 秒超 | merge policy を再設計し、node group 単位の増分マージなどを候補に再測定する |
| 提案 3: マージ込み書き込み増幅が現行超 | merge policy と保持セグメント上限を変えて再測定する。初期値は tombstone 比率 30% 超、保持セグメント 4 個である |
| 提案 4: peak memory が非 factorized 比 50% 超 | factorized 表現だけを落とし、push 型バッチ実行のみで再判定する |

提案 5 の「既存同期 API の互換」は、破壊範囲を明示したことで条件付き必達から必達へ格上げ済みである。
したがって、このプロトコルの対象は上の 5 行だけである。

## 本流実装へ進む条件

本流実装へ進む条件は、次のすべてを満たすことである。

- 提案 1 の COW spike で、必達と条件付き必達の条件が合格している。
- 条件付き必達が不合格になった場合、その扱い (scope 縮小、契約変更、再 spike、不採用) と、利用者向け契約、既知の限界、既定設定への反映先を書いている。
- 提案 2 以降の spike で使う baseline が、COW 採用後の baseline なのか、現行 v1 baseline なのか明記されている。
- v1 テストスイートを「流用するもの」「置換するもの」「削除するもの」に分類している。
- crash safety、snapshot isolation、single-writer、read-only transaction、ID stale reference、diagnostics の受け入れテストを列挙している。
- spike 結果をもとに、storage format、root catalog、allocator、index、record layout、query execution、FT/vector segment、runtime contract の実装タスクへ分解している。

この条件を満たすまでは、白紙再設計は構想と spike の段階に留める。
特に COW の採否が未確定のまま CSR、segment、push 実行の本実装へ入ることは避ける。

## 設計者回答

> 2026-07-07 設計者記入。前節までの実装担当レビューへの回答である。
> 本回答をもって、spike の実施と結果確認を実装担当へ委譲する (委譲範囲は末尾)。
> レビューが指摘した原案本文の不正確な記述 3 箇所 (generation、リカバリ、BM25) は本文側を修正済みであり、修正後の本文が正である。

### 回答 0: 数値条件の四分類

四分類 (必達、条件付き必達、努力目標、参考値) と、その定義をそのまま採用する。
数値条件は今後この文書では四分類の語で統一する。

各 spike の表の分類も、次の 3 行の変更を除いて承認する。

- 提案 1 の「単発 property 更新 commit の p50」: 条件付き必達のまま維持するが、補償策を差し替える (回答 1-5)。
- 提案 3 の「delete-aware global stats」: 条件自体を撤回する。global stats は持たない設計に確定し、本文の表現を改めた (回答 3)。
- 提案 5 の「既存同期 API の互換」: 破壊範囲を本回答で明示したため、必達へ格上げする (回答 5)。

旧版の表中で「spike 設計時に固定する」とされていた数値は、本回答 (回答 2、回答 3、回答 4) で固定する。
実装担当が実現可能性の観点で異議を持つ場合、spike 開始前に 1 度だけ再交渉する。
spike 開始後の数値変更はしない。

### 回答 1-1: generation の扱い

generation は残し、sequence の再利用も続ける。

COW が解くのは snapshot 可視性であり、アプリケーションが保持し続ける `EntityRef` やベクトル binding が「解放後に再利用された slot」を指す ABA 問題は、指摘のとおり別問題である。
sequence 再利用をやめる代替案は不採用とする。
locator 系 sidecar (incidence ストア型の fixed-slot 直接アドレス) は sequence を物理 slot へ直接写像するため、単調増加 sequence では削除の多いワークロードで穴が恒久に残り、sidecar が無際限に成長するからである。

したがって仕様は次のとおり。
generation 16 bit を維持し、slot 再利用時にインクリメントする。
slot を free に戻せる条件は、v1 の「アクティブトランザクションなし」から「その slot を解放した commit を reader epoch の下限が越えたとき」(回答 1-3 の規則) に置き換わる。

### 回答 1-2: 「リカバリの消滅」の意味の限定

指摘を受け入れ、消滅するのは WAL リプレイ型 (redo / undo) リカバリに限定する。本文修正済み。
起動時処理として次を仕様に残す。

- 二重 meta ページ双方の checksum 検証と commit id 比較を行い、有効かつ commit id が大きい方を root とする。
- データページの torn write は起動時の検出対象にしない。commit 手順 (全 dirty ページの書き出しと fsync が meta 更新に必ず先行する) により、torn になりうるページは未参照の新規ページに限られるからである。ただし、この順序保証そのものは crash injection の必達条件で検証する (回答 6)。
- committed 高水位を超えるファイル末尾の伸長は、未参照領域として free 扱いにする。切り詰めは必須にしない。
- free list の整合性は起動時に検証しない。root からの到達可能性走査による free list 検証は、CheckConsistency 相当のオフライン診断として提供する。

### 回答 1-3: free page 再利用と reader epoch の規則

中核規則を次で確定する: **commit C で解放された page は、活動中の全 reader の snapshot commit id の最小値が C を超えるまで再利用しない**。

実装方針:

- 解放 page は、解放した commit id を tag に持つ per-commit バケットの pending free list に積む。
- reader はトランザクション開始時に、自分の snapshot commit id をプロセス内 reader table (固定 slot 配列、interlocked で確保) に登録し、dispose で解除する。
- allocator は「tag < min(active reader ids)」のバケットだけを再利用可能 pool へ繰り入れる。reader がいなければ次の commit 以降すぐ再利用できる。
- dispose 漏れへの安全装置として、finalizer による slot 解放と、「最古 snapshot が何を pin しているか」を報告する診断を持つ。LMDB で最も厄介な stale reader 問題 (reader プロセスの突然死) は、in-process 専用の Quiver では構造的に発生しない。

これが COW 版における vacuum の中核である。
ファイルを物理的に縮める操作は、従来どおり明示のオフライン圧縮 (snapshot 書き直し) として別に残す。

### 回答 1-4: root catalog の単位

単一 root catalog に集約する。
全ストアの root (node、relationship、property、各 B+Tree、全文セグメントカタログ、ベクトルセグメントカタログ、hyperedge、token store)、free list root、committed 高水位を、1 つの root catalog (COW ページ。溢れたら COW ツリー) に置く。
meta ページが持つのは root catalog へのポインタ、commit id、checksum のみとする。
commit 境界での全ストア原子性は、この構造から自動的に従う。

「B+Tree 単体の COW spike ではこの原子性を検証できない」という指摘に同意し、前節の prototype 範囲 (meta、root catalog、allocator、free list、reader epoch、commit protocol を含める) を承認する。
crash injection では、異種 2 ストア以上と B+Tree と free list root が同一 commit で切り替わること (部分フリップが観測されないこと) を必達条件の判定に含めること。

### 回答 1-5: 単発 commit 不合格時の補償策の差し替え

group commit は採用判定に使わない。
group commit が縮めるのは並行 committer 群の合計 fsync 回数であり、単発 commit の p50 は縮まらない (合流待ち窓の分むしろ伸びうる)。
したがって旧版の表にあった group commit による補償案は、判定対象の計測を動かせず成立しない。
当該行の補償策を次の二択に差し替える。

条件付き必達「単発 property 更新 commit の p50 が現行の 2 倍以内 (group commit なし)」が不合格の場合、設計者が提示する選択肢は次に限る。

1. **opt-in の緩和 durability モード**：meta フリップの fsync を遅延させ、直近数 commit がクラッシュで失われうる (原子性と一貫性は保つ) モードを明示オプションとして追加する。既定は常に full durable であり、既定経路の不合格をこれで合格扱いにはしない。採用時は利用者契約と既知の限界に明記する。
2. **ハイブリッド再設計 + 再 spike**：root catalog の差分を追記する小さなコミットジャーナル (シーケンシャル 1 fsync) で commit を確定し、定期的に meta フリップへ折り畳む方式へ設計変更する。これは COW 案自体の変更であるため、必達条件を含めて再 spike する。

「契約変更なしに性能不合格を受け入れない」という原則に設計者も合意する。

### 回答 2: relationship 契約の確定

事前に決めるべきとされた五点を確定する。

1. **`RelationshipId` の意味**：indirection を維持する。`EntityRef` (kind + generation + sequence) を安定 ID とし、sequence から (node group, ordinal) を引く locator (incidence ストアと同じ fixed-slot 直接アドレス方式の sidecar) を持つ。merge で ordinal が変わったら locator を更新する。ordinal をそのまま公開 ID にする案は、merge のたびに公開 ID が無効になるため不採用。
2. **relationship property の置き場**：source 側 node group に、CSR ordinal と整列した列として置く。展開走査中の述語評価を隣接走査と同じシーケンシャル帯域で行うことを優先する。インライン幅を超える値と低頻度キーは overflow heap へ逃がす (v1 の inline + spill と同じ方針)。
3. **削除の表現**：セグメントは immutable のまま、セグメントごとの deletion bitmap (COW 管理の可変ページ) で隠す。挿入は delta store が受ける。物理回収は merge で行う。
4. **双方向**：forward と backward の CSR を二重に持つ。v1 も relationship record 内に双方向リンク 2 組を持っており、隣接表現の二重化は現行も払っているコストである。ストレージ増分は参考値として記録する。
5. **type filter と述語評価**：CSR を relationship type ごとに物理分割し、type filter はパーティション選択で解く。property 述語は ordinal 整列の列走査で評価し、必要に応じて zone map を足す。選択率の低い述語向けの B+Tree 二次インデックスは従来どおり別に維持する。type 数が多く各パーティションが希薄になるケースの影響は参考値として記録する。

実装担当は、この五点を性能計測の前に契約文書 plans/csr-relationship-contract.md へ 1 枚に書き下し、「`RelationshipId` direct lookup」必達条件のテストケースをそこから導出すること。

保留されていた数値も固定する。

- 述語つき 2-hop throughput の必達下限: 現行 v1 比 **2.0 倍**。二層構造 (segment + delta) と merge を抱える複雑さの対価として、これを下回るなら採用しない。努力目標は 5 倍 (Kuzu 系の実測報告の水準)。
- 点更新 p50 の条件付き必達上限: 現行 v1 比 3 倍以内。不合格時の再検討先 (RAG 向け batch profile への scope 縮小) は表のとおり。
- checkpoint merge 時間の条件付き必達上限: delta 100 万エントリのマージが 2.0 秒以内 (NVMe、Ryzen 7 5700X)。

### 回答 3: BM25 統計の意味の確定

「常に厳密」という表現を撤回し、delete-aware な global stats は持たない設計に確定する。本文修正済み。

採用する方式は検索時合算である。
検索時にライブセグメント群の統計 (N、df、総文書長) を合算して BM25 に与える。
各セグメントの term dictionary は検索でどのみち引くため、合算の追加コストは小さい。
この方式の保証は「スナップショット渡しに由来する陳腐化は消える。tombstone された文書は merge まで df / N に数え続ける」であり、誤差上限は merge policy が規定する。

初期値を固定する (いずれも参考値の実測で調整する)。

- merge 対象条件: セグメントの tombstone 比率が 30% 超。
- 保持セグメント数の上限: 4 (検索 p50 必達条件の測定条件と整合)。
- 検索 p50 の必達上限 (許容幅の確定): 現行実測 7.77 ms に対して +10% の **8.55 ms**。

表の「delete-aware global stats」行は前提が消えるため撤回し、代わりに「検索時合算統計が上記の保証を満たすこと」を機能テスト (性能表とは別) で確認する。

### 回答 4: 提案 4 の数値固定

- 述語つき 2-hop throughput: baseline は「提案 2 完了時点のエンジン」とし (この spike だけ現行 v1 比較ではない。理由は回答 7)、その **1.5 倍以上**を必達とする。実行器の差し替えで 1.5 倍に届かないなら、push 型 + factorized の複雑さに見合わないため不採用。
- 中間結果 peak memory: 多対多 3-hop の代表パターンで、非 factorized 実行比 **50% 以下**を条件付き必達とする。不合格なら factorized 表現だけを落とし、push 型バッチ実行のみで再判定する。
- 残り 2 行 (ダイアディック演算 gate 維持、operator fusion は参考値) は表のとおり承認。

### 回答 5: 提案 5 の切り分けと挙動の確定

切り分けに合意する。
`[ThreadStatic]` 廃止、write gate、並行使用検出の 3 点は、COW spike に先行する独立タスクとして現行 ARIES 上で実施してよい。
これらは COW spike 自体のノイズ (thread 事故由来の flaky) も減らすため、先行に積極的な理由がある。

挙動を確定する。

- write gate の既定は**待機**とし、既存のロック待ちタイムアウトと同じ系 (`TransactionException`) で失敗させる。即時拒否はオプションで提供する。
- 明示ハンドル化の帰結として、`await` 跨ぎはそれ自体が安全になる (トランザクション状態が thread ではなくハンドルに付くため)。検出対象として残るのは「同一トランザクションハンドルの複数 thread からの並行使用」であり、ハンドル内の owner フィールド (interlocked) で検出して即時例外にする。表の必達行の判定文は「暗黙に WAL logging が欠落する経路が存在しないこと、および同一ハンドルの並行使用が例外になること」と読み替える。
- 破壊範囲: 公開 API のシグネチャは変えない。挙動変更は (1) 2 本目の並行 write トランザクションが未定義動作から待機または拒否になる、(2) ハンドルの thread 間並行使用が未定義動作から例外になる、の 2 点で、いずれも未定義動作の定義化である。影響する v1 テストは並行性テストのみで、テスト三分類 (回答 7) では「置換」に入れる。

この明示により、「既存同期 API の互換」行は条件付き必達から必達へ格上げする。

### 回答 6: spike 実施契約への合意と追加

手順 1 から 8 をそのまま採用する。次の 4 点を追加する。

1. **環境固定**：計測は Ryzen 7 5700X 機、電源プラン高パフォーマンス、固定した .NET SDK バージョンで行い、これらを記録に含める。一時ファイルは BenchTempDir を使う (%TEMP% 残骸の既知問題対策)。
2. **計測バンドル**：手順 5 の記録項目は plans/benchmark-driven-perf-improvement.md の計測バンドル形式に合わせる。asm / HW カウンタは opt-in ローカル限定とし、採否判定には使わない (同計画の方針を踏襲)。
3. **crash injection の手法**：実プロセス kill ではなく、二層シムで決定的に行う。`IPagedFile` の下に「persisted 層と volatile 層を持ち、fsync で volatile を persisted へ反映する」fault injection 実装を挟む。クラッシュ = volatile 層を破棄して persisted 層から再オープン。torn write = 対象 write の先頭 k バイトだけを persisted へ反映 (k を 0 からページサイズまでスイープ)。kill point は prototype 内の全 write / fsync 呼び出し境界を列挙し、各点でクラッシュさせて必達 3 条件 (未 commit 不可視、torn meta 耐性、reader snapshot 不変) を検証する。
4. **baseline の保存**：手順 2 で取り直した現行 v1 baseline は、提案 2 以降の比較にも使うため、計測バンドルごと docs/benchmarks/ に残す。

### 回答 7: baseline の規約とテスト三分類

- **比較対象の規約**：性能系の必達と条件付き必達は、原則として「同一マシンで取り直した現行 v1 実測」との end-to-end 比較で判定する。COW 採用後に行う提案 2 と提案 3 の spike は新カーネル上で動くが、比較値は新カーネル込みの end-to-end 値であり、層別の寄与 (カーネル分とレイアウト分) は参考値として分解記録する。利用者に見えるのは end-to-end の性能だからである。唯一の例外は提案 4 で、これは同一エンジン上の実行器差し替え比較であるため「提案 2 完了時点のエンジン」を baseline とする (回答 4)。
- **v1 テスト三分類の初期方針**：流用 = API 契約、クエリと DSL、Rag、全文検索セマンティクス、hyperedge 契約、診断のブラックボックステスト。置換 = WAL、リカバリ、vacuum、MVCC 内部構造、並行性の whitebox テスト。削除 = page image WAL と補償レコードの実装詳細に固有のテスト。分類表は実装担当が作成し、設計者がレビューする。
- 受け入れテストの列挙 (crash safety、snapshot isolation、single-writer、read-only、stale ID、diagnostics) と本流タスクへの分解も実装担当の成果物とし、「本流実装へ進む条件」を満たした時点で設計者が承認する。

### 委譲範囲

本回答をもって、次を実装担当へ委譲する (結果の確認を含む)。

1. 提案 5 の先行 3 項目 (`[ThreadStatic]` 廃止、write gate、並行使用検出) の実装、テスト、v1 スイートでの回帰確認。
2. 提案 1 COW spike の実施一式: prototype 構築 (回答 1-2 から 1-4 の仕様に従う)、crash injection (回答 6-3 の手法)、計測、docs/benchmarks/ への記録、本書「決定記録」への採否追記。
3. 提案 2 の契約文書 plans/csr-relationship-contract.md の起草 (回答 2 の五点に従う)。

判断が設計者へ戻るのは次の三点に限る。

- 条件付き必達の不合格時の選択 (回答 1-5 の二択、回答 2 と 3 の再検討先の選択)。
- 固定済み数値への spike 開始前の異議 (1 度だけ)。
- 「本流実装へ進む条件」の充足判定と承認。

## COW 不採用後のカーネル選択

2026-07-08 時点の本流カーネルは、現行 ARIES 型 WAL / MVCC カーネルの継続とする。

COW shadow paging の現行案は、crash safety と単発 property 更新 p50 は満たしたが、RAG-like batch=10 の write amplification が必達上限を超えた。
このため、本流実装の前提として COW 系へ切り替えない。

root catalog 差分ジャーナルと定期 meta flip を持つ hybrid 再設計は、COW 系の再検討候補として残す。
ただし、それを選ぶには必達条件を含めた新しい spike が必要であり、提案 2 と提案 3 の直近 spike を待たせる前提にはしない。

したがって、CSR relationship と全文 / ベクトル segment の baseline は現行 ARIES 上で取り直す。
性能 spike も、カーネル差し替えを含まない ARIES 上の end-to-end 比較として開始する。

baseline 取得入口は `benchmarks/Quiver.Benchmarks` の standalone runner として追加する。

```powershell
dotnet run -c Release --project benchmarks\Quiver.Benchmarks -- --clean-slate-aries-baseline [degree] [traversalIters] [fullTextChunks] [fullTextQueries] [vectorCount] [vectorQueries]
```

既定値は、述語つき 2-hop baseline が degree 100 / 300 iterations、全文 baseline が 100,000 chunks / 500 queries、vector baseline が recall corpus 既定値である。
この runner は現行 ARIES 上の比較値を出すための入口であり、CSR や segment 本実装を先取りしない。

2026-07-08 の正式計測では、述語つき 2-hop p50 は 3.7963 ms、relationship property update commit p50 は 1163.80 us、全文検索 p50 は 9.286 ms、全文 ingest WAL 増幅は 11.74x、vector recall@10 は 0.950 だった。
これにより、提案 2 の throughput 必達下限は p50 1.8982 ms 以下相当、点更新 p50 の条件付き必達上限は 3491.40 us 以下となる。
提案 3 の全文検索 p50 は、固定済みの必達上限 8.55 ms を維持する。
結果は [2026-07-08_CleanSlate_AriesBaseline.md](../docs/benchmarks/2026-07-08_CleanSlate_AriesBaseline.md) に記録した。

提案 2 の read-path spike は、既存の永続 adjacency payload lane を immutable CSR base segment 近似として使って実施した。
述語つき 2-hop p50 は 1.1006 ms、現行 ARIES row path 比 3.45x で、必達 2.0x を満たした。
これにより、CSR relationship の read-path 前提は PASS とする。
ただし、この spike は persistent locator sidecar、delta store、deletion bitmap、merge、crash/recovery を未検証であり、本流採用判定ではなく次の永続化 spike へ進む判断に限定する。
結果は [2026-07-08_CleanSlate_CsrRelationshipSpike.md](../docs/benchmarks/2026-07-08_CleanSlate_CsrRelationshipSpike.md) に記録した。

CSR relationship の persistence spike は、standalone prototype で locator sidecar、delta record、deletion bitmap、merge、commit frame recovery を検証した。
direct lookup、snapshot visibility、delete、sequence reuse、merge 後 lookup、traversal visibility は PASS、crash/recovery は 4/4 PASS。
点更新 commit p50 は 1478.20 us で条件付き必達上限 3491.40 us を満たし、delta 1,000,000 件の merge は 792.61 ms で checkpoint merge 上限 2.0 秒を満たした。
これにより、永続化前提も prototype レベルでは PASS とする。
ただし、この prototype は製品 storage format でも `GraphDatabase` 統合でもないため、本流採用ではなく、現行 ARIES 上の product-gated 統合実装へ進む判断に限定する。
結果は [2026-07-08_CleanSlate_CsrPersistenceSpike.md](../docs/benchmarks/2026-07-08_CleanSlate_CsrPersistenceSpike.md) に記録した。

CSR relationship の product-path integration validation では、公開 `GraphDatabase` API で relationship property update、delete、insert を行い、既存の `CompactAdjacency()` で V2 payload lane 付き base segment を再構築した。
payload lane と row property の結果一致を計測前提にし、2534 件の述語一致数が両経路で一致、mismatch 0 を確認した。
述語つき 2-hop p50 は 1.2102 ms、p95 は 1.2470 ms で、ARIES baseline から導いた p50 上限 1.8982 ms を満たした。
統合上の具体的な不足として、V2 payload lane 付き `CompactAdjacency()` が未実装だったため、既存 V2 base payload の継承と relationship property からの payload refresh を実装した。
さらに compact 開始時に adjacency descriptor を無効化して durable 化し、final descriptor write を epoch metadata 更新後へ遅延させた。
fault injection test では descriptor invalidation 後と derived-view rebuild 後のどちらで中断しても、reopen 時に部分的な derived view を開かず row path へ fallback することを確認した。
final descriptor flush 後の中断では、reopen 時に V2 derived view を開き、compacted base epoch へ fold 済みの delta relationship が重複しないことも確認した。
backend crash-contract matrix でも同じ 3 phase を reopen 経路で検証し、descriptor invalidation / rebuild 後は row path fallback、final descriptor flush 後は V2 view reopen になることを確認した。
これにより、検証済み slice では現行 ARIES product path へ統合可能と判断する。
2026-07-08 の残 Blocker 検証では、stable locator sidecar は再オープン後の generation 付き direct lookup と sequence reuse 後の stale generation 拒否を targeted test で確認した。
snapshot-aware delta store は read-only transaction 開始後の delta insert / head 更新 / delete に対する regression test で確認した。
external process-kill / torn-write recovery matrix は descriptor invalidation、rebuild、final descriptor flush の 3 phase と WAL tail zero の組み合わせ 6 cases を外部 child process kill で確認し、6/6 PASS だった。
したがって、ここで列挙していた CSR mainline Blocker は解消済みと判断する。
結果は [2026-07-08_CleanSlate_CsrProductIntegration.md](../docs/benchmarks/2026-07-08_CleanSlate_CsrProductIntegration.md) に記録した。

CSR relationship の統合 1M merge gate では、公開 `GraphDatabase` API で 1,000,000 件の delta relationship を挿入し、`CompactAdjacency()` で V2 adjacency payload view へ fold した。
compact elapsed は 1,558.02 ms で、checkpoint merge 上限 2,000 ms を満たした。
reopen 後の relationship 件数は 1,000,001、payload matches は 500,001 で、統合 storage 上の merge gate は PASS と判断する。
結果は [2026-07-08_CleanSlate_CsrIntegratedMergeGate.md](../docs/benchmarks/2026-07-08_CleanSlate_CsrIntegratedMergeGate.md) に記録した。

全文 / ベクトル segment の fan-out spike は、製品 storage format を変更しない in-memory prototype として実施した。
4 segment 保持時の全文検索 p50 は 5.618 ms、p95 は 12.797 ms で、固定済みの必達上限 8.55 ms を満たした。
BM25 top-k は strict full scan と一致し、4 segment から 1 segment へ merge した後の結果も一致した。
merge 込みの推定書き込み増幅は 2.01x で、ARIES baseline の 11.74x 以下を満たした。
小さな決定的 catalog で、旧 snapshot 可視性、文書 / vector 更新時の旧世代除外、tombstone による削除隠蔽、merge 後同値、hybrid search の catalog generation 一致も確認した。
vector fan-out は exact flat prototype で recall@10 1.000、p50 3.668 ms、merge 後の結果一致を確認した。
これにより、提案 3 は prototype レベルでは product-path integration へ進める。
ただし、vector は immutable HNSW segment の recall を未検証であり、catalog 永続化、tombstone/update、crash/recovery、hybrid search snapshot は未完了のため、本流採用はまだ行わない。
結果は [2026-07-08_CleanSlate_FullTextVectorSegmentSpike.md](../docs/benchmarks/2026-07-08_CleanSlate_FullTextVectorSegmentSpike.md) に記録した。

## 決定記録

| 日付 | 対象 | 結果 | 決定 | 根拠 |
|---|---|---|---|---|
| 2026-07-07 | 実装担当レビュー | 原案の方向性は妥当。ただし採否条件の重みが未定義 | 本流実装前に、各数値を必達、条件付き必達、努力目標、参考値へ分類する | COW 採否後に全提案の baseline が変わるため、失敗時の扱いを先に固定する必要がある |
| 2026-07-07 | 設計者回答 | 四分類に合意。提案 1 の前提 5 点と提案 2 の契約 5 点を確定。保留数値を固定。group commit 補償を撤回して二択に差し替え。BM25「常に厳密」を撤回し検索時合算に確定 | 提案 5 先行 3 項目、COW spike、CSR 契約文書起草を実装担当へ委譲。設計者へ戻る判断は 3 点に限定 | 本書「設計者回答」節 |
| 2026-07-07 | 条件付き必達の不合格時プロトコル | 不合格のまま本流へ入れず、判断は設計者へ戻す。対象は提案 1 が 1 行、提案 2 が 2 行、提案 3 と 4 が各 1 行の計 5 行 | 各行の事前宣言済みフォールバックを本書に固定。提案 1 の opt-in 緩和 durability は既定 full durable 経路の不合格を救済しない | 本書「条件付き必達の不合格時プロトコル」節 |
| 2026-07-07 | COW spike | crash safety 3 条件は PASS。単発 property p50 は現行比 0.08x で条件内。一方、RAG-like batch=10 の write amplification が現行比 15.91x で必達 4.7x を超過 | 現 COW shadow paging 案は本流へ入れない。次判断は root catalog 差分ジャーナルを含むハイブリッド再設計 + 再 spike、または ARIES 継続へ戻す | [2026-07-07_CleanSlate_CowShadowPaging.md](../docs/benchmarks/2026-07-07_CleanSlate_CowShadowPaging.md) |
| 2026-07-08 | COW 不採用後のカーネル選択 | hybrid 再設計は新しい必達条件込みの再 spike が必要。提案 2/3 の契約はカーネル非依存に固定済み | 本流カーネルは現行 ARIES 継続とし、CSR relationship と全文 / ベクトル segment の baseline と spike を ARIES 上で進める | 本書「COW 不採用後のカーネル選択」節 |
| 2026-07-08 | ARIES baseline 計測 | relationship 述語つき 2-hop p50 3.7963 ms、点更新 p50 1163.80 us、全文検索 p50 9.286 ms、全文 WAL 増幅 11.74x、vector recall@10 0.950 | hybrid へ戻らず、提案 2/3 の spike を ARIES 上で進める。提案 2 の 2-hop 合格ラインは p50 1.8982 ms 以下相当、点更新 p50 上限は 3491.40 us 以下 | [2026-07-08_CleanSlate_AriesBaseline.md](../docs/benchmarks/2026-07-08_CleanSlate_AriesBaseline.md) |
| 2026-07-08 | CSR relationship read-path spike | adjacency payload lane を immutable CSR base segment 近似として計測。述語つき 2-hop p50 1.1006 ms、現行比 3.45x | 提案 2 の read-path 前提は PASS。次は persistent locator sidecar、delta store、deletion bitmap、merge、crash/recovery を含む永続化 spike へ進む。本流採用はまだしない | [2026-07-08_CleanSlate_CsrRelationshipSpike.md](../docs/benchmarks/2026-07-08_CleanSlate_CsrRelationshipSpike.md) |
| 2026-07-08 | CSR relationship persistence spike | standalone prototype で locator sidecar、delta、deletion bitmap、merge、commit frame recovery を計測。点更新 p50 1478.20 us、1M delta merge 792.61 ms、crash/recovery 4/4 PASS | 永続化前提は prototype レベルで PASS。次は現行 ARIES 上の product-gated 統合実装へ進む。本流採用はまだしない | [2026-07-08_CleanSlate_CsrPersistenceSpike.md](../docs/benchmarks/2026-07-08_CleanSlate_CsrPersistenceSpike.md) |
| 2026-07-08 | CSR relationship product-path integration validation | 公開 API の update/delete/insert と `CompactAdjacency()` 後に payload lane と row property を照合。2534 件一致、mismatch 0、p50 1.2102 ms。descriptor invalidation 後と derived-view rebuild 後の中断は row path fallback。final descriptor flush 後の中断は V2 view reopen。backend crash-contract matrix 3 cases passed | 検証済み slice は ARIES product path へ統合可能。V2 payload lane compact と中断時 fallback を実装済み。後続の残 Blocker 検証で locator sidecar、snapshot-aware delta、external process-kill / torn-write recovery matrix も PASS | [2026-07-08_CleanSlate_CsrProductIntegration.md](../docs/benchmarks/2026-07-08_CleanSlate_CsrProductIntegration.md) |
| 2026-07-08 | CSR relationship integrated 1M merge gate | 公開 API で 1,000,000 件の delta relationship を挿入し、`CompactAdjacency()` で V2 payload view へ fold。compact 1,558.02 ms、上限 2,000 ms 内。reopen 後 1,000,001 relationships、payload matches 500,001 | 統合 storage 上の merge gate は PASS。これと残 Blocker targeted test / recovery matrix により、CSR relationship の既知 mainline blocker は全て検証済み | [2026-07-08_CleanSlate_CsrIntegratedMergeGate.md](../docs/benchmarks/2026-07-08_CleanSlate_CsrIntegratedMergeGate.md) |
| 2026-07-08 | CSR relationship mainline 実装計画 | 既存 row store を正本に残し、dedicated persistent delta store を tenant 27/28 として段階投入する方針を固定。write path、read path、compact reset、recovery matrix、テスト順序を slice 化 | 後続エージェントはこの計画から着手する。専用 delta ページ形式と merge policy は Blocker ではなく、この計画に沿って本流へ畳み込む | [csr-relationship-mainline-implementation-plan.md](csr-relationship-mainline-implementation-plan.md) |
| 2026-07-08 | CSR relationship 契約 | `RelationshipId` は stable `EntityRef` のまま、locator sidecar、source 側 property column、deletion bitmap、delta store、双方向 CSR、type partition を固定 | 提案 2 の性能 spike 前契約として採用する。ただし COW 不採用後のカーネル判断が終わるまで本実装へは入らない | [csr-relationship-contract.md](csr-relationship-contract.md) |
| 2026-07-08 | 全文とベクトルのセグメント契約 | segment catalog、snapshot 可視性、検索時 BM25 統計合算、vector generation フィルタ、merge、crash/recovery、hybrid search snapshot を固定 | 提案 3 の性能 spike 前契約として採用する。baseline は ARIES 継続またはハイブリッド方式の選択後に取り直す | [fulltext-vector-segment-contract.md](fulltext-vector-segment-contract.md) |
| 2026-07-08 | 全文 / ベクトル segment fan-out spike | in-memory prototype で 4 segment fan-out を計測。全文 p50 5.618 ms、BM25 top-k strict scan 一致、merge 後一致、推定書き込み増幅 2.01x、vector exact fan-out recall@10 1.000。決定的 catalog で更新/tombstone/snapshot/hybrid generation 検証 PASS | 提案 3 は product-path integration へ進める。ただし immutable HNSW segment、catalog 永続化、crash/recovery は未完了のため本流採用はまだしない | [2026-07-08_CleanSlate_FullTextVectorSegmentSpike.md](../docs/benchmarks/2026-07-08_CleanSlate_FullTextVectorSegmentSpike.md) |
