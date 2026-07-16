# `plans/single-writer-redesign.md` レビュー結果 (Fable 5)

> レビュー対象: `plans/single-writer-redesign.md`（基点 commit `ee811d1`）
> レビュー実施: Fable 5 (claude-fable-5) による独立レビュー、2026-07-10。
> 本書は指摘の記録であり、正本ではない。修正を正本へ反映した後は「対応済み」を追記すること。

## Critical

### C-1. Buffer 管理方針 (steal/no-steal) が未定義で、§2.5 / §4.2 / §4.3 が整合しない

> **対応済み(2026-07-10)**: no-steal / no-force + redo-only recovery で確定。正本 §2.5, §4.2, §4.3, §6.2, §7.4, §16 decision log を参照。`RecoveryManager` disposition も loser 除外へ統一済み。

- **該当**: §2.5 (L103-104)、§4.2 commit 図 (L180-188)、§4.3 step 5 (L200)、§6.2 (L334)
- **内容**: §2.5 は「dirty page を data file へ書く前に before-image が WAL で durable」(steal 前提の WAL-before-data) を要求し、§4.3 は「loser の before-image を LSN 逆順に undo」する。しかし §4.2 の commit シーケンス図には `PageBeforeImage` の WAL append ステップが一切なく、書かれるのは「flush coalesced PageImage records」と `Commit` だけ。§6.2 の `WalWriteSet` は「before-image stack」を transaction-owned の**メモリ内**構造として持つ、と読める(L334、abort は「before-image を逆順適用」L190 = メモリ内 undo)。
- **失敗シナリオ**: no-steal (commit 前に dirty page を data file へ書かない) なら loser の undo pass 自体が不要で §4.3 step 5 は dead spec。steal を許すなら、eviction 時に `PageBeforeImage` を WAL へ書く経路が必要だが、それは §4.2 の「commit 時に coalesced flush」というモデルと衝突する(commit 前の任意時点で WAL 書き込みが発生する)。実装者はどちらかを勝手に選び、選択次第で recovery の正しさ根拠(§2.5 L104 の undo 冪等性)が変わる。既知バグ #1 (recovery clobber, docs/spec/08_known_limits.md) の再発領域そのもの。
- **修正案**: steal/no-steal と force/no-force を明示的に確定する節を §2.5 に追加。no-steal を選ぶなら §4.3 step 5 と `PageBeforeImage` record を削除し「loser は redo しないだけ」に簡約。steal を選ぶなら §4.2 の図に「eviction 前の before-image WAL append + fsync」を明記し、`WalWriteSet` のメモリ内 before-image stack (abort 用) と WAL 上の `PageBeforeImage` (recovery undo 用) の役割分担を書き分ける。

### C-2. Wave 1 で `IsolationLevel` を削除すると Wave 4 まで生き残る SSN コードがビルド不能 — Wave 順序の破綻

> **対応済み(2026-07-10、実装依存監査で改訂)**: Wave 4 で SSN/lock/Serializable 分岐を削除し、public `IsolationLevel` と旧開始 API は facade compatibility として Wave 6 まで残す。Wave 6 の public transaction/query/schema cutover と同じ commit で enum と旧 API を削除する。未実装の新旧 public model を中間 Wave で二重公開しない。

- **該当**: Wave 1 (L493「`IsolationLevel` … を削除する」、L500 Build 必須、L502 完了条件)、§7.3 (L382)、Wave 4 (L546)
- **内容**: `LockManager` / `SsnContext` / `SerializabilityException` / `IsolationLevel.Serializable` の削除は §7.3 と Wave 4 に割り当てられている。しかし実コードでは `src/Quiver/Transactions/SsnContext.cs`、`SerializabilityException.cs`、`Transaction.cs`、`TransactionManager.cs` が `IsolationLevel` を参照している。Wave 1 の完了条件は「solution 全体が新 contract だけを参照し、obsolete shim が 0 件」+ `dotnet build Quiver.slnx` 成功なので、Wave 1 時点で enum を消すと Wave 4 の削除対象が全部コンパイルエラーになる。
- **失敗シナリオ**: 実装者は Wave 1 でビルドを通すために SSN/Lock 系を前倒しで削除する(= Wave 4 の対象が実質 Wave 1 に繰り上がり、Wave 1 の変更量が爆発する)か、shim を挟む(完了条件違反)かの二択を迫られる。
- **修正案**: (a) SSN/LockManager/DeadlockDetector の物理削除を Wave 1 に移し、Wave 4 は「WriterLease/SnapshotRegistry の新実装」だけにする、または (b) Wave 1 では `IsolationLevel` を internal 化して public API からのみ削除し、enum 本体の削除を Wave 4 に残す — のどちらかを明記する。現状の記述はどちらとも読めない。

### C-3. 可視性規則 (§2.2.5) に writer の自己書き込み可視性の例外がない

> **対応済み(2026-07-10)**: 自己可視性の例外(`xmin/xmax = selfTxId`)を §2.2 に追加。正本 §2.2, §16 decision log を参照。

- **該当**: §2.2.5 (L75)、§4.1 Create (L150「`xmin = writerTxId`」)
- **内容**: 「version `v` は `xmin` が snapshot から committed と見え…るときだけ可視」と定義されているが、writer 自身の未 commit 書き込み (`xmin = writerTxId`) はこの規則では不可視になる。write transaction 内での read-your-own-writes(作成直後の entity を同一 tx 内で property 更新・traversal する典型パス)が規則上は動作しない。§11.2 は「reader が uncommitted `xmax` を無視する理由」に触れるだけで、自己可視性には触れない。
- **失敗シナリオ**: 実装者が規則を字義通り実装すると同一 tx 内 create→read が not found になる。逆に暗黙で例外を入れると、`xmax = writerTxId`(自 tx 内 delete)の扱い、savepoint rollback 後の可視性など、仕様なしの独自判断が増える。
- **修正案**: §2.2.5 に「writer transaction 自身に対しては `xmin = selfTxId` は committed 扱い、`xmax = selfTxId` は削除済み扱いとする。savepoint rollback で undo された version はこの限りでない」を明文で追加する。

### C-4. EntityRef の invalid と reserved kind の public contract が未定義

> **設計決定済み・実装未対応(2026-07-12)**: 正本 §2.3、§5.1、§16 に canonical Invalid、public factory、raw unpack の境界を追加した。Wave 1 の実装、factory test、PublicApi approval が完了するまで対応済みにはしない。

- **発見日**: 2026-07-12
- **影響**: Wave 1 commit 1
- **内容**: `EntityRef.From(NodeId.Invalid)` が invalid sentinel を返すのか例外にするのか、`Create`/`Pack`/`EntityId` の生成と変換が Property、予約、未知 kind をどう扱うのかが未定義だった。実装者が任意の振る舞いを選ぶと、raw packed value の読取りと public entity identity の生成境界が混同される。
- **対応条件**: typed Invalid、`EntityId.Invalid`、packed値 `0` だけを canonical Invalid とし、public factory と `EntityId` の生成/変換は非0の Property、予約、未知 kind を `ArgumentOutOfRangeException` で拒否する。`EntityId.IsValid` は Node(1)、Relationship(2)、Hyperedge(4) と local pack 可能範囲だけで真にし、canonical Invalid/packed値 `0` 以外の invalid `EntityId.ToPacked` は例外にする。`UnpackKind` は raw 抽出として検証を行わない。
- **検証**: Wave 1 の factory test と PublicApi approval で typed Invalid、packed値 0、Property/予約/未知 kind、noncanonical local、raw unpack、raw constructor 非公開を確認する。

### C-5. Generation materialization の source と logical 境界が未定義

> **設計決定済み・実装未対応(2026-07-12)**: 正本 §2.3、§5.1、§7.1、§15、§16 に physical Sequence と logical full ID の境界を追加した。read/query/traversal/index の実装と test が完了するまで対応済みにはしない。

- **発見日**: 2026-07-12
- **影響**: Wave 1 commit 1
- **内容**: relationship、incidence、adjacency、locator、delta が Sequence だけを保存する一方、typed ID equality は Generation を含む。Sequence をそのまま typed ID として emit すると Generation `0` が logical output へ漏れ、stale entry と slot reuse を区別できない。
- **対応条件**: physical Sequence は内部 address に限定する。logical emit/key、public API、query/traversal、index output は sidecar `CurrentGeneration` から full typed ID を materialize し、Generation `0` を public identity にしない。Generation `> 0` の stale input は reject、derived stale entry は skip する。owner Sequence は参照 relationship/incidence が当該 read snapshot から論理不可視となり reader horizon を越えるまで再利用せず、owner delete は参照 relationship/incidence を同じ logical delete 境界で無効化する。したがって materialization は同じ live owner だけを表し、relationship/incidence の Sequence は再利用後の別 entity へ retarget しない。
- **検証**: read/scan、query/traversal、dense/sparse frontier、index/full-text/vector output、owner delete と relationship/incidence、old snapshot 中の reuse 不可と旧参照の旧 owner 観測、reader 終了と vacuum/reuse 後の non-retarget を各 test と PublicApi approval で確認する。

### C-6. Relationship raw entry の reuse fence が未定義

> **設計決定済み・実装未対応(2026-07-13、Wave 9 移管)**: 正本 §2.3、§5.1、§7.1、§9 Wave 1/9、§15、§16 に raw entry の lifetime、Wave 1 no-reuse、Wave 9 の再利用解放 coordinator を追加した。Wave 1 の no-reuse と materialization、Wave 9 の coordinator と lifecycle test が完了するまで対応済みにはしない。

- **発見日**: 2026-07-13
- **影響**: Wave 1 commit 1
- **内容**: base、delta、locator、epoch entry が raw relationship Sequence を保持したまま再利用すると、sidecar の新 Generation が旧 entry を新 relationship と誤認させる。
- **対応条件**: raw Sequence は physical entry にだけ残し、transaction/query/traversal boundary で materialize する。Wave 1 は reclaim 済み relationship storage を回収しても Sequence を free list へ release せず、create は free 候補を無視して high-water mark からだけ割り当てる。したがって raw entry が残っても ABA は起きない。Wave 9 の `RelationshipReuseCoordinator` が reader horizon、base rebuild、delta/epoch reset、locator rebuild、derived durable の順に完了してから free release する。release 前の crash は safe leak とし、reopen 時に coordinator が再開する。materialization 不能な candidate は skip/not-found にする。
- **検証**: Wave 1 の materializer/no-reuse/old raw non-retarget test と、Wave 9 の coordinator 順序、各境界 crash/reopen、safe leak 再開、実 reuse test で確認する。

### C-7. `LabelNodeIndex.Lookup` と legacy raw-long candidate の境界が未定義

> **設計決定済み・実装未対応(2026-07-13、integration)**: `Lookup` を full `NodeId` を返す logical API に確定した。public `EntityCandidateSet` と filtered vector の direct raw-long contract は Wave 7 まで残す physical compatibility surface (`compatibility adapter`) であり、logical identity API ではない。node query/traversal は full typed `NodeId` の primary `Read` 検証直後の `Sequence` だけを physical lookup に渡す。正本 §2.3、§5.1、§8.1、§15、§16 を参照。

- **該当**: §2.3 identity と version、§5.1 ID、§7.2 `LabelNodeIndex`、§8.1、Wave 1 identity migration、Wave 7 vector。
- **内容**: `LabelNodeIndex.Lookup` の output を physical sequence candidate と logical query/traversal result の双方が共有すると、どちらかが必ず誤る。raw sequence のまま logical pipeline に渡すと generation を失い、vacuum reuse 後に stale slot が別 node を指す。反対に full `NodeId` を返して raw candidate consumer がその packed value を locator、index key、または candidate set に入れると、physical sequence と一致せず empty result になる。public `EntityCandidateSet` と filtered vector の direct raw-long contract は Wave 7 まで残す physical compatibility surface (`compatibility adapter`) であり、logical identity API や query/traversal の入力ではない。
- **失敗シナリオ**: label lookup 後の `ApplyDyadic` が full identity を raw key として扱い候補を失う。filtered KNN または full-text が raw candidate として packed ID を渡し、完全一致すべき graph-first query が空になる。逆に raw output を採ると、同 sequence の新 generation を old reader/query が別 entity として返す。legacy raw-long を query/traversal へ直接渡すと `Read` 検証を迂回する。
- **対応条件**: `Lookup` を full `NodeId` の logical API に固定する。logical pipeline は full typed ID を保持し、node query/traversal が physical lookup に渡すのは full typed `NodeId` の primary `Read` 検証直後の `Sequence` だけである。physical candidate/output は current generation と primary `Read` で full ID に materialize し、stale candidate を skip する。public `EntityCandidateSet` と filtered vector の direct raw-long contract は Wave 7 まで残す physical compatibility surface (`compatibility adapter`) であり、logical identity API ではない。raw long の診断表示はこの adapter と別に、表示・計測だけに閉じる。
- **テスト条件**:
  - label lookup は full `NodeId` を返し、same-sequence/different-generation を raw sequence として返さない。
  - `ApplyDyadic` と `ApplyDyadicOversampleTests` は label 起点の full typed `NodeId` を primary `Read` 検証直後の `Sequence` に変換して physical lookup し、期待する match を返す。
  - filtered KNN と filtered full-text は graph-first/text-first の双方で full ID を保持し、node physical lookup には full typed `NodeId` の primary `Read` 検証直後の `Sequence` を使う。
  - stale label/vector/full-text candidate は materialize 後に skip され、新 generation の entity を返さない。
  - public direct raw-long contract は Wave 7 まで physical compatibility surface として残っても logical identity API にならず、node query/traversal へ直接流入せず、adapter が primary `Read` 検証直後の `Sequence` に限定する。
  - diagnostic の raw long は表示・計測だけに使われ、query/traversal/transaction の入力へ流入しない。

### C-8. EntityVersionMeta の縮約が存続中の SSN call site を破壊する

> **設計決定済み・実装未対応(2026-07-16、Wave 4 移管)**: Wave 3 は entity Generation の正本を一箇所へ固定するが、現行 SSN が使う pstamp/sstamp lane と更新 API は一時維持する。Wave 4 で SSN と全 call site を削除する同じ変更境界に metadata の `(xmin,xmax,generation)` 縮約を移した。正本 §7.2、§7.3、§9 Wave 3/4、§16 を参照。

- **発見日**: 2026-07-16
- **発見者**: Codex
- **影響**: Wave 3、Wave 4
- **内容**: 正本 §7.2 と §9 Wave 3 は `EntityVersionMeta` から pstamp/sstamp lane を除去するよう要求する一方、`Transactions/Transaction.cs` は Wave 4 の SSN 削除まで `Pstamp` / `Sstamp` を commit 判定と post-commit 更新に使用する。Wave 3 で metadata だけを縮約すると production build が成立しない。
- **対応条件**: Wave 3 では pstamp/sstamp の物理 lane と更新 API を維持し、新 property/entity store が同じ sidecar contract で既存 transaction 層に接続できるようにする。Wave 4 で SSN、lock hook、全 pstamp/sstamp call site を削除したうえで、sidecar record、in-memory store、page arithmetic、test を三 laneへ同時に縮約する。一時 sidecar や互換 shim は作らない。
- **検証**: Wave 3 の solution build と既存 SSN regression、Generation source の一意性を確認する。Wave 4 では production source の pstamp/sstamp symbol が 0 件、metadata record が `(xmin,xmax,generation)` のみ、reopen と page-boundary test、single-writer/snapshot suite が成功することを確認する。

## Major

### M-1. Wave 3 のテスト項目が Wave 5 / Wave 9 の成果に暗黙依存する

> **対応済み(2026-07-10)**: visibility 三型を WriterLease と同じ Wave 4 へ割り当て、multi-writer manager との不成立な中間状態を除いた。Wave 3 は store-level clean reopen と stale generation rejection に限定し、crash reopen は Wave 5、vacuum 経由の実 reuse は Wave 9 へ移した。

- **該当**: Wave 3 テスト (L533「Generation reuse、…、reopen」)、Wave 5 (L557-572)、Wave 9 vacuum (L625-638)、§2.3 (L85「vacuum 後に sequence を再利用するときだけ Generation を進める」)
- **内容**: (a) Generation の再利用は仕様上 vacuum(Wave 9)後にしか起きないのに、Wave 3 と Wave 7 のテスト項目に「Generation reuse」がある。(b) Wave 3 の「reopen」テストは durable commit と recovery を要するが、commit/recovery 統合は Wave 5 の成果。Wave 2 は「parser skeleton」(L506) しか提供しない。(c) さらに `Core/Visibility.cs` / `SnapshotState.cs` の rewrite (§7.1 L346) はどの Wave の対象リストにも現れないが、Wave 3 の完了条件「snapshot visibility が動き」(L537) が要求する。
- **修正案**: Wave 3 に「vacuum を経由しない slot 再利用のテスト用 internal hook を提供する」と明記するか、Generation reuse テストを Wave 9 へ移す。reopen テストは「checkpoint 済みクリーン shutdown の reopen に限定、crash reopen は Wave 5」とスコープを書く。`Visibility.cs` rewrite を Wave 1 か Wave 3 の対象リストへ明示的に載せる。

### M-2. checkpoint と WriterLease の関係が未定義

> **対応済み(2026-07-10)**: writer lease 下の sharp checkpoint を採用した。active writer の終了を待つが reader は待たない。buffer pressure は未 commit page の eviction ではなく transaction abort と chunk commit で扱う。正本 §2.1、§2.5、§9 Wave 5、§16 decision log を参照。

- **該当**: §2.1.2 (L62)、§7.3 `Checkpointer`/`AdaptiveCheckpointController` (L380)、§2.5 (L105)
- **内容**: §2.1.2 は lease を取得すべき操作として bulk load / schema mutation / migration / vacuum / index rebuild / segment merge を列挙するが、**checkpoint が含まれていない**。checkpoint が lease を取るなら、長時間の write transaction 中は checkpoint できず WAL が伸び続ける。このリポジトリでは filterchain WAL 146GB の実害があり、`plans/clean-slate-redesign.md` に記録されている。lease を取らないなら、active writer の dirty page と checkpoint の flush が並行し、`CheckpointEnd` の意味(どの LSN までが data file に反映済みか)と C-1 の flush 順序保証を並行制御なしで守る必要があるが、その規定がない。
- **修正案**: checkpoint を「lease 不要の fuzzy checkpoint(dirty page table + active tx を記録)」とするか「lease 必須(= write tx 境界でのみ実行)」とするかを §2.5 に確定し、後者なら long-running write tx 中の WAL 上限策(サイズ契機の強制 checkpoint 待機など)を書く。

### M-3. HNSW segment merge が writer lease 下 — 単一 writer 設計で merge が全書き込みを停止させる

> **対応済み(2026-07-10)**: immutable artifact の構築を lease 外の read snapshot で行い、source manifest generation の再検証と publish commit だけを writer lease 下にした。Wave 7/8 の writer stall gate も追加した。

- **該当**: §4.5 (L222)、§4.6 merge (L232)、§13「segment fan-out」(L775)
- **内容**: merge は「writer lease 下で新 HNSW segment を作成し…一つの commit で切り替える」。HNSW 構築は件数次第で秒〜分単位かかる。writer が一つしかない本設計では、その間ユーザの全書き込みが `WriterWaitTimeout`(既定 5 秒, L809)で失敗し得る。§13 の segment fan-out リスクは「検索の線形悪化」しか見ておらず、**merge による書き込み停止**という単一 writer 固有の裏面リスクに言及がない。10.4 にも writer stall のゲートがない。
- **修正案**: 「HNSW 構築は lease 外で immutable segment をオフライン構築し、lease 取得は manifest 切り替え commit の短時間のみ」と明記する(immutable segment ならソースは snapshot read で足りるため実現可能)。10.4 に「merge 中の writer wait p99」ゲートを追加する。

### M-4. §10.4 の baseline 定義が自己矛盾(基点 commit で測定不能な基準値を含む)

> **対応済み(2026-07-10)**: `plans/single-writer-redesign-baseline.md` を追加し、各 gate の出所、再現コマンド、測定環境、基点 commit で測定できない構造の扱いを分離した。正本 §10.4 は出所列を持つ表へ変更済み。

- **該当**: §10.4 前文 (L688「基点 commit の baseline と比較する」) と表 (L692-698)、Wave 8 完了条件 (L621)
- **内容**: 前文は「基点 commit `ee811d1` の baseline と比較」と宣言するが、表の値の一部は基点 commit では測定不能。特に「full-text 4 segment p50 8.55 ms」— 基点実装は mutable postings tree であり segment 構造を持たないため、「4 segment」という測定条件が基点に存在しない(Wave 8 完了条件は正しく「`clean-slate` baseline gate」と別ソースを指しており、10.4 前文と食い違う)。commit の 1163.80 µs、WAL 11.74x も「`clean-slate` ARIES baseline」と表内に書かれており、出所が前文と不一致。また前文が「同じマシン」比較を掲げる一方で絶対値 (1.8982 ms 等) を本文に焼いており、測定環境が変わった瞬間に全ゲートが無意味になる。
- **修正案**: 各ゲート行に baseline の出所(基点 commit 実測 / clean-slate spike 実測)を列として明記し、可能な限り絶対値でなく「baseline 比 ≤ N.x」の相対表現に統一する。絶対値を残すなら測定環境(マシン・runtime・データセット)を本書か参照先に固定記載する。

### M-5. §8 破壊的変更一覧の漏れ

> **対応済み(2026-07-10)**: public vector store 型、transaction interface 分割、transaction handle の同時使用契約、migration history sidecar 廃止を正本 §8 へ追加した。破壊変更は依存実装と同じ Wave 3/6/7/8/9 に割り当て、Wave 1で未実装の新旧 modelを二重公開しない。

- **該当**: §8 (L430-464)
- **内容**: 本文で削除・変更が明言されているのに §8 に載っていない public 面の変更:
  1. **public `InMemoryVectorStore` / `JsonFileVectorCatalog` の型削除** (§7.1 L349) — `IVectorStore` の行では型自体の消滅は読み取れない。
  2. **`MigrationHistory` sidecar text file の廃止** (§7.6 L417, Wave 9 L630) — 運用者が触れるファイルが消える永続形式変更だが §8.2 に記載なし。
  3. **`IGraphTransaction` の read/write interface 分割** (§7.1 L350) — `BeginTransaction` の行はメソッド変更のみで、利用者コードが型名で受けている `IGraphTransaction` 自体の運命(削除? read 側に改名?)が §8.1 にない。
  4. **`TransactionUsageLease` の挙動変更**(thread 固定→同時使用検出, §7.3 L383)— 例外送出条件の変化は §8.3 の `ConcurrentTransactionUseException` 追加と対で説明すべき。
- **修正案**: 上記 4 件を §8.1 / §8.2 に追記する。

### M-6. vacuum 回収順序と「payload 欠落 = primary corruption」規則が誤検知しうる

> **対応済み(2026-07-10)**: corruption 判定を horizon 上で到達可能な primary property version に限定した。property version と専有 payload は同じ write transaction で回収し、stale derived entry は candidate revalidation で除外する。

- **該当**: §4.1 Update/Delete step 4 (L168「property、payload、index entry、incidence、entity slot の順」)、§5.5 (L313「property ref が durable なのに payload が無い状態は primary corruption として open/recovery を失敗させる」)
- **内容**: §5.5 の corruption 規則が「visible な property version」に限定されていない。vacuum の順序は property→payload だが、crash がこの 2 ステップの間に落ちると「reclaim 済み property version(または reclaim commit が durable になる前の状態)が、すでに回収された payload を指す」中間状態が heap 上に残り得る。字義通りに実装すると open/recovery が正常な GC 中間状態を corruption と誤判定し、DB が開けなくなる。また payload より**後**に回収される index entry は必然的に dangling ref 期間を持つが、これが「正常」である旨は §4.5 の revalidation 記述から推測するしかない。
- **修正案**: §5.5 の規則を「snapshot horizon 上で visible な property version が payload を欠く場合のみ corruption」と限定し、vacuum の property/payload 回収を単一 commit にする(または回収順を payload が最後になるよう定義する)ことを明記する。

## Minor

### m-1. §2.4 と §7.2 の「column cache」齟齬

> **対応済み(2026-07-10)**: column cache を現行 derived data 列挙から外し、将来再導入時も derived data とする注記へ変更した。

§2.4 (L91) は「column cache は derived data である」と目標アーキテクチャの構成要素として列挙するが、§7.2 (L368) は `ColumnCatalog` / `ColumnManager` / `ScalarColumnStore` を Delete し「必要なら別トラックで再導入」とする。§2.4 の列挙から column cache を外すか、「(将来の再導入時も) derived data として扱う」と注記して整合させるべき。

### m-2. 「committed high-water/gap」の gap の意味が本文で説明されない

> **対応済み(2026-07-10)**: gap を high-water 以下の aborted txId 集合として定義し、安全な prune 条件を §7.1 に追加した。

§7.1 (L346) に「committed high-water/gap だけを扱う」とあるが、単一 writer + page-level undo なら abort された txid の書き込みは物理的に巻き戻るため、可視性判定は high-water 単独で足りるはず。gap が必要になる条件(undo 未完で crash した loser の txid? faulted 状態?)を §2.2 か §7.1 に一文で定義しないと、実装者が SnapshotState に不要な集合を持ち込むか、逆に必要な gap 追跡を落とす。

### m-3. §4.3 step 6 の再構築コストが未検討

> **対応済み(2026-07-10)**: next tx id と Generation high-water を checkpoint 済み catalog から復元し、checksum 不良時だけ明示 repair scan を要求する規則へ変更した。

「next tx id、Generation high-water を primary metadata から再構築」(L201) は素直に読むと全 slot scan であり、大規模 DB の open 時間に直結する。catalog への永続化(checkpoint 時に高水位を書く、現行 ARCH-4 の committed 高水位 catalog 永続化と同型)で回避できるはずで、方式を一文明記すべき。

### m-4. SnapshotRegistry の reader リーク対策がない

> **対応済み(2026-07-10)**: active count、oldest age、開始位置を診断 API と警告へ公開し、安全性を壊す強制失効は行わない規則を §2.2 と Wave 4 へ追加した。

§2.2.6 で read 登録が vacuum horizon を決めるが、dispose されない reader が horizon を永久に留める。リーク検出(Wave 4 テストの「lease leak」は writer 側のみ)や上限・警告 metric(§9 Wave 9 の「oldest snapshot age」metric はあるが、それに基づく方針がない)を §2.2 か Vacuum 節に足すべき。

### m-5. 用語の揺れ

> **対応済み(2026-07-10)**: public index definition の派生型名を §5.4 に追加した。snapshot の watermark は `CommittedHighWater`、aborted 集合は `AbortedGaps` に統一する実装指示とした。

- `Snapshot(ReadVersion, ActiveWriterId?)` (L71) の `ReadVersion` と、§4.2 の「snapshot high-water」(L185)、§7.1 の「committed high-water」— 同一概念なら名称を統一。
- `VectorIndexDefinition` (L220, L423, L442) と §5.4 の `IndexDefinition`(Kind=Vector) の関係(派生型であること)が §5.4 本体に書かれておらず、Wave 1 の「`IndexDefinition` 派生型」(L494) から逆算するしかない。§5.4 に派生型名を列挙すべき。

### m-6. HYP トラックの性能ゲートが §10.4 に引き継がれていない

> **対応済み(2026-07-10)**: hyperedge traversal の degree 10、100、1000 に対する binary/view 比3.0x gate と出所を §10.4 に追加した。

hyperedge store は Rewrite 対象(§7.2 L360, incidence L365)なのに、§10.4 には hyperedge 走査ゲート(HYP-6c で合格した走査 ≤2.14x)に相当する行がない。traversal 行 (L694) は relationship 2-hop のみ。hyperedge 走査の回帰ゲートを追加するか、対象外とする根拠を書くべき。

### m-7. §4.2 faulted 経路の lease 解放

> **対応済み(2026-07-10)**: faulted 遷移でも lease を一度だけ解放し、instance の fault flag が新 operation を拒否する規則を §2.1 と Wave 4 に追加した。

§2.1.6 (L66) の「全経路で一度だけ解放」の列挙(commit / abort / dispose / commit 失敗)に、L192 で導入される「fsync 成功後の publish 失敗 → faulted」経路が含まれていない。faulted 時に lease を解放するのか(instance ごと閉塞するので解放不要なのか)を §2.1.6 に一言追加すべき。

## 良い点(簡潔に)

- Wave 0(ドキュメント・skill 整合化を最初に置く)の根拠 §12.3 は、過去の「✅ 再実装禁止」規則との衝突を正しく予見しており説得力がある。
- §5.3 の owner を property record に焼く判断、§2.5 の presume-committed 排除、§12.2 の historical record 保全方針は、既知バグ #1 と過去のフィードバック(タスク番号の非露出、実測主義)と整合している。
- 「未決定事項は残さない」(§15) と §16 decision log の追記プロトコルは、委譲実装での曖昧 TODO 散乱を防ぐ規律である。2026-07-10 の修正版では C-1〜C-3、M-1〜M-6、m-1〜m-7 の対応を正本へ反映した。

## 総括

方向性(single writer + snapshot readers、primary/derived 分離、strict commit)は健全である。
2026-07-10 の修正版は本書の全指摘を正本、プロセス、Wave 指示書へ反映した。
実装者は「対応済み」ラベルだけでなく、参照先の正本に残存矛盾がないことを着手前に検索して確認する。
