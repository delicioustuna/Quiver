# 全文とベクトルのセグメント契約

> 提案 3 の spike 前に固定する全文検索とベクトル検索のセグメント化契約。

## 位置づけ

この文書は、全文インデックスとベクトルインデックスを追記専用セグメントへ移すときの公開契約を定める。

COW shadow paging の現行案は 2026-07-07 の spike で本流不採用になった。
したがって、この契約は COW 専用にしない。
2026-07-08 の判断で、本流カーネルは現行 ARIES 継続とする。
ただし、将来 root catalog 差分ジャーナルを持つ hybrid 方式を再検討する場合も、同じ検索結果と同じ可視性を満たすことを前提にする。

セグメント化の目的は、全文 postings/norms と HNSW の更新を in-place mutation から切り離すことである。
書き込みは新しい segment を作り、commit 時に catalog へ登録する。
削除と更新は tombstone と新 segment で表現し、物理回収は merge で行う。

## セグメントカタログ

**セグメントカタログ**は、各全文インデックスとベクトルインデックスについて、可視 segment の一覧と世代を保持する正本である。

各 catalog entry は少なくとも次の情報を持つ。

- segment identifier
- index name
- entity kind
- lower commit marker と upper commit marker
- document または vector の件数
- tombstone 件数
- byte size
- tokenizer、metric、dimension、HNSW パラメータなどの index 固有 metadata
- merge 由来 segment の入力 segment 一覧

segment 本体は immutable である。
commit 済み segment を後から書き換えない。
削除や更新は、新しい tombstone segment または delta segment を catalog に登録して表現する。

catalog の可視化は commit の原子性境界である。
commit 前に書き切られた segment 本体が存在しても、catalog entry が可視でなければ検索はそれを読まない。
catalog entry が可視なら、検索に必要な segment 本体は完全に読めなければならない。

## Snapshot 可視性

検索は、transaction 開始時に見える catalog snapshot を読む。
read-only transaction は、その開始後に追加された segment、tombstone、merge 結果を観測しない。

書き込み transaction は read-your-writes を保つ。
同一 transaction 内で全文またはベクトルを書いた後に検索する場合、実装は transaction-local overlay を見るか、既存 row path へフォールバックしてよい。
ただし、commit 後に新しい transaction から検索した結果と矛盾してはならない。

rollback された transaction の segment は catalog に可視化されない。
未参照 segment 本体が残った場合は、起動時または maintenance で回収対象にする。

## 全文セグメント

全文 segment は、term dictionary、postings、norms、segment-local corpus stats を持つ。
postings は `(term, entityRef)` の集合であり、値として `tf` を持つ。
norms は `entityRef` から `docLen` を引く。

全文更新は、旧文書の tombstone と新文書の追加として扱う。
同じ entity の同じ全文インデックスに複数の live 文書が見える場合、最も新しい catalog generation の文書だけを有効にする。
古い generation は query merge 時に除外される。

BM25 の `N`、`df`、総文書長は検索時に live segment 群から合算する。
tombstone 文書は merge まで `N` と `df` に残る。
これは既知の誤差であり、merge policy の tombstone 比率上限で抑える。
delete-aware global stats は持たない。

WAND の term 上限は segment-local stats から計算し、top-k merge では segment ごとの候補を統合する。
segment 間で同じ entity が複数候補に出た場合は、可視性と generation を検証したうえで最新候補だけを残す。

## ベクトルセグメント

ベクトル segment は、payload と HNSW グラフを segment 単位で immutable に持つ。
`FlatOnly` index では payload だけを持ち、HNSW graph を持たない。

ベクトル更新は、新しい vector entry の追加として扱う。
削除は tombstone として扱う。
同じ entity の同じ vector index に複数 entry が見える場合、最も新しい catalog generation の entry だけを有効にする。

KNN は可視 segment それぞれに問い合わせ、segment-local top-k を global top-k へマージする。
各候補は entity generation と tombstone を検証してから返す。
古い `EntityRef` の vector binding が、削除後に再利用された sequence の新しい entity を指してはならない。

HNSW graph は segment 内で immutable である。
既存 vector の上書きで HNSW graph を in-place に再リンクしない。
更新頻度が高い workload では segment 数と tombstone 比率が増えるため、merge が検索品質と検索レイテンシの正本の回復手段になる。

## Merge と物理回収

merge は複数 segment を読み、live entry だけを新 segment に書き直す。
merge 結果は catalog entry の追加で可視化し、入力 segment は同じ catalog 更新で retired にする。
検索 snapshot が入力 segment を参照している間、入力 segment 本体は削除しない。

初期 merge policy は次の値を使う。

- tombstone 比率が 30% を超えた segment は merge 候補にする。
- 可視 segment 数の上限は 4 とする。
- 4 個を超える場合は、小さい segment から階層的に merge する。

この値は採否条件ではなく、spike の初期条件である。
マージ込みの総書き込み増幅が現行以下にならない場合、merge policy と保持 segment 上限を変えて再測定する。

## Crash と recovery

ARIES 継続の場合、catalog 更新は通常の transaction commit と同じ durability 境界に乗せる。
segment 本体は catalog 登録より先に永続化する。
recovery 後に catalog が segment を指すなら、segment 本体は完全でなければならない。

ハイブリッド方式の場合、segment 本体の永続化、catalog 差分ジャーナル、catalog 折り畳みの順序を crash injection で検証する。
catalog 差分が commit 済みなら segment は可視であり、未 commit なら不可視である。

どちらの方式でも、次の状態で recovery が完了してはならない。

- catalog が存在しない segment を指す。
- tombstone だけが失われ、削除済み文書または vector が復活する。
- merge 出力と入力 segment の両方が同じ snapshot で二重に可視になる。
- 片方の検索系だけで catalog 世代が進み、hybrid search の RRF が異なる snapshot の全文結果と vector 結果を混ぜる。

## Query 契約

全文検索、ベクトル検索、hybrid search は既存の公開 API 形状を変えない。
score の公開範囲と DSL の戻り値も変えない。

filtered search は、候補集合の graph visibility と segment visibility の両方を満たす entity だけを返す。
graph-first と text/vector-first のどちらを選んでも、結果集合は同じでなければならない。

hybrid search は、同じ catalog snapshot から全文結果と vector 結果を取得する。
片方の segment merge だけを新しい snapshot で見て順位を混ぜることはしない。

## 必達テスト

segment spike では、少なくとも次をテストする。

- commit 前の segment は検索に出ない。
- commit 後の segment は新しい transaction から検索に出る。
- rollback された segment は検索に出ない。
- read-only transaction は開始後の segment 追加と tombstone を観測しない。
- 同一 transaction 内の書き込み後検索は read-your-writes を満たす。
- 文書更新後、旧文書と新文書が同時に結果へ出ない。
- vector 更新後、旧 vector と新 vector が同時に結果へ出ない。
- entity generation が変わった stale binding は全文と vector の両方で除外される。
- merge 後も検索結果、top-k 順位の同点処理、RRF 結果が merge 前の可視集合と一致する。
- crash injection 後、commit 済み segment だけが可視になる。

## 性能 spike の判定項目

性能 spike は `plans/clean-slate-redesign.md` の提案 3 の表に従う。

- セグメント 4 個保持時の全文検索 p50 は 8.55 ms 以下を必達とする。
- マージ込みの総書き込み増幅は現行以下を条件付き必達とする。
- BM25 top-k の recall は strict full scan と一致することを必達とする。
- セグメント数と top-k merge の寄与は参考値として記録する。

baseline は、現行 ARIES 上で取り直す。
hybrid 方式を将来採用する場合は、その方式上で baseline を別途取り直す。

2026-07-08 の ARIES baseline は次のとおり。

- 全文検索 p50: 9.286 ms。
- 全文検索 p95: 71.362 ms。
- 全文 ingest WAL amplification: 11.74x。
- vector recall@10: 0.950。
- vector search p50: 1.229 ms。

この baseline から導く扱いは次のとおり。

- 全文検索 p50 の必達上限は、固定済みの 8.55 ms を維持する。現行 ARIES baseline より約 7.9% 速い水準であり、segment 化の複雑さに対する最低条件とする。
- マージ込みの総書き込み増幅は、現行 ARIES baseline の 11.74x 以下を条件付き必達とする。
- vector recall@10 は 0.95 以上を維持する。baseline が 0.950 ちょうどのため、segment fan-out と top-k merge で recall を落とさないことを重視する。

## 非目標

この契約は、segment のページフォーマットを確定しない。
term dictionary の圧縮、postings block 形式、HNSW の segment 内レイアウト、merge scheduler の実装方式は spike で決める。

この契約は、全文検索またはベクトル検索だけを先に本流化する許可ではない。
全文、ベクトル、hybrid search の snapshot を同じ catalog 契約で扱えることを確認してから本流タスクへ分解する。
