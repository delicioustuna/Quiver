# ライブラリ洗練トラック (REF) — サブエージェント委託用 実装計画書

> **Codex レビュー判断 (2026-07-02): v1 の非同期 tx API (commit `c2ee592`) は全面撤回する。**
> 本版を撤回方針の正本とし、実体変更は REF-16 の実行時に行う。
> 変更点: REF-16 (async 撤回) の新設、REF-8 の同期専用化、REF-9 のスレッドアフィニティ検出への復帰、
> REF-10 の「唯一の非同期書き込み入口」への昇格 (凍結前必須化)、async-transaction-context-safety.md (P0) の停止決定。
> 撤回判断の根拠は本文の該当節および同計画書の停止注記を参照。
>
> **現時点の変更は plans/ の設計書 2 ファイルのみ。** ソリューション実体 (src / tests / samples /
> docs / slnx / approved.txt) は無変更であり、撤回の実体変更箇所は REF-16 の「撤回対象の実体 (転記)」に
> 記録して REF-16 の実行に委ねる。

> 起票日: 2026-07-02。起点: develop 計画群 + docs/spec + 公開 API 表面 (approved.txt 843 宣言) の外部レビュー。
> 本書は **REF-1〜16 の 16 タスク**に分割した実装計画と、各タスクで遵守すべき判断ポイントを定める。
> 小タスクは 1 サブエージェントセッションで完結する。工数が 2 日を超える中タスクは、
> 記載された手順の完了条件を変えず、実装前に独立して build green となる実行単位へ分割して委託する。
> 承認待ち・外部公開・タグ作成を同一セッションに含めない。判断ポイントの「既定」から逸脱したくなった場合は
> **実装せず、根拠を添えて報告に戻す**こと (勝手に代替案を実装しない)。
>
> 中心テーマ: 「足りないのは機能ではなく **順序と既定値**」。
> (1) API 表面のダイエットを 1.0 凍結より前に、(2) 安全機構は default-on に、
> (3) writer queue を製品 API に、(4) FTS は構造化 AST を主契約に、(5) Import/Export を前倒し。

---

## 0. 全タスク共通の遵守事項 (ガードレール)

各サブエージェントは以下を**無条件で遵守**する。違反が必要になった時点でそのタスクは中断・報告。

| # | 遵守事項 |
|---|---|
| G-1 | **正確性を一切落とさない。** MVCC 可視性・snapshot isolation・SSN・WAL 耐久性・crash recovery 契約を弱める変更は禁止。 |
| G-2 | **FormatVersion を bump しない。** 1.x 内は on-disk フォーマット固定方針 ([docs/spec/08_known_limits.md](../docs/spec/08_known_limits.md#no-migration))。フォーマット変更が必要と判明したら中断・報告。 |
| G-3 | **PublicApi approval baseline** ([tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt](../tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt)) の差分は全行を目視レビューし、意図した差分のみをコミットする。想定外の public 露出が出たら設計を見直す。 |
| G-4 | **全テスト緑を維持。** `dotnet build Quiver.slnx` + `dotnet test Quiver.slnx`。crash contract / chaos テストを含む。各タスクは独立して build green (途中中断しても品質が下がらない単位でコミット)。 |
| G-5 | **実測先行。** 性能に関する主張は `--basic-perf` runner / BenchmarkDotNet の before/after 実測を伴う。ホットパス (トラバーサル反復・commit 経路) への追加コードは 0-alloc / 定数コストであることを確認。 |
| G-6 | **as-built 原則。** 挙動・契約が変わる変更は docs/spec/ の該当章・README・cookbook を同一タスク内で更新する。「コードだけ直して文書が古い」状態でタスクを終えない。 |
| G-7 | **新規 public API は XML doc 必須。** XML doc に内部設計参照 (design NN 等) を書かない ([plans/v1-consolidation.md](v1-consolidation.md) §B.1 の降格ポリシー準拠)。 |
| G-8 | **スコープ外に手を出さない。** リファクタの誘惑 (隣のコードの改善等) は TODO として報告し、タスクスコープに含めない。 |

---

## 依存グラフと推奨実行順序

```
Wave 0 (衛生):        REF-1
Wave 1 (API 表面確定): REF-2 ──┐
                      REF-3 ── REF-6 ──┤
                      REF-4 ── REF-5 ──┤
                      (typed-write-sinks Phase 1 = 既存計画書) ──┤
Wave 2 (安全既定):     REF-16 ── REF-8, REF-9 ─────────────────┤
                                                              ▼
                                                    REF-7 (1.0 凍結ゲート)
Wave 3 (利用体験):     REF-10 ── REF-11      (REF-10 は凍結前必須 — 撤回後の唯一の非同期書き込み入口)
Wave 4 (運用・前倒し): REF-12 ── REF-13      (Export→Import。凍結前推奨)
Wave 5 (観測性):       REF-14, REF-15       (追加的 = 凍結後でも可)
```

- **REF-7 (凍結) より前に必ず完了させるもの**: ZD-2, VP-3, REF-16, REF-2, REF-3, REF-4, REF-6, REF-8, REF-9, REF-10
  (public 表面の縮小・既定挙動の変更は 0.x でしか無料でできない。REF-10 は撤回後の唯一の非同期書き込み入口となるため必須へ格上げ)。
  REF-5, REF-12/13 も凍結前完了が望ましい。
- **既存計画との関係**: [ga-readiness.md](ga-readiness.md) の GA-1 は REF-8 に、GA-2〜4 は REF-4/REF-5 に**置換 (supersede)** される。
  GA-5 (ITokenFilter)・GA-6〜11 (テスト拡充) は本書と独立で、いつでも並列実行可。
  [typed-write-sinks-and-query-patterns.md](typed-write-sinks-and-query-patterns.md) は既存計画のまま有効 (public API を増やすため凍結前に Phase 1 を完了させる)。
  [pw19-numeric-range-index-pushdown.md](pw19-numeric-range-index-pushdown.md) は追加的変更のため凍結後でも可だが、GA 前実施を推奨 (性能崖の解消)。
- **2026-07-02 反映 (develop の新実装との整合)**: 非同期 API (`BeginTransactionAsync` / `CommitAsync` / `DisposeAsync`、commit `c2ee592`) と
  インメモリモード (`GraphDatabase.CreateInMemory`、commit `4009e19`) の導入を受け、REF-2 / REF-8 / REF-9 の前提を改訂した。
- **2026-07-02 改訂 (撤回方針確定)**: 非同期 tx API は **REF-16 で撤回**する。理由: (1) `BeginTransactionAsync` /
  `DisposeAsync` を安全化するには [async-transaction-context-safety.md](async-transaction-context-safety.md) の
  文脈 tx 所有化 (write/recovery 最深部の恒久改修) が必須、
  (2) 安全化後も「await またぎの tx 保持」が自然に書けるようになり、単一 writer + WAL 切り詰めピン留め (§short-transactions) と
  相性が最悪、(3) 非同期の実利 (呼び出しスレッドの解放) は REF-10 の queue が構造的に安全な形で全て提供する、
  (4) main 未反映の現時点なら撤回コストがゼロ。`CommitAsync` 単体は終端処理として技術的に分離可能だが、
  v1 では async 契約を二系統にせず TCB と公開面を最小化する方針を優先して意図的に撤回する。
  これに伴い同計画書 (P0) は**停止** (対象 API の消滅により不要化。
  発見欠陥の記述は再導入検討時の一次資料として保持)。
  [cross-platform-ci-and-release-gates.md](cross-platform-ci-and-release-gates.md) / [benchmark-regression-baseline.md](benchmark-regression-baseline.md) /
  [build-reproducibility-and-coverage.md](build-reproducibility-and-coverage.md) / [embedded-rag-comparative-benchmarks.md](embedded-rag-comparative-benchmarks.md) は
  本トラックと独立・補完 (CI/release gate は REF-7 実行前に整備されていることが望ましい)。

> **Codex レビューコメント (2026-07-02、commit `bf0b476` 確認後)**:
> develop の非同期 API / インメモリモードと整合させた REF-2 / REF-8 / REF-9 の改訂は妥当。
> 追加確認で、(1) P0 計画では未決定のカーソル生存期間を REF-9 が `MoveNext` 単位に先決めしていたこと、
> (2) 並行使用時の例外型が P0 の `TransactionException` と REF-9 の `InvalidOperationException` で不一致だったこと、
> (3) 直列 writer queue では複数 flush request が同時に存在せず group commit 効果を期待できないこと、の 3 点を検出した。
> 以下の REF-9 / REF-10 は、カーソル契約を実測・既存挙動から決める、非 transient な reason code 付き
> `TransactionException` に統一する、group commit の性能主張を削除する、という判断で修正済み。
>
> **撤回方針での扱い**: Codex 指摘 (1)(2) は非同期 API の撤回により対象が消滅する
> (REF-9 はスレッドアフィニティ検出へ復帰し、カーソル生存期間の監査も `ConcurrentUse` 例外分類も不要になる)。
> (3) は非同期の有無と無関係に正しく、REF-10 に反映済みのまま維持する。
>
> **Codex 実装現状レビュー (2026-07-02、commit `dbca321` 確認後)**:
> 全面撤回を v1 の方針として承認する。ただし根拠と実装境界を次のように補足する。
> (a) `CommitAsync` 単体は await 前に WAL/MVCC 文脈を終了し rollback 情報を捕捉する終端 API として
> 技術的には残せるが、API 一貫性・TCB 最小化のため政策的に撤回する、
> (b) `c2ee592` は `ListVectorIndexes` と排他機構も含むため commit 全体を機械的に revert せず、
> REF-16 の棚卸しを正として外科的に除去する、
> (c) 公開 async 撤回は WAL 内部の `Channel<FlushRequest>` / flush worker の撤去を意味しない、
> (d) REF-10 は bounded queue・backpressure・shutdown 時の未実行 job 契約を必須とする、
> (e) 擬似 async terminal が提供していたキャンセル確認は同期 cursor の公式利用手順として残す。

---

## Wave 0 — 衛生

### REF-1: 計画書記載と実装実態の同期 (recovery clobber ほか)

- **目的**: 「握りつぶさない」原則の裏面として、**直った問題が「未修正」のまま残る**のも監査の信頼性を損なう。計画書群の stale 記載を実態に同期する。
- **対象**: `src/Quiver/Transactions/RecoveryManager.cs`、関連 crash contract テスト、`plans/typed-write-sinks-and-query-patterns.md`、`plans/v1-consolidation.md`、その他状態記載を持つ `plans/*.md`。
- **背景**: [typed-write-sinks-and-query-patterns.md](typed-write-sinks-and-query-patterns.md):23 と [v1-consolidation.md](v1-consolidation.md):57,169 に「v1 監査 #1 recovery Pass-3 loser-undo clobber = HIGH **未修正**」の記載が残るが、[src/Quiver/Transactions/RecoveryManager.cs](../src/Quiver/Transactions/RecoveryManager.cs) には presume-committed 2 相 recovery による clobber 対策の実装とコメントが既に存在する。
- **実装手順**:
  1. RecoveryManager の該当経路 (第 2b パス presume-committed / 第 3 パス論理 undo) を精読し、監査 #1 のシナリオ (loser Delete の undo = UpsertRaw 無条件上書きが committed 値を clobber) が**現行コードで再現不能であることをテストで確認** (既存 crash contract テストに該当ケースがあるか探し、無ければ 1 本追加)。
  2. 確認が取れたら計画書 2 箇所の「未修正」記載を「修正済み (commit 参照)」へ更新。確認が取れなければ **08_known_limits.md に「既知・未修正・HIGH」として収録** (v1-consolidation の元方針どおり)。
  3. ついでに他 plans/*.md の「状態」欄で完了済みタスクの stale 記載がないか棚卸しし、機械的に更新。
- **判断ポイント (遵守)**:
  - 「コメントに対策が書いてある」ことは修正済みの証拠にしない。**テストで再現不能を示せた場合のみ**「修正済み」と記載する。どちらとも言えない場合は「要調査」として報告。
- **完了条件**: clobber シナリオの検証テストが存在し緑。plans/ に実態と矛盾する記載が残らない。
- **依存関係**: なし。
- **工数**: 小 (0.5〜1 日)。

---

## Wave 1 — API 表面の確定 (1.0 凍結の前提条件)

### REF-2: backend 抽象の internal 化 (公開契約からの除去)

- **目的**: `IGraphStorageBackend` を public 契約から外す。**前提改訂 (2026-07-02)**: インメモリモード (commit `4009e19`) により実装は binary / in-memory の 2 つになったため「単一実装の dead generality」根拠は失効した。しかし**両実装ともコア `Quiver` アセンブリ内**であり、外部から backend を差し込む経路は存在しない以上、抽象・factory・`BackendKind` を public に保つ理由は依然ない。利用者に見せる入口は `GraphDatabase.Open` / `GraphDatabase.CreateInMemory` という意図の明確なメソッドに限定する。
- **対象**: [src/Quiver/Backend/](../src/Quiver/Backend/) — `IGraphStorageBackend.cs` / `IGraphStorageBackendInternal.cs` / `IGraphStorageBackendFactory.cs` / `BinaryGraphStorageBackendFactory.cs` / `InMemoryGraphStorageBackend.cs` / `InMemoryGraphStorageBackendFactory.cs` / `BackendKind.cs` / `BulkLoadCapabilities.cs`、`GraphDatabase.cs` の配線、`tests/Quiver.Backend.Tests/` の契約テスト、approved.txt。
- **実装手順**:
  1. 現状の public 露出を棚卸し (approved.txt を grep)。`BackendKind` / factory / 抽象が公開面のどこから到達可能か (GraphDatabaseOptions / `CreateInMemory` 経路等) を列挙。
  2. **internal 化** (削除ではない): 型は残し `public` → `internal` に降格。backend の選択は `Open` / `CreateInMemory` の 2 入口に集約し、`BackendKind` を公開面から除去。
  3. backend 契約テスト (binary / in-memory のパラメタ化) は InternalsVisibleTo で維持。
  4. approved.txt 再生成 → 差分レビュー (G-3)。docs/api/concepts/backends.md 等の記述を「エンジンの内部構成」として更新 (インメモリモードの利用者向け説明は `CreateInMemory` 基準で書く)。
- **判断ポイント (遵守)**:
  - **既定 = internal 化。物理削除はしない** (契約テスト資産と将来の backend 追加余地を保持しつつ、公開契約からは外す)。削除まで踏み込みたくなっても行わない。
  - `GraphDatabaseOptions` 等のシグネチャから backend 型・`BackendKind` が漏れている場合、そのメンバは除去 (0.x なので破壊可)。ただし除去リストは完了報告に明記。
  - インメモリモードの**機能自体には触れない** (in-memory の永続性契約・`NullWriteAheadLog` の扱いは [in-memory-mode.md](in-memory-mode.md) のスコープ)。
- **完了条件**: approved.txt から backend 抽象系・`BackendKind` が消え、公開面に残る backend 関連の入口が `Open` / `CreateInMemory` のみで、全テスト緑。
- **依存関係**: なし。REF-3 より先に完了させる。
- **工数**: 小〜中 (1 日)。

### REF-3: public API インベントリと降格候補の承認

- **目的**: 843 public 宣言は v1 で凍結する契約としては広い。「意図して公開している API」だけを残す。
- **対象**: `tests/Quiver.PublicApi.Tests/PublicApi/*.approved.txt`、`src/Quiver.SourceGen/`、README、samples、docs/api、docs/spec、`plans/ref3-api-inventory.md`。
- **実装手順**:
  1. approved.txt の全 public 型・メンバを走査し、各項目を分類:
     - **KEEP**: (a) README / cookbook / samples / docs/api が利用例を示す、(b) **Source Generator の生成コードが参照する** (生成コードはユーザアセンブリでコンパイルされるため internal 化不可 — [src/Quiver.SourceGen/](../src/Quiver.SourceGen/) の emitter 群を grep して参照型を機械的に列挙すること)、(c) docs/spec が公開契約として記述する、のいずれか。
     - **DEMOTE 候補**: 上記いずれにも該当しない public (実装都合の公開・テスト都合の公開・歴史的公開)。
     - **UNCLEAR**: 判断がつかないもの。
  2. 分類結果を `plans/ref3-api-inventory.md` として出力 (項目・分類・根拠 1 行)。
  3. DEMOTE 候補と UNCLEAR 項目をユーザへ提示し、項目単位の承認結果をインベントリへ記録する。**承認待ちでこのタスクを終了し、コードは変更しない。**
- **判断ポイント (遵守)**:
  - **承認前に internal 化を始めない。** 第 1 段階の成果物はレポートのみ。
  - SourceGen 参照型の列挙は**推論ではなく emitter ソースの grep + 生成コードのコンパイル確認**で行う (`tests/*SourceGen*` のスナップショット/コンパイルテストを活用)。
  - `Quiver.Rag` / `Quiver.Hosting` / `Quiver.OpenTelemetry` の public 表面も対象に含める (approval テストが無ければ追加を提案として報告)。
- **完了条件**: 全 public 宣言を分類したインベントリ文書が存在し、DEMOTE/KEEP/UNCLEAR の判断とユーザ承認結果が記録されている。コードと approved.txt に差分がない。
- **依存関係**: REF-2 完了後が効率的。
- **工数**: 小〜中 (1 日)。

### REF-4: FTS 構造化クエリ AST (`FtsQuery`) — 主契約の新設

- **目的**: GA-2〜4 (prefix / boolean / fuzzy) を文字列ミニ言語として直接実装せず、**合成可能な型安全 AST を主契約**として先に設ける。RAG 層・MCP・ユーザコードが文字列連結とエスケープなしでクエリを組めるようにする。文字列構文 (REF-5) はこの上の薄い糖衣とする。
- **対象**: `src/Quiver/Index/FullText/` に新規 `FtsQuery.cs` (AST)、[src/Quiver/Operators/Bm25Scorer.cs](../src/Quiver/Operators/Bm25Scorer.cs)、[src/Quiver/Operators/FullTextScanOperator.cs](../src/Quiver/Operators/FullTextScanOperator.cs)、[src/Quiver/Client/GraphTraversalSource.cs](../src/Quiver/Client/GraphTraversalSource.cs) (`g.Search(index, FtsQuery, k)` オーバーロード)。
- **API 形 (確定イメージ)**:
  ```csharp
  public abstract record FtsQuery
  {
      public static FtsQuery Term(string term);
      public static FtsQuery Prefix(string prefix);            // TermRange scan
      public static FtsQuery Fuzzy(string term, int maxEdits); // maxEdits 1..2
      public static FtsQuery And(params FtsQuery[] operands);
      public static FtsQuery Or(params FtsQuery[] operands);
      public static FtsQuery Not(FtsQuery positive, FtsQuery negative); // AND-NOT (差集合)
  }
  ```
- **実装手順**:
  1. AST 定義 (immutable record 階層、上記 factory のみ公開。具象型は internal で可 — approved.txt を最小に)。
  2. 実行配線: 単一 Term は既存 WAND 経路そのまま。`Or` = 複数 term の既存 term-at-a-time 合流に帰着。`And` = multi-cursor intersection (postings カーソルの sorted merge)。`Not` = positive 側走査中の除外フィルタ。`Prefix` = `PostingsKey.TermRange()` で term 展開 → `Or` に脱糖。`Fuzzy` = Levenshtein 候補生成 → `Or` に脱糖。
  3. 既存 `g.Search(index, string, k)` (単純クエリ) は互換維持し、内部で `FtsQuery` に変換。
  4. テスト: 各ノードの正確性 (既知コーパスの期待ヒット集合)、スコア一致規約、prefix 展開上限、fuzzy 距離境界。
- **判断ポイント (遵守)**:
  - **トップレベル純否定 (`Not` の positive 無し相当) は非対応** — 全コーパス走査になるため `ArgumentException`。`Not` は必ず差集合形 (positive AND NOT negative) とする。
  - **Prefix 展開の上限**: 既定 **1024 term**。超過時は例外 (静かな部分結果を返さない)。上限は `Search` オプションで調整可。
  - **Fuzzy は maxEdits ≤ 2** に制限。候補生成は postings に存在する term に限る (辞書外候補を生成しない)。
  - **スコア規約を壊さない**: text-first / graph-first の per-doc スコア一致 (FTS-4 由来、[rag-fts-track.md](rag-fts-track.md) 参照) と WAND の「取りこぼしなし」保証 (08_known_limits.md §bm25-stats) を全ノード種で維持。WAND 上限計算が AND/OR 配下でも安全側 (過大評価は可、過小評価は不可) であることをテストで固定。
  - **日本語 (bigram) との整合**: Term/Prefix はトークナイズ後の term 空間に対して動作することを文書化 (bigram 索引に対する Prefix の意味を spec に明記)。
- **完了条件**: `g.Search(index, FtsQuery, k)` が動作し全テスト緑。docs/spec/07_fulltext.md に AST 節を追記。approved.txt 差分は factory + Search オーバーロードのみ。
- **セッション分割**: (A) AST・public API・単一 Term 互換、(B) And/Or/Not 実行、(C) Prefix/Fuzzy・性能/文書。各単位を独立コミットし、単位ごとに build・関連テストを緑にする。
- **依存関係**: なし (GA-5 ITokenFilter と独立)。
- **工数**: 中 (合計 3〜5 日)。

### REF-5: FTS 文字列構文パーサ (糖衣) + fuzz テスト

- **目的**: `"graph AND database NOT vector"` / `"quiv*"` / `"quiver~1"` を **REF-4 の `FtsQuery` に変換する薄いパーサ**として提供 (GA-2〜4 の表層構文を統合実装)。
- **対象**: 新規 `FtsQueryParser.cs`、`g.Search(index, string, k)` の拡張構文 opt-in。
- **実装手順**:
  1. 再帰降下パーサ (トークン: TERM / AND / OR / NOT / `*` 後置 / `~N` 後置 / 括弧 / 引用符)。出力は `FtsQuery` のみ — パーサは検索実行に一切関与しない。
  2. エスケープ規則を先に文書化してから実装 (引用符内リテラル、`\` エスケープ、予約語 AND/OR/NOT の扱い)。
  3. **fuzz テスト**: FsCheck (既にユニットテストで使用中) で任意文字列を投げ、「`FtsQueryParseException` 以外の例外を投げない」「パース成功時は必ず有効な AST」を性質として固定。
  4. round-trip テスト: `FtsQuery` → 文字列化 → パース → 同一 AST。
- **判断ポイント (遵守)**:
  - **既存の単純クエリ動作を変えない**: 拡張構文の有効化方法は「`SearchQuerySyntax.Simple` (既定・現行互換) / `SearchQuerySyntax.Extended`」のオプション明示とする。既定を Extended にしない (既存ユーザの検索語に `*` や `AND` が含まれても挙動不変であること — RAG では検索語はほぼ外部入力である)。
  - パースエラーは位置情報付きの専用例外。**部分パースや黙殺フォールバックをしない。**
- **完了条件**: 拡張構文の正常系/異常系/fuzz テスト緑。cookbook に構文リファレンス追記。
- **セッション分割**: (A) 構文・エスケープ仕様と再帰下降パーサ、(B) opt-in 配線・fuzz/round-trip・文書。各単位で build green を維持する。
- **依存関係**: REF-4。
- **工数**: 中 (合計 2〜3 日)。

### REF-6: 承認済み public API の降格

- **目的**: REF-3 で承認された DEMOTE 項目だけを公開契約から除外し、v1 で維持する API 表面を確定する。調査・承認と実装を別セッションに分離し、未承認の破壊的変更を防ぐ。
- **対象**: `plans/ref3-api-inventory.md` で承認済みの型・メンバ、対応する `src/`、`tests/`、InternalsVisibleTo 設定、PublicApi approval baseline、影響する README/docs/samples。
- **実装手順**:
  1. インベントリの承認状態を確認し、承認済み DEMOTE 項目だけを作業リストとして固定する。UNCLEAR・未承認項目には触れない。
  2. 対象を `internal` 化し、テスト都合の参照は必要最小限の InternalsVisibleTo で維持する。Source Generator の生成コードから到達する型は降格対象から除外する。
  3. 全 approval baseline を再生成し、作業リストとの一対一対応を目視確認する。想定外の削除・追加があれば実装を戻して報告する。
  4. README/docs/samples の参照を更新し、build・全テスト・Source Generator の生成コードコンパイルを確認する。
- **判断ポイント (遵守)**:
  - **REF-3 の承認済みリストが唯一の変更許可範囲**。実装中に見つけた追加候補を同時に降格しない。
  - 物理削除ではなく internal 化を既定とする。削除が必要な場合は実装せず理由を報告する。
  - approval baseline の差分行数ではなく、型・メンバ単位で承認リストとの対応を確認する。
- **完了条件**: 承認済み項目だけが public baseline から消え、縮小前後の宣言数と変更対象一覧が報告され、全テストが緑。
- **依存関係**: REF-2、REF-3 のユーザ承認完了後。
- **工数**: 小〜中 (1 日)。

### REF-7: 1.0 凍結ゲート (チェックリスト実行)

- **目的**: v1-consolidation §E の「唯一の不可逆ステップ」を、前提条件を検証してから実行する。
- **対象**: `Directory.Build.props`、全 PublicApi approval baseline、`docs/api-stability.md`、README、リリース前検証記録。
- **前提条件 (すべて満たすまで着手しない)**:
  - [x] ZD-2 (Core の Logging.Abstractions 除去 + EventSource / Hosting bridge) 完了 — commit `345b249`
  - [ ] VP-3 (FormatVersion V2 + 自己記述 vector catalog + per-index HNSW レイアウト) 完了
  - [x] ベクトル要素表現の契約予約 (`VectorElementType`) 完了 — 2026-07-03。量子化埋め込み
    (int8 等) の将来対応に備え、格納表現を `VectorIndexSpec` / catalog entry / payload ヘッダ
    (offset 12) に永続化。現在は Float32 のみ許可し、未対応値は作成時・読込時・open 時に
    `VectorException` で拒否。catalog は末尾フィールド追加 (V2 の長さプレフィクス機構) のため
    FormatVersion 据え置き。スコアリングカーネルの抽象化は 2 つ目の表現の実装時に行う
  - [ ] REF-2 (backend 畳み込み) / REF-6 (承認済み API 降格) 完了
  - [ ] REF-4 (FtsQuery) 完了 — 検索エントリのシグネチャは凍結後に変えられない
  - [ ] REF-16 (非同期 tx API 撤回) 完了 — 撤回は公開後 MAJOR になるため凍結前が唯一の機会
  - [ ] REF-8 / REF-9 (既定挙動の変更) 完了 — 既定値変更は 0.x でしか無料でできない
  - [ ] typed-write-sinks Phase 1 (`MergeRelationship` + 終端シンク) 完了 (public API 追加のため)
  - [ ] REF-10 (writer queue API) 完了**必須** (REF-16 撤回後の唯一の非同期書き込み入口であり、v1 の推奨書き込みパターンとして文書と一体化するため)
- **実装手順**:
  1. 前提条件をコミット・テスト結果・approval 差分で検証し、チェックリストへ根拠を記録する。
  2. `Directory.Build.props` の `VersionPrefix=1.0.0`、approval baseline の v1 契約宣言、[docs/api-stability.md](../docs/api-stability.md) の「0.x は無保証」節、README の pre-release 記述を同一コミットで更新する。
  3. build・全テスト・package validation を実行し、公開可能な状態で停止する。
- **判断ポイント (遵守)**:
  - 前提条件が 1 つでも未達なら**凍結を実行せず報告に戻す**。「あと少しだから」で凍結しない。
  - NuGet 公開、`PackageValidationBaselineVersion=1.0.0` の設定、release tag 作成・push はこのタスクに含めない。公開後の状態を確認して行う別のユーザ承認付きリリース操作とする。
- **完了条件**: v1 契約文書・バージョン・approval baseline が整合し、build・全テスト・package validation が緑。外部公開やタグ操作を行わず、公開候補コミットとして引き渡されている。
- **依存関係**: 上記チェックリストすべて。
- **工数**: 小 (0.5 日)。

---

## Wave 2 — 安全機構の default-on 化 (凍結前必須)

### REF-16: 非同期トランザクション API の撤回

- **目的**: commit `c2ee592` で導入した API 境界の非同期 (`BeginTransactionAsync` / `CommitAsync` / `DisposeAsync` / traversal・Match の Async 系 terminal) を公開面から撤回し、非同期書き込みの入口を REF-10 の `ExecuteWriteAsync` に一本化する。`CommitAsync` は単体なら文脈所有権の全面改修なしに維持できる可能性があるが、v1 では API 一貫性・TCB 最小化を優先して撤回する。根拠は「依存グラフと推奨実行順序」の 2026-07-02 改訂項を参照。**main 未反映の今だけ撤回が無料** (公開後は MAJOR)。
- **対象**: 下記「撤回対象の実体 (転記)」を正とする。**本計画書の承認まで、ソリューション実体 (src / tests / samples / slnx / docs) には一切触れない** — 本節は `c2ee592` の変更内容を設計書へ転記した as-is 記録であり、実体の変更は本タスクの実行時に初めて行う。
- **撤回対象の実体 (転記: 2026-07-02 時点、`c2ee592` の diff より採録)**:
  - **公開 API (approved.txt 差分 32 行より)**:
    | 型 | 撤回するメンバ |
    |---|---|
    | `GraphDatabase` | `IAsyncDisposable` 実装 / `DisposeAsync()` / `BeginTransactionAsync(IsolationLevel, CancellationToken)` / `BeginReadOnlyTransactionAsync(CancellationToken)` |
    | `IGraphTransaction` | `IAsyncDisposable` 継承 / `CommitAsync(CancellationToken)` |
    | `GraphTraversal<T>` | `AsAsyncEnumerable` / `CountAsync` / `NextAsync` / `ToListAsync` / `TryNextAsync` |
    | `TypedGraphTraversal<T>` | `AsAsyncEnumerable` / `CountAsync` / `FirstAsync` / `ToListAsync` / `ToListWithIdsAsync` |
    | `Match.ReturnClause<TResult>` | `AsAsyncEnumerable` / `CountAsync` / `FirstAsync` / `ToListAsync` |
  - **撤回対象から除外するもの (同コミット同梱だが async と無関係 / 別タスク素材)**:
    - `IGraphTransaction.ListVectorIndexes()` / `GraphTransaction.ListVectorIndexes()` — `c2ee592` に同梱された無関係の公開 API 追加。**残す** (REF-3 インベントリの通常対象として扱う)。
    - `GraphDatabaseOptions.EnforceExclusiveWriter` と `SemaphoreSlim` 排他機構 — **残す** (REF-8 の素材。public フラグの除去は REF-8 が実施)。
  - **内部実装 (公開面の撤回に伴い削除/縮約)**: `Transaction.CommitAsync` 実装 ([src/Quiver/Transactions/Transaction.cs](../src/Quiver/Transactions/Transaction.cs) +86 行)、`ITransaction` (+3) / `GraphTransaction` (+7) の配線、`IWriteAheadLog.FlushToAsync` の interface 配線と `WriteAheadLog.FlushToAsync` の公開相当経路、[src/Quiver/GraphDatabase.cs](../src/Quiver/GraphDatabase.cs) の async begin 配線 (+87 のうち semaphore 排他部分は温存)。**既存の同期 `FlushTo` が利用する内部 `Channel<FlushRequest>` / `RunFlushLoopAsync` / group commit worker は c2ee592 より前から存在するため温存する。**
    - **WAL 精密化 (2026-07-02 追記、c2ee592 diff 精査より)**: `WriteAheadLog` / `IWriteAheadLog` は **internal 型**であり、この項目は approved.txt に現れない (= 公開面撤回の必須要件ではない)。加えて c2ee592 は同期 `FlushTo` の本体を `FlushToAsync` + `FlushToCoreAsync` へ抽出し、`FlushTo` をその blocking ラッパ (`.GetAwaiter().GetResult()`) に書き換えている。したがって `WriteAheadLog.FlushToAsync` を素朴に削除すると**同期 `FlushTo` が壊れる**。扱いは次のいずれかとし、どちらでも完了条件を満たす: (i) 内部形はそのまま残す (interface メンバ `IWriteAheadLog.FlushToAsync` だけ落とすかも任意 — internal のため公開契約に影響しない)、(ii) `FlushTo` を c2ee592 以前の直接 blocking 実装に戻して async 抽出を畳む。**どちらの場合も flush worker 経路 (`FlushToCoreAsync` が依存する channel/loop) を削らない**こと。手順 6 の crash contract / group commit 検証が本項の安全網。
  - **付随物**: `samples/Quiver.Samples.AsyncApi/` (Program.cs + csproj) と `Quiver.slnx` の該当 1 エントリ、`tests/Quiver.Tests/AsyncApiTests.cs` (111 行)、`c2ee592` が更新した docs 13 ファイル (README / docs/api/concepts/transaction.md / docs/api/getting-started.md / docs/api/index.md / architecture-diagrams.md / architecture.md / cookbook.md / docs/design/development.md / glossary.md / operations/01_quickstart.md / operations/05_known_limits.md / docs/spec/08_known_limits.md / plans/publish-branch-workflow.md)。
- **実装手順**:
  1. `git diff c2ee592^ c2ee592` と上記棚卸しを参照し、対象メンバ・配線・付随物を**外科的に除去**する。`git revert c2ee592` を作業起点にしない (残す機能と後続コミットを巻き戻す危険があるため)。
  2. **残すもの**: `EnforceExclusiveWriter` の `SemaphoreSlim` 排他機構 (REF-8 の素材。同期待機に縮約)。`AsyncApiTests` のうち排他検証として意味が残るケースは同期版に書き換えて移設。
  3. 08_known_limits.md §threading / §commit-durability / §write-serialization を同期契約 (スレッドアフィン) に戻す。§write-serialization は「書き込みゲート / 専用ライタスレッド (REF-10 で製品化予定)」の 2 パターン構成へ戻す。Async terminal が行っていた反復中 cancellation は、同期 cursor を呼び出し側で列挙し各反復で `CancellationToken.ThrowIfCancellationRequested()` を呼ぶ公式レシピとして cookbook に残す。REF-16 では代替 public overload を追加しない。
  4. [async-transaction-context-safety.md](async-transaction-context-safety.md) に「**停止 (撤回により対象消滅)**」と根拠を追記してアーカイブ (FTS-9 と同じ流儀)。発見した欠陥 (`await BeginTransactionAsync` 後の継続スレッドで `[ThreadStatic]` 文脈が失われ WAL 記録が silent に欠落) の記述は、再導入検討時の一次資料として**削除しない**。
  5. approved.txt 再生成 → 差分が Async 系メンバの消滅のみであることを目視確認 (G-3)。`ListVectorIndexes` と REF-8 用の排他実装が残ることを明示的に確認する。
  6. 同期 `Commit` の crash contract、WAL flush batch/group commit、in-memory rollback、同期 cursor cancellation レシピを検証し、公開面撤回が内部 durability pipeline を壊していないことを確認する。
- **判断ポイント (遵守)**:
  - 撤回は**公開面のみ**。内部の排他機構・テスト資産は REF-8 が再利用するため温存する。
  - 「一部だけ残す」(例: `CommitAsync` のみ存続) を**しない**。`CommitAsync` 単体は技術的に分離可能でも、async 契約を二系統にしないという v1 の公開面判断を優先する。残したくなったら実装せず報告。
  - 公開 async API の撤回と、WAL 内部の非同期 flush worker を混同しない。同期 `FlushTo` を支える既存 worker は削除・同期化しない。
  - 同期 query のキャンセル代替として新規 overload を本タスクで増やさない。cursor レシピで不足する実需が確認された場合だけ REF-3 の public API 監査を経て別タスク化する。
  - 再導入する場合は 1.0 後の MAJOR 判断であることを docs/api-stability.md に 1 行残す。
- **完了条件**: 公開面から tx/query 上の Async 系が消え、全テスト緑。§threading が同期契約に戻り、async-transaction-context-safety.md が停止注記済みで、approved.txt 差分が撤回分のみ。`ListVectorIndexes`・排他機構・内部 WAL flush worker が維持され、同期 cursor cancellation レシピが文書化されている。
- **依存関係**: なし。**REF-8 / REF-9 / REF-7 より先に実施** (両タスクの前提を単純化するため)。
- **工数**: 小〜中 (1 日)。

### REF-8: 並行 writer 排他の既定有効化 (GA-1 改訂版)

- **目的**: GA-1 原案は opt-in フラグだったが、**opt-in の安全フラグは必要な人ほど付けない**。誤用時に「WAL ロギングを暗黙にスキップしうる」現状 (08_known_limits.md §one-writer) は黙って壊れる系のフットガンであり、既定で大声で失敗する側に倒す。
- **現状 (2026-07-02 時点、REF-16 後)**: `GraphDatabaseOptions.EnforceExclusiveWriter` は commit `c2ee592` 由来の **opt-in 実装** (有効時、`BeginTransaction()` は競合を即時例外で拒否)。REF-16 で非同期 Begin 経路は消滅済み。本タスクはこれを「既定 ON (フラグ除去) + timeout 付き待機」へ改訂する。
- **対象**: `GraphDatabaseOptions` ([src/Quiver/GraphDatabase.cs](../src/Quiver/GraphDatabase.cs) 内)、`TransactionManager`、docs/spec/08_known_limits.md §one-writer / §write-serialization。
- **実装手順**:
  1. `BeginTransaction()` 時に active writer が存在する場合、**既定で `WriterLockTimeout` (既定 10 秒) までブロックして待機**し、タイムアウトで `TransactionException` (transient、既存リトライ分類に整合)。SQLite の busy_timeout 相当で、既存の「書き込みゲート」パターンをエンジン内に取り込む形。既存の `SemaphoreSlim` 排他機構 (REF-16 で温存) を同期待機に流用する。
  2. `WriterLockTimeout = TimeSpan.Zero` 指定で即時 throw (fail-fast を好むユーザ向け)。
  3. 現行の `EnforceExclusiveWriter` は public API から除去し、排他を常時有効にする。既存テストに排他を迂回する必要があれば internal のテスト専用フックに限定する。
  4. 待機成功 / タイムアウト、commit/rollback/dispose 後の解放、読み取り専用 tx との非干渉をテストする。
- **判断ポイント (遵守)**:
  - **既定はブロック + timeout** (throw ではない)。理由: 既存の推奨パターン (SemaphoreSlim ゲート) と等価な挙動を既定にすることで、正しく使っているアプリの動作が変わらない。即時 throw を既定にすると、たまたま動いていた並行書き込みアプリが全滅する。
  - 待機は **writer 間のみ**。`BeginReadOnlyTransaction()` は一切ブロックしない/されない (現行契約維持)。
  - 待機による**新規デッドロック経路を作らない**: 同一スレッドが writer tx 保持中に再度 `BeginTransaction()` した場合は待機せず即時 `InvalidOperationException` (自己デッドロック検出)。
  - **公開 opt-out を設けない。** 未検証の並行 writer を許すフラグは「サポート」に見え、再び静かな破損経路を公開する。native concurrent writer は別設計・別タスクで実装する。
  - lock-free 読み取り経路・commit ホットパスにコストを足さない (待機判定は Begin 時のみ)。
- **完了条件**: 並行 writer テスト (待機成功 / タイムアウト / 自己デッドロック) 緑。public baseline から `EnforceExclusiveWriter` が消え、未検証並行 writer の公開迂回路がない。既存 stress テストが既定値変更で壊れず、08_known_limits.md が「エンジンが常時直列化する」記述に改訂されている。
- **依存関係**: REF-16 完了後 (非同期 Begin 経路の消滅を前提に排他を単純化するため)。ga-readiness.md の GA-1 を supersede と追記すること。
- **工数**: 小〜中 (1〜2 日)。

### REF-9: トランザクションのスレッドアフィニティ違反検出

> **前提改訂 (2026-07-02、本版)**: 非同期 tx API の撤回 (REF-16) により契約は「tx は 1 スレッドの同期スコープ」に
> 戻るため、本タスクも原案 = **スレッド ID 固定 + 越境操作で throw** に復帰する。`[ThreadStatic]` 文脈と契約が
> 再び整合するので、tx 所有文脈化・並行使用ガード・カーソル生存期間の監査・`ConcurrentUse` 例外分類
> (develop 版 REF-9 の内容) はすべて不要になる。

- **目的**: `[ThreadStatic]` MVCC 文脈のため、生きた tx を別スレッドで使うと WAL スキップ等の静かな破損に至りうる。文書 (08_known_limits.md §threading) だけでなく**ランタイムで検出して throw** する。
- **対象**: `Transaction` ([src/Quiver/Transactions/Transaction.cs](../src/Quiver/Transactions/Transaction.cs))、`GraphTransaction`、Tx store wrapper、tx から返す列挙子・カーソル、tx の public エントリポイント群。
- **実装手順**:
  1. `Begin` 時に所有 thread ID を捕捉する内部 `TransactionThreadGuard` を作り、`Transaction` と `GraphTransaction`、tx から生成されるカーソルへ同一インスタンスを渡す。
  2. tx の全 public 操作 (read/write/commit/rollback/dispose、カーソル `MoveNext` 含む) の入口で比較する。不一致なら状態を変更する前に `InvalidOperationException` (メッセージに「tx はスレッドアフィン」+ known_limits へのリンク文言)。
  3. **常時 ON** (Debug 限定にしない)。1 int 比較は G-5 の定数コスト要件を満たす — ただしトラバーサル反復 (カーソル MoveNext) 経路は `--basic-perf` before/after で退行なし (±2% 以内) を実測確認。
  4. 別スレッドからの `Dispose` も例外にせず throw する。所有スレッドの `WalPageContext` / `MvccContext` を別スレッドから安全に rollback できないため、暗黙 cleanup は行わない。
  5. 直接操作、列挙途中、commit/rollback/dispose、`Task.Run` 越しの違反テストと perf 記録を追加する。
- **判断ポイント (遵守)**:
  - **REF-16 完了が前提。** 非同期 tx API が公開面に残っている間は本ガードを入れない (`await BeginTransactionAsync` の継続スレッドで即 throw し、出荷 API と矛盾するため)。
  - 検出できないケースを偽装しない: 同一スレッドに戻ってくる `await` は検出不能。**検出は補助であり契約の代替ではない**ことを 08_known_limits.md に明記。
  - opt-out フラグは**設けない** (これを切る正当な理由がない)。
  - **別スレッド Dispose を「安全側 rollback」と扱わない。** 現行 rollback は thread-static の before-image / logical undo を参照する。これを可能にするには rollback state の tx 所有化という別設計が必要であり、本タスクへ混ぜない。
  - 例外は `InvalidOperationException` のまま (プログラミングエラー)。`TransactionException` にしない — §retry の推奨パターンが型で一括 catch して再試行するため、リトライ対象に見せない。
  - MoveNext 経路の実測で退行が出たら、カーソルのみチェック頻度を下げる案 (取得時のみ検査) を**実装せず報告**。
- **完了条件**: 違反検出テスト (Task.Run 越し操作・カーソル・Dispose で状態変更前に throw / 同一スレッドは無影響) 緑。全 tx 由来カーソルが guard を保持し、perf 実測が記録されている。
- **依存関係**: REF-16 完了後。REF-8 と同一セッション可。
- **工数**: 小 (1 日)。

---

## Wave 3 — writer queue の製品化 (利用体験)

### REF-10: `ExecuteWrite` / `ExecuteWriteAsync` — 専用ライタスレッドファサード

- **目的**: 08_known_limits.md §write-serialization が推奨する「専用ライタスレッド + Channel」パターンを全ユーザに手書きさせず、**製品 API として本体に同梱**する。MVCC を触らずに async アプリ (ASP.NET / デスクトップ) からの自然な利用感を提供する、費用対効果最大の洗練。
- **位置づけ (2026-07-02 改訂)**: 非同期 tx API の撤回 (REF-16) 後、**本 API がライブラリ唯一の非同期書き込み入口**となる。begin→work→commit を**単一専用スレッドの同期スコープに閉じ込める**ため、tx 内 await・スレッド移動の問題が構造的に発生せず、排他・冪等リトライ・キャンセル・shutdown を一箇所に集約する。撤回で失われる「呼び出しスレッドを塞がない書き込み」の実利は本 API が代替する。このため凍結前**必須**へ格上げ (REF-7 前提条件)。読み取り/query engine は同期実行のままとし、非同期 I/O を提供しているように見える擬似 async API は設けない。
- **対象**: 新規 `src/Quiver/Api/WriterQueue.cs` (名称は実装時に確定可、公開面は `GraphDatabase` の拡張として)、docs/operations、cookbook。
- **API 形 (確定イメージ)**:
  ```csharp
  // GraphDatabase 上のインスタンスメソッド。初回呼び出しで専用ライタスレッド + Channel を遅延起動
  public Task ExecuteWriteAsync(Action<IGraphTransaction> work, CancellationToken ct = default);
  public Task<T> ExecuteWriteAsync<T>(Func<IGraphTransaction, T> work, CancellationToken ct = default);
  public void ExecuteWrite(Action<IGraphTransaction> work, CancellationToken ct = default);
  ```
- **実装手順**:
  1. `GraphDatabaseOptions.WriterQueueCapacity` (既定 1024、1 以上) を設け、`BoundedChannelFullMode.Wait` の bounded `Channel<WriteJob>` を遅延生成する。満杯時は enqueue を待機させ、無制限な delegate / `TaskCompletionSource` 蓄積を禁止する。
  2. 単一専用スレッドが channel を drain し、各ジョブを **begin → work → commit を同一スレッド同期スコープで**実行 (スレッドアフィニティ契約に構造的に適合)。結果/例外は `TaskCompletionSource` で呼び出し元へ。
  3. **リトライ内蔵**: `DeadlockException` / `SerializabilityException` と、writer/lock timeout を表す **transient な `TransactionException` だけ**を既定 5 回・有界バックオフでトランザクション全体リトライする。状態不正・savepoint 不正等の deterministic な `TransactionException` は即時返す。判別に文字列比較を使わず、internal reason code を例外生成箇所で設定する。work デリゲートは**リトライで複数回呼ばれうる**ことを XML doc に明記 (冪等要件)。
  4. `CancellationToken`: **enqueue / キュー待機中のみキャンセル可**。実行開始後の tx 中断はしない (中断=rollback の複雑さを v1 で持ち込まない)。同期 `ExecuteWrite` も同じ token で backpressure 待機を中断できる。
  5. Dispose 時: 新規受付を停止し、`GraphDatabaseOptions.WriterQueueDrainTimeout` (既定 30 秒) までは queued job を drain する。timeout 後は未開始 job を `ObjectDisposedException` で完了し、実行中 job だけは commit/rollback が終わるまで待ってから backend を閉じる。実行中 delegate を強制中断しない。
- **判断ポイント (遵守)**:
  - **配置は Quiver 本体** (Hosting ではない)。書き込み直列化はコアの利用体験であり、Hosting 未使用のユーザにも必要。Hosting は REF-11 で DI 登録のみ担う。
  - **自動バッチングを実装しない** (複数ジョブの 1 tx への合成は commit 失敗時の帰属が壊れる)。また単一 writer queue は各 commit 完了まで次ジョブへ進まないため、複数 flush request を束ねる group commit 効果も性能根拠にしない。
  - **unbounded channel を使わない。** backpressure は public 契約であり、capacity・満杯時待機・キャンセル・dispose 競合をテストする。
  - **トランザクション自体の async 化に踏み込まない**。work デリゲートは同期 (`Action`/`Func<T>`)。`Func<IGraphTransaction, Task>` オーバーロードは**提供しない** (tx 内 await の温床になるため。要望があれば報告)。
  - `TransactionException` を型だけで一律リトライしない。リトライ対象は競合・timeout に限定し、ユーザ work が投げた例外はそのまま一度で返す。
  - work 内での `ExecuteWrite*` 再入 (ライタスレッドから自呼び出し) は即時 `InvalidOperationException` (自己デッドロック防止)。
  - REF-8 との関係: キュー経由の書き込みは構造的に直列なので writer lock と競合しない。両者併存の挙動 (キュー外の直接 `BeginTransaction` と混在) をテストで固定。
  - drain timeout は queued job の打ち切り境界であり、実行中 tx の強制 abort 境界ではない。backend を active writer より先に破棄しない。
- **完了条件**: 正常系 / bounded capacity / backpressure / リトライ / キャンセル / 再入 / Dispose drain・timeout / 直接 tx との混在テスト緑。直接の直列書き込みを baseline として queue の追加 overhead・待ち時間・throughput を実測し、group commit 改善を主張しない。cookbook「書き込みの直列化」節を本 API 前提に書き換える。
- **セッション分割**: (A) queue lifecycle・同期/非同期 API、(B) retry/cancellation/re-entry/dispose、(C) 統合テスト・性能・文書。各単位を独立コミットする。
- **依存関係**: REF-8 完了後 (writer lock との相互作用を固定するため)。
- **工数**: 中 (合計 2〜3 日)。

### REF-11: Hosting 統合 + 運用ドキュメント改訂

- **目的**: REF-10 を DI / Generic Host の世界に接続し、既定の使い方を「洗練された形」に一本化する。
- **対象**: [src/Quiver.Hosting/QuiverServiceCollectionExtensions.cs](../src/Quiver.Hosting/QuiverServiceCollectionExtensions.cs)、docs/operations/01_quickstart.md、docs/cookbook.md、README。
- **実装手順**: `AddQuiver(...)` が返す `GraphDatabase` の lifetime と `ExecuteWriteAsync` の利用例を Hosting サンプルに追加。`IHostApplicationLifetime` 連動の graceful shutdown (drain) を確認。README のクイックスタートに async 利用例を 1 つ追加。
- **判断ポイント (遵守)**: 新規 public 型を Hosting に増やさない (既存拡張メソッドの範囲で完結)。BackgroundService でのバッチ取込例は cookbook に置く。
- **完了条件**: Hosting 経由の統合テスト 1 本 + ドキュメント更新。
- **依存関係**: REF-10。
- **工数**: 小 (0.5〜1 日)。

---

## Wave 4 — Import/Export の前倒し (migration story の実体化)

### REF-12: Export (JSONL ダンプ)

- **目的**: 「フォーマット自動マイグレーションなし・ソースから再構築」戦略 (08_known_limits.md §no-migration) が成立するのは**公式 dump/load があるとき**。Export を先に作る (Import より先。旧版 DB を読めるのは旧版バイナリだけであり、dump 側が migration の鍵)。
- **対象**: 新規 `src/Quiver/Api/GraphExporter.cs` (または `Quiver.Api` 配下の静的エントリ)、docs/operations に「バックアップとフォーマット移行」節。
- **実装手順**:
  1. 下記 JSONL 仕様を docs/operations に先に固定し、型タグとレコード順序の golden test を作る。
  2. read-only snapshot 上の store 走査を streaming JSONL writer へ接続し、全件 materialize しないことをテストする。
  3. 全プロパティ型・vector・索引定義・決定的出力順・gzip option を検証し、運用手順を更新する。
- **フォーマット仕様 (このタスクで文書として確定させる)**:
  1. JSONL (1 行 = 1 レコード)。先頭にヘッダ行: `{"quiver_dump":1, "format":<FormatVersion>, "created":...}` — **dump フォーマット自体のバージョン (`quiver_dump`) を on-disk FormatVersion と独立に持つ**。
  2. レコード種: `label` / `propkey` (トークン定義) → `node` → `rel` → `index` (定義のみ: B+Tree/FTS/vector の spec)。ID は `NodeId.Value` (generation+sequence パック値) をそのまま保持し、リレーションはそれを参照。
  3. **プロパティ型の忠実性**: 値は型タグ付き (`{"t":"i64","v":...}` 等、FT-35 の canonical encoding に対応)。`float[]` (vector payload) は JSON 数値配列 (既定) — サイズより可読性・可搬性を優先。
  4. 出力順は決定的 (store 走査順)。`BeginReadOnlyTransaction()` の snapshot 内で全走査 (一貫性保証・writer を止めない)。streaming 書き出し (全 materialize しない)。
- **判断ポイント (遵守)**:
  - **索引の中身は dump しない** (postings / HNSW グラフ / B+Tree は Import 時に再構築)。dump するのは索引**定義**のみ。
  - 独自バイナリフォーマットにしない。JSONL は圧縮 (gzip) をオプションで — 既定は素の JSONL。
  - snapshot が長時間 WAL をピン留めする問題 (§short-transactions) を文書化し、大規模 DB では `CreateSnapshot()` ファイルからの export を推奨手順にする。
- **完了条件**: export 実行 + 出力仕様書 (docs/operations)。件数・型網羅のテスト。
- **依存関係**: なし (REF-13 の前提)。
- **工数**: 中 (2 日)。

### REF-13: Import (BulkLoader 経由) + round-trip 検証

- **目的**: REF-12 の dump を新規 DB に取り込む。これで「no-migration 制限」が「文書化された移行ワークフロー」に変わる。
- **対象**: 新規 `GraphImporter`、[src/Quiver/Stores/BulkLoader.cs](../src/Quiver/Stores/BulkLoader.cs) 経路、docs/operations の移行手順。
- **実装手順**:
  1. ヘッダ検証 (`quiver_dump` バージョン) → トークン → ノード → リレーション → 索引定義の順に `BeginBulkLoad(buildAdjacencyIndex: true)` で投入。索引 (B+Tree / FTS / HNSW) は定義から再構築。
  2. **ID 保存**: dump の `NodeId.Value` を維持して取り込めるか BulkLoader の能力を確認。維持できない場合は旧→新 ID の remap 表を内部で保持しリレーション解決 (どちらを採ったか報告)。
  3. **round-trip テスト (本タスクの核)**: 代表 DB (全プロパティ型 + vector + FTS + 索引 + 削除跡あり) を export → import → 再 export し、**2 つの dump がバイト同一** (または正規化比較で同値) であることを CI に固定。RagSandbox 相当のデータでも検証。
- **判断ポイント (遵守)**:
  - Import は**空 DB へのみ** (既存 DB へのマージは非目標。要望があれば別タスク)。
  - 部分失敗時は中途半端な DB を残さない (一時ファイルに構築 → 成功時 rename)。
  - HNSW 再構築は決定的でない (グラフトポロジ) — round-trip 比較は**索引内容を除外し、検索結果の recall で代替検証**する。
- **完了条件**: round-trip CI テスト緑。docs/operations「フォーマット移行手順」が export→import ベースで完結。08_known_limits.md §no-migration の緩和策をこの手順に差し替え。
- **セッション分割**: (A) parser・空DB検証・token/node/relationship import、(B) 索引再構築・原子的置換・round-trip/recall・文書。各単位を独立コミットする。
- **依存関係**: REF-12。
- **工数**: 中 (合計 2〜3 日)。

---

## Wave 5 — 観測性 (追加的、凍結後でも可)

### REF-14: 運用契約のランタイム診断化 (メトリクス + 警告)

- **目的**: known_limits の運用ルールを「読まなくても気づける」ものにする。文書化済みのフットガン (長寿命 tx の WAL ピン留め、WAL 成長、HNSW tombstone) を EventSource イベント + `System.Diagnostics.Metrics` で既定露出する。
- **対象**: コア各所 (Checkpointer / TransactionManager / HnswIndex / WAL)、[src/Quiver.OpenTelemetry/QuiverInstrumentation.cs](../src/Quiver.OpenTelemetry/QuiverInstrumentation.cs)。
- **実装手順**:
  1. Meter 名・instrument 名・単位・警告しきい値を文書とテストで固定する。
  2. 低頻度イベントから gauge/counter を更新し、長寿命 tx と WAL 警告にレート制限を入れる。
  3. commit histogram を追加して allocation と性能を実測し、Quiver.OpenTelemetry の登録 helper と運用文書を更新する。
- **仕様 (計装項目)**:
  | 項目 | 種別 | しきい値/備考 |
  |---|---|---|
  | 長寿命トランザクション | EventSource Warning (1 回/tx) | 既定 30 秒超 or WAL ピン留め 64MB 超。Hosting は ILogger へ転送。オプションで調整可 |
  | WAL サイズ / 切り詰め契機 | ObservableGauge + Counter | checkpoint 完了時に更新 (ホットパス外) |
  | group commit 効率 | Histogram (batch サイズ) | commit 経路 — **0-alloc 必須、実測で退行 ±2% 以内** |
  | HNSW tombstone 比率 / rebuild 発火 | ObservableGauge + Counter | 既存 auto-rebuild トリガに接続 |
  | checkpoint 所要 / vacuum 回収量 | Histogram / Counter | |
- **判断ポイント (遵守)**:
  - コアの計装は **`ActivitySource` + `Meter` + `EventSource` のみ** (すべて in-box)。OpenTelemetry / Microsoft.Extensions.Logging への依存をコアに足さない。Quiver.OpenTelemetry は Source / Meter 名を登録し、Quiver.Hosting が EventSource を ILogger へ転送する。
  - **測定はホットパス外で**: gauge は checkpoint / vacuum / rebuild 等の低頻度イベントで更新。トラバーサル反復・per-op 経路に計装を入れない。commit 経路の histogram のみ例外とし、実測 gate (G-5) を通す。
  - 警告ログはレート制限 (同一 tx で 1 回、WAL 警告は指数間隔) — ログ洪水を作らない。
- **完了条件**: 各項目のユニットテスト (しきい値発火 / 非発火)。commit 経路 before/after 実測。docs/operations/03_performance_tuning.md にメトリクス一覧表を追加。
- **セッション分割**: (A) tx/WAL/checkpoint、(B) HNSW/vacuum/group commit、(C) OpenTelemetry helper・性能・文書。各単位を独立コミットする。
- **依存関係**: なし。
- **工数**: 中 (合計 2〜3 日)。

### REF-15: BM25 コーパス統計の checkpoint 時 auto-refresh

- **目的**: 08_known_limits.md §bm25-stats の「将来方針」を実装。snapshot 統計の陳腐化を checkpoint 契機で自動解消する (安価な体感品質向上)。
- **対象**: Checkpointer / AdaptiveCheckpointController、`GraphStats` キャッシュ保持点。
- **実装手順**:
  1. checkpoint End 後に db レベルの既定 `GraphStats` キャッシュを再収集して atomic swap する。ユーザが明示的に渡した snapshot は影響を受けない。
  2. `GraphDatabaseOptions.RefreshStatsOnCheckpoint = true` (既定 ON) を追加し、無効時と更新時のテストを作る。
  3. score/WAND correctness と checkpoint 時間を before/after 測定し、spec を更新する。
- **判断ポイント (遵守)**:
  - 再収集は checkpoint スレッド上で行い、**検索経路にロックを持ち込まない** (差し替えは参照の atomic swap)。
  - 再収集コストが checkpoint 時間を有意に延ばす場合 (実測で +10% 超) は近似更新 (N と avgdl のみ) に縮退し報告。
  - WAND の「取りこぼしなし」性質が統計更新のタイミングに依存しないことを既存テストで再確認。
- **完了条件**: 陳腐化解消のテスト (書き込み → checkpoint → スコアが新統計反映)。checkpoint 時間の before/after 実測。08_known_limits.md §bm25-stats の「将来方針」を「実装済み (既定 ON)」に改訂。
- **依存関係**: なし。REF-14 と同一セッション可。
- **工数**: 小 (1 日)。

---

## スコープ外 (本トラックでは扱わない — 参照用の判断記録)

| 項目 | 判断 |
|---|---|
| マルチバリュープロパティ ([multivalue-property-design-notes.md](multivalue-property-design-notes.md)) | **凍結継続。** MVCC チェーンに可視性と多重度の 2 概念を同居させる仕様化コストが高い。グラフ的にはタグ=ノード+エッジが本来の語彙で、`metadataJson` 代替もある。実需が出るまで着手しない。 |
| tx 上の非同期 API (あらゆる形) | **撤回 (REF-16)。** `BeginTransactionAsync` / `DisposeAsync` の安全化は write/recovery 最深部の恒久改修 ([async-transaction-context-safety.md](async-transaction-context-safety.md)、停止決定) を要し、安全化後も「await またぎの tx 保持」という WAL ピン留めアンチパターンを誘発する。`CommitAsync` 単体は技術的に分離可能だが、v1 の API 一貫性・TCB 最小化のため同時に撤回する。非同期書き込みは REF-10 に一本化し、読み取り/query は同期契約を明示する。反復中 cancellation は同期 cursor レシピで提供する。再導入は 1.0 後の MAJOR 判断。 |
| Quiver Studio | 本トラック対象外。コアの semver / API 安定性ポリシーの適用外であることを docs 側で明文化する (REF-7 の api-stability 改訂に 1 行含める)。GA を Studio の完成度にブロックさせない。 |
| Quiver.Mcp | 本トラック対象外 (差別化として継続支持)。REF-4 完了後に `traverse` の FTS 起点を `FtsQuery` 組み立てに移行するタスクを別途起票。 |
| ネイティブ並行 writer / きめ細かい索引ロック | 将来課題のまま (08_known_limits.md 記載どおり)。REF-8 は排他の強制であり並行化ではない。 |
| net8.0 マルチターゲット / 英語ドキュメント | 個人利用目的の現状では保留。MCP 等で外部ユーザを取りに行く判断をした時点で英語 README を最優先タスクとして起票。 |

## 完了の定義 (トラック全体)

- REF-16/2/3/6 により approved.txt が「意図して公開した API のみ」になり、宣言数の縮小が記録されている。
- 誤用 3 態 (並行 writer / スレッド越し tx / 長寿命 tx) がすべて「黙って壊れる」から「throw または警告」に変わっている。
- 非同期の公開面が `ExecuteWriteAsync` に一本化され、cookbook / README の推奨書き込みパターンになっている。
- export → import の round-trip が CI で緑であり、no-migration 制限の緩和策が公式手順を指している。
- REF-7 のチェックリストが全て埋まった状態で 1.0.0 が宣言できる (宣言自体はユーザ判断)。
