# CSR relationship 契約

> 提案 2 の CSR node group spike 前に固定する relationship entity の契約。

## 位置づけ

この文書は、CSR node group 化で relationship entity の公開契約を壊さないための物理契約を定める。

COW shadow paging spike は 2026-07-07 の計測で crash safety と単発更新 p50 を満たしたが、RAG-like batch の write amplification が現行比 15.91x となり、必達上限 4.7x を超えた。
したがって、現 COW shadow paging 案は本流に入れない。
2026-07-08 の判断で、本流カーネルは現行 ARIES 継続とする。
CSR の baseline と性能 spike は、現行 ARIES 上で開始する。

この文書で固定するのは、カーネル方式に依存しない relationship の論理契約と、CSR レイアウトで守るべき直接参照の条件である。
永続化手段が COW、ハイブリッド、ARIES のどれになっても、公開 `RelationshipId` の意味は変えない。

## 公開 ID

**RelationshipId** は、公開 API では stable な `EntityRef` として扱う。
構成は `kind`、`generation`、`sequence` であり、`sequence` を CSR segment 内 ordinal として公開しない。

`sequence` から物理位置を引くために、固定スロット直接アドレス方式の **relationship locator sidecar** を持つ。
locator は少なくとも `generation`、live/deleted 状態、source 側 node group、relationship ordinal、relationship type partition、segment identifier を保持する。
実装が削除や逆方向更新を効率化するために target 側 node group や backward ordinal を複製してよいが、公開契約は source 側 locator で満たす。

direct lookup は次の手順で定義する。

1. `RelationshipId` から `kind`、`generation`、`sequence` を読む。
2. `sequence` に対応する locator slot を読む。
3. locator の `generation` と live 状態を検証する。
4. source 側 node group と ordinal から relationship header と property column を読む。
5. deletion bitmap と delta store の可視性を同じ snapshot で確認する。

merge により CSR ordinal が変わる場合、merge の可視化と同じトランザクション境界で locator を更新する。
古い ordinal は内部参照であり、merge 後の公開 `RelationshipId` は変化しない。
削除後に同じ `sequence` を再利用する場合は `generation` を進め、古い `RelationshipId` が新しい relationship を指さないようにする。

ordinal を公開 ID にする設計は採用しない。
merge のたびに公開 ID が無効になり、relationship を第一級 entity として返す既存 API を守れないためである。

## Property 配置

relationship property は、source 側 node group に置く。
各 property column は CSR の relationship ordinal と整列し、隣接展開中に property 述語を同じシーケンシャル走査で評価できるようにする。

小さく頻出する値は column 内に inline で保持する。
inline 幅を超える値、低頻度の key、複数値 property は overflow heap へ逃がす。
この方針は現行 v1 の inline と spill の契約を引き継ぐ。

relationship property の source 側配置は、target 側から見た逆方向走査で property を失うことを意味しない。
逆方向走査で relationship が選択された後、locator 経由で source 側 property column を読む。
この追加参照のコストは、逆方向 property predicate の spike で参考値として記録する。

## 削除と挿入

CSR segment は immutable とする。
削除は segment ごとの **deletion bitmap** で表現し、lookup と traversal は snapshot に見える bitmap を必ず適用する。

挿入は delta store が受ける。
traversal は immutable segment と delta store を同じ snapshot で合成して返す。
物理回収は merge で行い、merge は live entry だけを新 segment に書き直し、locator を新しい ordinal に更新する。

削除済み relationship の property は、deletion bitmap により不可視になる。
overflow heap の物理領域は merge または vacuum で回収する。
削除直後に overflow payload が残っていても、direct lookup と traversal から観測されてはならない。

## 双方向 CSR

隣接は forward CSR と backward CSR を二重に持つ。
outgoing traversal は forward CSR を読み、incoming traversal は backward CSR を読む。
both traversal は両方を統合する。

この二重保持は新しい概念的コストではない。
現行 v1 も relationship record 内に source 側と target 側の双方向リンクを持ち、更新時に両方向を維持している。
CSR spike では、forward と backward を二重保持したときのストレージ増分を参考値として記録する。

削除は forward と backward の両方で同じ可視性になる必要がある。
実装は中央の live state と各 segment の deletion bitmap を併用してよいが、片方向だけに削除済み relationship が残る状態を snapshot から観測させてはならない。

## Type filter と property 述語

CSR は relationship type ごとに物理分割する。
type filter は partition selection として解く。
type 指定のない traversal は対象 node group の type partition を列挙する。

property predicate は、ordinal 整列の column scan で評価する。
必要に応じて zone map を持ち、範囲や nullability で segment を飛ばす。
低選択率の predicate には、現行と同じく B+Tree 二次インデックスを別に維持する。

type 数が多く、各 partition が疎になるケースは不採用条件にしない。
ただし、partition の希薄化が one-hop traversal と merge cost に与える影響は参考値として記録する。

## 必達テスト

CSR spike では、少なくとも次の direct lookup 契約をテストする。

- 作成した relationship の `RelationshipId` から、source、target、type、property を読める。
- merge 後も同じ `RelationshipId` から同じ relationship を読める。
- 削除後の `RelationshipId` は deleted または not found と判定され、property を返さない。
- 削除後に同じ `sequence` が再利用されても、古い `generation` の `RelationshipId` は新しい relationship を指さない。
- outgoing、incoming、both traversal が deletion bitmap と delta store を同じ snapshot で反映する。
- read-only transaction は開始時 snapshot の locator と bitmap を読み、後続 commit の merge や deletion に影響されない。
- property predicate は row path と column path で同じ relationship 集合を返す。
- overflow heap に逃がした property も direct lookup と traversal predicate で同じ可視性になる。

永続化カーネルの方式が確定している場合は、次の crash/recovery テストを追加する。

- locator 更新前に crash しても、旧 segment と旧 locator の組が観測される。
- locator 更新後に crash しても、新 segment と新 locator の組が観測される。
- forward と backward の片側だけが可視化された状態で recovery が完了しない。
- deletion bitmap と overflow heap 回収の順序が入れ替わっても、削除済み property が復活しない。

## 性能 spike の判定項目

性能 spike は `plans/clean-slate-redesign.md` の提案 2 の表に従う。
この契約文書から導出される必達条件は `RelationshipId direct lookup` である。

baseline は、現行 ARIES 上で取り直した現行 v1 相当値を使う。
hybrid 再設計を将来採用する場合は、その時点で hybrid 上の baseline を別途取り直す。

2026-07-08 の ARIES baseline は次のとおり。

- 述語つき 2-hop p50: 3.7963 ms。
- 述語つき 2-hop throughput: 263.4 traversals/sec。
- `RelationshipId` property direct lookup: 248.6 ns/lookup。
- relationship property update commit p50: 1163.80 us。

この baseline から導く性能合格ラインは次のとおり。

- 述語つき 2-hop throughput 必達下限: 526.8 traversals/sec 以上、同一 runner 形状の p50 では 1.8982 ms 以下相当。
- 点更新 p50 条件付き必達上限: 3491.40 us 以下。
- `RelationshipId direct lookup` は機能契約として必達。throughput 値は参考値として扱う。

2026-07-08 の read-path spike では、adjacency payload lane を immutable CSR base segment の近似として使い、述語つき 2-hop p50 1.1006 ms、p95 1.1191 ms、throughput 908.6 traversals/sec、現行比 3.45x を記録した。
これは 2.0x の read-path 必達条件を満たす。

ただし、この spike は locator sidecar、deletion bitmap、delta store、merge、crash/recovery を永続化していない。
したがって CSR relationship の read-path 前提は通過とし、次は現行 ARIES 上で persistent locator sidecar、delta store、deletion bitmap、merge、crash/recovery を含む永続化 spike に進む。
本流採用は、その永続化 spike が direct lookup 契約と点更新 p50 条件付き上限を満たすまで行わない。

2026-07-08 の persistence spike では、standalone prototype で locator sidecar、delta record、deletion bitmap、merge、commit frame recovery を検証した。
direct lookup、snapshot visibility、delete、sequence reuse、merge 後 lookup、traversal visibility は PASS。
crash/recovery は 4 ケースすべて PASS。
点更新 commit p50 は 1478.20 us で、条件付き必達上限 3491.40 us を満たした。
delta 1,000,000 件の merge は 792.61 ms で、checkpoint merge 上限 2.0 秒を満たした。

この結果により、CSR relationship の永続化前提は prototype レベルで通過とする。
ただし、prototype は製品 storage format でも `GraphDatabase` 統合でもないため、本流採用はまだ行わない。
次は現行 ARIES カーネルに product-gated な locator sidecar、base segment、delta store、deletion bitmap、recovery test を実装して、同じ計測を統合実装上で再実行する。

2026-07-08 の product-path integration validation では、公開 `GraphDatabase` API の update/delete/insert と既存 `CompactAdjacency()` を使い、V2 payload lane 付き base segment 再構築を検証した。
payload lane と row property の述語一致数は 2534 件で一致し、mismatch は 0。
述語つき 2-hop p50 は 1.2102 ms で、合格ライン 1.8982 ms 以下を満たした。
compact 開始時に adjacency descriptor を無効化して durable 化し、final descriptor write を compact epoch metadata 更新後へ遅延させることで、途中中断時に部分的な derived view を reopen せず、row path へ fallback できることも fault injection test で確認した。
検証点は descriptor invalidation 後、derived-view rebuild 後、final descriptor flush 後の 3 箇所である。
rebuild 後中断では row path fallback、final descriptor flush 後中断では V2 derived view reopen になり、どちらも delta relationship の重複が起きないことを確認した。
backend crash-contract matrix でも同じ 3 phase を reopen 経路で検証し、3 cases が通過した。

この検証により、既存 ARIES product path 上で base segment、source 側 property payload、deletion、delta insert、merge の最小 slice は統合可能と判断する。
2026-07-08 の残 Blocker 検証で、locator sidecar の永続化、snapshot-aware delta store、external process-kill / torn-write recovery matrix、統合 storage 上の 1,000,000 delta merge gate はすべて product path 上で PASS した。

2026-07-08 の snapshot-aware delta store 初期 slice では、既存の linked-list delta 走査を `RelationshipDeltaStore` へ分離し、`BinaryExpandCursor` と cardinality 見積もりが同じ delta cursor を使うようにした。
read-only transaction 開始後に新しい delta relationship が chain head へ追加されても、開始時 snapshot から見えていた既存 delta relationship を落とさない regression test を追加した。
対象テスト `AdjacencyEpochTests` は 16 件成功し、`dotnet build Quiver.slnx -v minimal` は 0 errors で通過した。
この slice は snapshot 合成の correctness を product path 上で固定するものだが、専用の永続 delta ページ形式や merge policy を確定するものではない。
専用の永続 delta ページ形式と merge policy は後続設計項目として残すが、CSR relationship の mainline 採用を止める Blocker ではない。
後続実装の作業順序と永続 layout の初期案は [csr-relationship-mainline-implementation-plan.md](csr-relationship-mainline-implementation-plan.md) を正本とする。

## 非目標

この文書は、CSR node group のページフォーマットを確定しない。
merge policy、zone map の具体形式、delta store のページ分割、secondary index の更新順序も別途 spike で決める。

この文書は、relationship を property のない隣接要素へ格下げしない。
Quiver は relationship を第一級 entity として公開し続ける。
