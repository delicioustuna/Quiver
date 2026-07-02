# ライブラリ洗練トラック (REF) — サブエージェント委託用 実装計画書

> 起票日: 2026-07-02。起点: develop 計画群 + docs/spec + 公開 API 表面 (approved.txt 843 宣言) の外部レビュー。
> 本書は **1 タスク = 1 サブエージェントセッション** で委託できる粒度に分割した実装計画と、
> 各タスクで遵守すべき判断ポイントを定める。判断ポイントの「既定」から逸脱したくなった場合は
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
                      REF-3 ──┤
                      REF-4 ── REF-5 ──┤
                      (typed-write-sinks Phase 1 = 既存計画書) ──┤
Wave 2 (安全既定):     REF-8, REF-9 ──────────────────────────┤
                                                              ▼
                                                    REF-7 (1.0 凍結ゲート)
Wave 3 (利用体験):     REF-10 ── REF-11      (凍結前推奨、REF-10 は public API 追加のため)
Wave 4 (運用・前倒し): REF-12 ── REF-13      (Export→Import。凍結前推奨)
Wave 5 (観測性):       REF-14, REF-15       (追加的 = 凍結後でも可)
```

- **REF-7 (凍結) より前に必ず完了させるもの**: REF-2, REF-3, REF-4, REF-8, REF-9
  (public 表面の縮小・既定挙動の変更は 0.x でしか無料でできない)。REF-5, REF-10, REF-12/13 も凍結前完了が望ましい。
- **既存計画との関係**: [ga-readiness.md](ga-readiness.md) の GA-1 は REF-8 に、GA-2〜4 は REF-4/REF-5 に**置換 (supersede)** される。
  GA-5 (ITokenFilter)・GA-6〜11 (テスト拡充) は本書と独立で、いつでも並列実行可。
  [typed-write-sinks-and-query-patterns.md](typed-write-sinks-and-query-patterns.md) は既存計画のまま有効 (public API を増やすため凍結前に Phase 1 を完了させる)。
  [pw19-numeric-range-index-pushdown.md](pw19-numeric-range-index-pushdown.md) は追加的変更のため凍結後でも可だが、GA 前実施を推奨 (性能崖の解消)。

---

## Wave 0 — 衛生

### REF-1: 計画書記載と実装実態の同期 (recovery clobber ほか)

- **目的**: 「握りつぶさない」原則の裏面として、**直った問題が「未修正」のまま残る**のも監査の信頼性を損なう。計画書群の stale 記載を実態に同期する。
- **背景**: [typed-write-sinks-and-query-patterns.md](typed-write-sinks-and-query-patterns.md):23 と [v1-consolidation.md](v1-consolidation.md):57,169 に「v1 監査 #1 recovery Pass-3 loser-undo clobber = HIGH **未修正**」の記載が残るが、[src/Quiver/Transactions/RecoveryManager.cs](../src/Quiver/Transactions/RecoveryManager.cs) には presume-committed 2 相 recovery による clobber 対策の実装とコメントが既に存在する。
- **手順**:
  1. RecoveryManager の該当経路 (第 2b パス presume-committed / 第 3 パス論理 undo) を精読し、監査 #1 のシナリオ (loser Delete の undo = UpsertRaw 無条件上書きが committed 値を clobber) が**現行コードで再現不能であることをテストで確認** (既存 crash contract テストに該当ケースがあるか探し、無ければ 1 本追加)。
  2. 確認が取れたら計画書 2 箇所の「未修正」記載を「修正済み (commit 参照)」へ更新。確認が取れなければ **08_known_limits.md に「既知・未修正・HIGH」として収録** (v1-consolidation の元方針どおり)。
  3. ついでに他 plans/*.md の「状態」欄で完了済みタスクの stale 記載がないか棚卸しし、機械的に更新。
- **判断ポイント (遵守)**:
  - 「コメントに対策が書いてある」ことは修正済みの証拠にしない。**テストで再現不能を示せた場合のみ**「修正済み」と記載する。どちらとも言えない場合は「要調査」として報告。
- **完了条件**: clobber シナリオの検証テストが存在し緑。plans/ に実態と矛盾する記載が残らない。
- **工数**: 小 (0.5〜1 日)。依存: なし。

---

## Wave 1 — API 表面の確定 (1.0 凍結の前提条件)

### REF-2: backend 抽象の畳み込み (binary 直結)

- **目的**: SQLite 撤去 (v1-consolidation §C 完了) 後、`IGraphStorageBackend` は単一実装の抽象 = dead generality。ゼロ依存・TCB 最小化 thesis に沿い、0.x のうちに public 表面から畳む。v1-consolidation §C.2 の「代替案 (clean v1)」を正式採用する。
- **対象**: [src/Quiver/Backend/](../src/Quiver/Backend/) — `IGraphStorageBackend.cs` / `IGraphStorageBackendInternal.cs` / `IGraphStorageBackendFactory.cs` / `BinaryGraphStorageBackendFactory.cs` / `BackendKind.cs` / `BulkLoadCapabilities.cs`、`GraphDatabase.cs` の配線、`tests/Quiver.Backend.Tests/` の契約テスト、approved.txt。
- **手順**:
  1. 現状の public 露出を棚卸し (approved.txt を grep)。`BackendKind` / factory / 抽象が公開面のどこから到達可能か (GraphDatabaseOptions 等) を列挙。
  2. **internal 化** (削除ではない): 型は残し `public` → `internal` に降格。`BinaryGraphStorageBackend` 直結にできる箇所は直結。`BackendKind` が enum 1 値なら公開面から除去。
  3. backend 契約テストは InternalsVisibleTo で維持 (binary 単独パラメタ化のまま)。
  4. approved.txt 再生成 → 差分レビュー (G-3)。docs/api/concepts/backends.md 等の記述を更新。
- **判断ポイント (遵守)**:
  - **既定 = internal 化。物理削除はしない** (契約テスト資産と将来の backend 復活余地を保持しつつ、公開契約からは外す)。削除まで踏み込みたくなっても行わない。
  - `GraphDatabaseOptions` 等のシグネチャから backend 型が漏れている場合、そのメンバは除去 (0.x なので破壊可)。ただし除去リストは完了報告に明記。
- **完了条件**: approved.txt から backend 抽象系が消え、全テスト緑。README/spec/cookbook に backend 選択の記述が残らない。
- **工数**: 小〜中 (1 日)。依存: なし。REF-3 と同一セッションでも可 (REF-3 の縮小対象から backend 系を除ける)。

### REF-3: public API 監査とダイエット (2 段階: インベントリ → 縮小)

- **目的**: 843 public 宣言は v1 で凍結する契約としては広い。「意図して公開している API」だけを残す。
- **手順 — 第 1 段階 (インベントリ、このタスクの主成果物)**:
  1. approved.txt の全 public 型・メンバを走査し、各項目を分類:
     - **KEEP**: (a) README / cookbook / samples / docs/api が利用例を示す、(b) **Source Generator の生成コードが参照する** (生成コードはユーザアセンブリでコンパイルされるため internal 化不可 — [src/Quiver.SourceGen/](../src/Quiver.SourceGen/) の emitter 群を grep して参照型を機械的に列挙すること)、(c) docs/spec が公開契約として記述する、のいずれか。
     - **DEMOTE 候補**: 上記いずれにも該当しない public (実装都合の公開・テスト都合の公開・歴史的公開)。
     - **UNCLEAR**: 判断がつかないもの。
  2. 分類結果を `plans/ref3-api-inventory.md` として出力 (項目・分類・根拠 1 行)。**ここで一旦停止しユーザ承認を得る。**
- **手順 — 第 2 段階 (承認後、別セッション可)**: 承認された DEMOTE リストを一括 internal 化 → InternalsVisibleTo 整理 → approved.txt 再生成 → 全テスト緑。
- **判断ポイント (遵守)**:
  - **承認前に internal 化を始めない。** 第 1 段階の成果物はレポートのみ。
  - SourceGen 参照型の列挙は**推論ではなく emitter ソースの grep + 生成コードのコンパイル確認**で行う (`tests/*SourceGen*` のスナップショット/コンパイルテストを活用)。
  - `Quiver.Rag` / `Quiver.Hosting` / `Quiver.OpenTelemetry` の public 表面も対象に含める (approval テストが無ければ追加を提案として報告)。
- **完了条件**: 第 1 段階 = インベントリ文書。第 2 段階 = 縮小後 approved.txt + 全テスト緑 + 縮小前後の宣言数を報告。
- **工数**: 第 1 段階 1 日 / 第 2 段階 1 日。依存: REF-2 完了後が効率的。

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
- **手順**:
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
- **工数**: 中 (3〜5 日)。依存: なし (GA-5 ITokenFilter と独立)。

### REF-5: FTS 文字列構文パーサ (糖衣) + fuzz テスト

- **目的**: `"graph AND database NOT vector"` / `"quiv*"` / `"quiver~1"` を **REF-4 の `FtsQuery` に変換する薄いパーサ**として提供 (GA-2〜4 の表層構文を統合実装)。
- **対象**: 新規 `FtsQueryParser.cs`、`g.Search(index, string, k)` の拡張構文 opt-in。
- **手順**:
  1. 再帰降下パーサ (トークン: TERM / AND / OR / NOT / `*` 後置 / `~N` 後置 / 括弧 / 引用符)。出力は `FtsQuery` のみ — パーサは検索実行に一切関与しない。
  2. エスケープ規則を先に文書化してから実装 (引用符内リテラル、`\` エスケープ、予約語 AND/OR/NOT の扱い)。
  3. **fuzz テスト**: FsCheck (既にユニットテストで使用中) で任意文字列を投げ、「`FtsQueryParseException` 以外の例外を投げない」「パース成功時は必ず有効な AST」を性質として固定。
  4. round-trip テスト: `FtsQuery` → 文字列化 → パース → 同一 AST。
- **判断ポイント (遵守)**:
  - **既存の単純クエリ動作を変えない**: 拡張構文の有効化方法は「`SearchQuerySyntax.Simple` (既定・現行互換) / `SearchQuerySyntax.Extended`」のオプション明示とする。既定を Extended にしない (既存ユーザの検索語に `*` や `AND` が含まれても挙動不変であること — RAG では検索語はほぼ外部入力である)。
  - パースエラーは位置情報付きの専用例外。**部分パースや黙殺フォールバックをしない。**
- **完了条件**: 拡張構文の正常系/異常系/fuzz テスト緑。cookbook に構文リファレンス追記。
- **工数**: 中 (2〜3 日)。依存: REF-4。

### REF-7: 1.0 凍結ゲート (チェックリスト実行)

- **目的**: v1-consolidation §E の「唯一の不可逆ステップ」を、前提条件を検証してから実行する。
- **前提条件 (すべて満たすまで着手しない)**:
  - [ ] REF-2 (backend 畳み込み) / REF-3 第 2 段階 (API ダイエット) 完了
  - [ ] REF-4 (FtsQuery) 完了 — 検索エントリのシグネチャは凍結後に変えられない
  - [ ] REF-8 / REF-9 (既定挙動の変更) 完了 — 既定値変更は 0.x でしか無料でできない
  - [ ] typed-write-sinks Phase 1 (`MergeRelationship` + 終端シンク) 完了 (public API 追加のため)
  - [ ] REF-10 (writer queue API) 完了推奨 (追加的だが v1 の「推奨パターン」として文書と一体化するため)
- **手順**: `Directory.Build.props` の `VersionPrefix=1.0.0`、approved.txt を v1 契約として凍結宣言、[docs/api-stability.md](../docs/api-stability.md) の「0.x は無保証」節を改訂、README の pre-release 記述更新、公開後に `PackageValidationBaselineVersion=1.0.0` 設定、release タグ。
- **判断ポイント (遵守)**: 前提条件が 1 つでも未達なら**凍結を実行せず報告に戻す**。「あと少しだから」で凍結しない。
- **工数**: 小 (0.5 日)。依存: 上記すべて。

---

## Wave 2 — 安全機構の default-on 化 (凍結前必須)

### REF-8: 並行 writer 排他の既定有効化 (GA-1 改訂版)

- **目的**: GA-1 原案は opt-in フラグだったが、**opt-in の安全フラグは必要な人ほど付けない**。誤用時に「WAL ロギングを暗黙にスキップしうる」現状 (08_known_limits.md §one-writer) は黙って壊れる系のフットガンであり、既定で大声で失敗する側に倒す。
- **対象**: `GraphDatabaseOptions` ([src/Quiver/GraphDatabase.cs](../src/Quiver/GraphDatabase.cs) 内)、`TransactionManager`、docs/spec/08_known_limits.md §one-writer / §write-serialization。
- **仕様**:
  1. `BeginTransaction()` 時に active writer が存在する場合、**既定で `WriterLockTimeout` (既定 10 秒) までブロックして待機**し、タイムアウトで `TransactionException` (transient、既存リトライ分類に整合)。SQLite の busy_timeout 相当で、既存の「書き込みゲート」パターンをエンジン内に取り込む形。
  2. `WriterLockTimeout = TimeSpan.Zero` 指定で即時 throw (fail-fast を好むユーザ向け)。
  3. opt-out: `GraphDatabaseOptions.AllowConcurrentWriters = true` (既定 false) で現行動作 (検証済み並行ライタサポートは将来課題のまま)。
- **判断ポイント (遵守)**:
  - **既定はブロック + timeout** (throw ではない)。理由: 既存の推奨パターン (SemaphoreSlim ゲート) と等価な挙動を既定にすることで、正しく使っているアプリの動作が変わらない。即時 throw を既定にすると、たまたま動いていた並行書き込みアプリが全滅する。
  - 待機は **writer 間のみ**。`BeginReadOnlyTransaction()` は一切ブロックしない/されない (現行契約維持)。
  - 待機による**新規デッドロック経路を作らない**: 同一スレッドが writer tx 保持中に再度 `BeginTransaction()` した場合は待機せず即時 `InvalidOperationException` (自己デッドロック検出)。
  - lock-free 読み取り経路・commit ホットパスにコストを足さない (待機判定は Begin 時のみ)。
- **完了条件**: 並行 writer テスト (待機成功 / タイムアウト / 自己デッドロック / opt-out) 緑。既存 stress テストが既定値変更で壊れないことを確認。08_known_limits.md の該当節を「エンジンが既定で直列化する」記述に改訂。
- **工数**: 小〜中 (1〜2 日)。依存: なし。ga-readiness.md の GA-1 を supersede と追記すること。

### REF-9: トランザクションのスレッドアフィニティ違反検出

- **目的**: `[ThreadStatic]` MVCC 文脈のため、生きた tx を別スレッドで使うと WAL スキップ等の静かな破損に至りうる。文書 (08_known_limits.md §threading) だけでなく**ランタイムで検出して throw** する。
- **対象**: `Transaction` ([src/Quiver/Transactions/Transaction.cs](../src/Quiver/Transactions/Transaction.cs))、tx の public エントリポイント群。
- **仕様**:
  1. `Begin` 時に `Environment.CurrentManagedThreadId` を捕捉し、tx の全 public 操作 (read/write/commit/dispose、カーソル `MoveNext` 含む) の入口で比較。不一致なら `InvalidOperationException` (メッセージに「tx はスレッドアフィン」+ known_limits へのリンク文言)。
  2. **常時 ON** (Debug 限定にしない)。1 int 比較は G-5 の定数コスト要件を満たす — ただしトラバーサル反復 (カーソル MoveNext) 経路は `--basic-perf` before/after で退行なし (±2% 以内) を実測確認。
  3. Dispose だけは例外: 別スレッドからの Dispose は throw せず安全側 (rollback) に倒す (finalizer / DI コンテナ経由の破棄があり得るため)。ログ警告は出す。
- **判断ポイント (遵守)**:
  - 検出できないケースを偽装しない: 同一スレッドに戻ってくる `await` は検出不能。**検出は補助であり契約の代替ではない**ことを 08_known_limits.md に明記。
  - opt-out フラグは**設けない** (これを切る正当な理由がない)。
  - MoveNext 経路の実測で退行が出たら、カーソルのみチェック頻度を下げる案 (取得時のみ検査) を**実装せず報告**。
- **完了条件**: 違反検出テスト (Task.Run 越し操作で throw / 同一スレッドは無影響 / 別スレッド Dispose は rollback) 緑。perf 実測記録。
- **工数**: 小 (1 日)。依存: なし。REF-8 と同一セッション可。

---

## Wave 3 — writer queue の製品化 (利用体験)

### REF-10: `ExecuteWrite` / `ExecuteWriteAsync` — 専用ライタスレッドファサード

- **目的**: 08_known_limits.md §write-serialization が推奨する「専用ライタスレッド + Channel」パターンを全ユーザに手書きさせず、**製品 API として本体に同梱**する。MVCC を触らずに async アプリ (ASP.NET / デスクトップ) からの自然な利用感を提供する、費用対効果最大の洗練。
- **対象**: 新規 `src/Quiver/Api/WriterQueue.cs` (名称は実装時に確定可、公開面は `GraphDatabase` の拡張として)、docs/operations、cookbook。
- **API 形 (確定イメージ)**:
  ```csharp
  // GraphDatabase 上のインスタンスメソッド。初回呼び出しで専用ライタスレッド + Channel を遅延起動
  public Task ExecuteWriteAsync(Action<IGraphTransaction> work, CancellationToken ct = default);
  public Task<T> ExecuteWriteAsync<T>(Func<IGraphTransaction, T> work, CancellationToken ct = default);
  public void ExecuteWrite(Action<IGraphTransaction> work);           // 同期版 (内部で同じキューに直列化)
  ```
- **仕様**:
  1. 単一専用スレッドが `Channel<WriteJob>` を drain し、各ジョブを **begin → work → commit を同一スレッド同期スコープで**実行 (スレッドアフィニティ契約に構造的に適合)。結果/例外は `TaskCompletionSource` で呼び出し元へ。
  2. **リトライ内蔵**: `DeadlockException` / `TransactionException` / `SerializabilityException` を既定 5 回・有界バックオフでトランザクション全体リトライ (08_known_limits.md §retry のパターンを内蔵)。work デリゲートは**リトライで複数回呼ばれうる**ことを XML doc に明記 (冪等要件)。
  3. `CancellationToken`: **キュー待機中のみキャンセル可**。実行開始後の tx 中断はしない (中断=rollback の複雑さを v1 で持ち込まない)。
  4. Dispose 時: 新規受付を停止 → キューを drain (タイムアウト付き) → スレッド終了。`GraphDatabase.Dispose` に接続。
- **判断ポイント (遵守)**:
  - **配置は Quiver 本体** (Hosting ではない)。書き込み直列化はコアの利用体験であり、Hosting 未使用のユーザにも必要。Hosting は REF-11 で DI 登録のみ担う。
  - **自動バッチングを実装しない** (複数ジョブの 1 tx への合成は commit 失敗時の帰属が壊れる)。バッチングは group commit (`GroupCommitWindow`) に任せ、その旨を文書化。
  - **トランザクション自体の async 化に踏み込まない**。work デリゲートは同期 (`Action`/`Func<T>`)。`Func<IGraphTransaction, Task>` オーバーロードは**提供しない** (tx 内 await の温床になるため。要望があれば報告)。
  - work 内での `ExecuteWrite*` 再入 (ライタスレッドから自呼び出し) は即時 `InvalidOperationException` (自己デッドロック防止)。
  - REF-8 との関係: キュー経由の書き込みは構造的に直列なので writer lock と競合しない。両者併存の挙動 (キュー外の直接 `BeginTransaction` と混在) をテストで固定。
- **完了条件**: 正常系 / リトライ / キャンセル / 再入 / Dispose drain / 直接 tx との混在テスト緑。スループット実測 (単発 durable commit 律速 ~1ms/commit に対しキュー経由で group commit が効くこと)。cookbook「書き込みの直列化」節を本 API 前提に書き換え。
- **工数**: 中 (2〜3 日)。依存: REF-8 完了後 (writer lock との相互作用を固定するため)。

### REF-11: Hosting 統合 + 運用ドキュメント改訂

- **目的**: REF-10 を DI / Generic Host の世界に接続し、既定の使い方を「洗練された形」に一本化する。
- **対象**: [src/Quiver.Hosting/QuiverServiceCollectionExtensions.cs](../src/Quiver.Hosting/QuiverServiceCollectionExtensions.cs)、docs/operations/01_quickstart.md、docs/cookbook.md、README。
- **手順**: `AddQuiver(...)` が返す `GraphDatabase` の lifetime と `ExecuteWriteAsync` の利用例を Hosting サンプルに追加。`IHostApplicationLifetime` 連動の graceful shutdown (drain) を確認。README のクイックスタートに async 利用例を 1 つ追加。
- **判断ポイント (遵守)**: 新規 public 型を Hosting に増やさない (既存拡張メソッドの範囲で完結)。BackgroundService でのバッチ取込例は cookbook に置く。
- **完了条件**: Hosting 経由の統合テスト 1 本 + ドキュメント更新。
- **工数**: 小 (0.5〜1 日)。依存: REF-10。

---

## Wave 4 — Import/Export の前倒し (migration story の実体化)

### REF-12: Export (JSONL ダンプ)

- **目的**: 「フォーマット自動マイグレーションなし・ソースから再構築」戦略 (08_known_limits.md §no-migration) が成立するのは**公式 dump/load があるとき**。Export を先に作る (Import より先。旧版 DB を読めるのは旧版バイナリだけであり、dump 側が migration の鍵)。
- **対象**: 新規 `src/Quiver/Api/GraphExporter.cs` (または `Quiver.Api` 配下の静的エントリ)、docs/operations に「バックアップとフォーマット移行」節。
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
- **工数**: 中 (2 日)。依存: なし (REF-13 の前提)。

### REF-13: Import (BulkLoader 経由) + round-trip 検証

- **目的**: REF-12 の dump を新規 DB に取り込む。これで「no-migration 制限」が「文書化された移行ワークフロー」に変わる。
- **対象**: 新規 `GraphImporter`、[src/Quiver/Stores/BulkLoader.cs](../src/Quiver/Stores/BulkLoader.cs) 経路、docs/operations の移行手順。
- **手順**:
  1. ヘッダ検証 (`quiver_dump` バージョン) → トークン → ノード → リレーション → 索引定義の順に `BeginBulkLoad(buildAdjacencyIndex: true)` で投入。索引 (B+Tree / FTS / HNSW) は定義から再構築。
  2. **ID 保存**: dump の `NodeId.Value` を維持して取り込めるか BulkLoader の能力を確認。維持できない場合は旧→新 ID の remap 表を内部で保持しリレーション解決 (どちらを採ったか報告)。
  3. **round-trip テスト (本タスクの核)**: 代表 DB (全プロパティ型 + vector + FTS + 索引 + 削除跡あり) を export → import → 再 export し、**2 つの dump がバイト同一** (または正規化比較で同値) であることを CI に固定。RagSandbox 相当のデータでも検証。
- **判断ポイント (遵守)**:
  - Import は**空 DB へのみ** (既存 DB へのマージは非目標。要望があれば別タスク)。
  - 部分失敗時は中途半端な DB を残さない (一時ファイルに構築 → 成功時 rename)。
  - HNSW 再構築は決定的でない (グラフトポロジ) — round-trip 比較は**索引内容を除外し、検索結果の recall で代替検証**する。
- **完了条件**: round-trip CI テスト緑。docs/operations「フォーマット移行手順」が export→import ベースで完結。08_known_limits.md §no-migration の緩和策をこの手順に差し替え。
- **工数**: 中 (2〜3 日)。依存: REF-12。

---

## Wave 5 — 観測性 (追加的、凍結後でも可)

### REF-14: 運用契約のランタイム診断化 (メトリクス + 警告)

- **目的**: known_limits の運用ルールを「読まなくても気づける」ものにする。文書化済みのフットガン (長寿命 tx の WAL ピン留め、WAL 成長、HNSW tombstone) を ILogger 警告 + `System.Diagnostics.Metrics` で既定露出する。
- **対象**: コア各所 (Checkpointer / TransactionManager / HnswIndex / WAL)、[src/Quiver.OpenTelemetry/QuiverInstrumentation.cs](../src/Quiver.OpenTelemetry/QuiverInstrumentation.cs)。
- **仕様 (計装項目)**:
  | 項目 | 種別 | しきい値/備考 |
  |---|---|---|
  | 長寿命トランザクション | ILogger Warning (1 回/tx) | 既定 30 秒超 or WAL ピン留め 64MB 超。オプションで調整可 |
  | WAL サイズ / 切り詰め契機 | ObservableGauge + Counter | checkpoint 完了時に更新 (ホットパス外) |
  | group commit 効率 | Histogram (batch サイズ) | commit 経路 — **0-alloc 必須、実測で退行 ±2% 以内** |
  | HNSW tombstone 比率 / rebuild 発火 | ObservableGauge + Counter | 既存 auto-rebuild トリガに接続 |
  | checkpoint 所要 / vacuum 回収量 | Histogram / Counter | |
- **判断ポイント (遵守)**:
  - コアの計装は **`Meter` + `ILogger` のみ** (どちらも既存依存の範囲内。OpenTelemetry パッケージへの依存をコアに足さない)。Quiver.OpenTelemetry は Meter 名の登録ヘルパを足すだけ。
  - **測定はホットパス外で**: gauge は checkpoint / vacuum / rebuild 等の低頻度イベントで更新。トラバーサル反復・per-op 経路に計装を入れない。commit 経路の histogram のみ例外とし、実測 gate (G-5) を通す。
  - 警告ログはレート制限 (同一 tx で 1 回、WAL 警告は指数間隔) — ログ洪水を作らない。
- **完了条件**: 各項目のユニットテスト (しきい値発火 / 非発火)。commit 経路 before/after 実測。docs/operations/03_performance_tuning.md にメトリクス一覧表を追加。
- **工数**: 中 (2〜3 日)。依存: なし。

### REF-15: BM25 コーパス統計の checkpoint 時 auto-refresh

- **目的**: 08_known_limits.md §bm25-stats の「将来方針」を実装。snapshot 統計の陳腐化を checkpoint 契機で自動解消する (安価な体感品質向上)。
- **対象**: Checkpointer / AdaptiveCheckpointController、`GraphStats` キャッシュ保持点。
- **仕様**: checkpoint End 後に db レベルの既定 `GraphStats` キャッシュを再収集して差し替える。ユーザが明示的に渡した snapshot は影響を受けない (現行 API 契約不変)。opt-out: `GraphDatabaseOptions.RefreshStatsOnCheckpoint = true` (既定 ON)。
- **判断ポイント (遵守)**:
  - 再収集は checkpoint スレッド上で行い、**検索経路にロックを持ち込まない** (差し替えは参照の atomic swap)。
  - 再収集コストが checkpoint 時間を有意に延ばす場合 (実測で +10% 超) は近似更新 (N と avgdl のみ) に縮退し報告。
  - WAND の「取りこぼしなし」性質が統計更新のタイミングに依存しないことを既存テストで再確認。
- **完了条件**: 陳腐化解消のテスト (書き込み → checkpoint → スコアが新統計反映)。checkpoint 時間の before/after 実測。08_known_limits.md §bm25-stats の「将来方針」を「実装済み (既定 ON)」に改訂。
- **工数**: 小 (1 日)。依存: なし。REF-14 と同一セッション可。

---

## スコープ外 (本トラックでは扱わない — 参照用の判断記録)

| 項目 | 判断 |
|---|---|
| マルチバリュープロパティ ([multivalue-property-design-notes.md](multivalue-property-design-notes.md)) | **凍結継続。** MVCC チェーンに可視性と多重度の 2 概念を同居させる仕様化コストが高い。グラフ的にはタグ=ノード+エッジが本来の語彙で、`metadataJson` 代替もある。実需が出るまで着手しない。 |
| トランザクション自体の async 化 | **やらない。** `[ThreadStatic]` MVCC と根本衝突。REF-10 のファサードで代替。 |
| Quiver Studio | 本トラック対象外。コアの semver / API 安定性ポリシーの適用外であることを docs 側で明文化する (REF-7 の api-stability 改訂に 1 行含める)。GA を Studio の完成度にブロックさせない。 |
| Quiver.Mcp | 本トラック対象外 (差別化として継続支持)。REF-4 完了後に `traverse` の FTS 起点を `FtsQuery` 組み立てに移行するタスクを別途起票。 |
| ネイティブ並行 writer / きめ細かい索引ロック | 将来課題のまま (08_known_limits.md 記載どおり)。REF-8 は排他の強制であり並行化ではない。 |
| net8.0 マルチターゲット / 英語ドキュメント | 個人利用目的の現状では保留。MCP 等で外部ユーザを取りに行く判断をした時点で英語 README を最優先タスクとして起票。 |

## 完了の定義 (トラック全体)

- REF-2/3 により approved.txt が「意図して公開した API のみ」になり、宣言数の縮小が記録されている。
- 誤用 3 態 (並行 writer / スレッド越し tx / 長寿命 tx) がすべて「黙って壊れる」から「throw または警告」に変わっている。
- `ExecuteWriteAsync` が cookbook / README の推奨書き込みパターンになっている。
- export → import の round-trip が CI で緑であり、no-migration 制限の緩和策が公式手順を指している。
- REF-7 のチェックリストが全て埋まった状態で 1.0.0 が宣言できる (宣言自体はユーザ判断)。
