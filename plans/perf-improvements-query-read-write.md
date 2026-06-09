# Quiver 性能改善計画 — クエリプラン / 読取経路 / MVCC 書込増幅

> 作成: 2026-06-08 / 対象ブランチ: develop
> 起点: `--basic-perf` 実測（commit `2b0880e`）と README 性能目標の乖離。改善 3 点をコード精読で
> 妥当性確認した上で、spike 先行（kill criteria → 実測 → 採否）で進める。

## Context

`--basic-perf` runner（`benchmarks/Quiver.Benchmarks/Standalone/BasicPerfRunner.cs`）で基本性能を実測し、
README 性能目標との乖離から 3 つの改善余地が浮上。各タスクを **spike 先行（kill criteria を先に数値固定
→ 最小 spike で実測 → 採否判断）** で進める（推論より実地検証）。目的は「正確性を一切落とさず」
実用ワークロード（小クエリ大量 / 非bulk 読取 / 書込）の体感性能を底上げすること。

### 本ライブラリの位置づけ（遵守する制約）
- **embedded / 単一プロセス / 単一 writer。分散・レプリケーション・RPC は想定しない。**
- **正確性優先**: MVCC snapshot isolation + SSN serializable + crash recovery を不変に保つ。
  いかなる最適化も可視性・耐久性を弱めない。
- **pre-release クリーンブレイク**: format 変更はマイグレーション無しで可。ただし format bump を伴う
  変更（タスク C）は spike で厳しく gate。

## 検証済みの根拠（コード精読）

| 点 | 主張 | 根拠 |
|---|---|---|
| 1 | クエリ毎にプラン再構築（キャッシュ無し）、~60µs 固定費 | 全 terminal op (`ToList`/`AsCursor`/`Count`/`Next`) が `Compile()` = `LogicalOptimizer.Optimize` + `PhysicalPlanner.Plan` を毎回呼ぶ（`src/Quiver/Client/GraphTraversal.cs` 793-839）。`LogicalOptimizer.RewriteKnn` は KNN 無しでも `TryCollectKnnStack` で毎ノード `List` 確保（`src/Quiver/Query/LogicalOptimizer.cs` 120-128）。実測 wrapped 63.5µs vs raw adj 1.4µs |
| 2 | 非bulk DB は読取が常に linked-list（2.3µs/edge, MVCC 毎回） | `_adjStore` は null 許容で **bulk load / CompactAdjacency でのみ構築**（`src/Quiver/Backend/BinaryGraphStorageBackend.cs` 29-31,213）。`Expand` は `adj != null && adj.HasBlock(source)` 時のみ adjacency、無ければ linked-list（`src/Quiver/Transactions/InlineGraphAccessMethods.cs` 153-164）。実測 linked 2.3µs/edge vs adj 14ns/edge（~160×） |
| 3 | sidecar で書込 ~1.5× 増幅（retention 63-71%） | FT-32 ベンチ実測（`docs/benchmarks/2026-05-30_FT-32_MvccSidecarThroughput.md`）。entity create 毎に sidecar ページへの PinForWrite + WAL PageImage が 1 回増える。**FT-31/32 で意図的に選んだトレードオフ**（record +16B 案は cache line 64B 跨ぎで却下、承認済） |

## 共通インフラ（先に整える）

- **before/after ハーネス**: `BasicPerfRunner` に各タスク用 micro-bench を追加（既存構造流用）。
  format/挙動が変わる変更（C）は FT-32 同様 `git worktree` で baseline を build し同一マシン比較。
- **各 spike 後**: kill criteria クリア & 本実装完了 → README「性能目標と実測」表 + `docs/benchmarks/<date>_<task>.md` 更新。
- 既存テスト（`Quiver.Tests` / `Quiver.Operators.Tests`、MVCC/SSN/crash contract）を全タスクで緑に保つ。

---

## タスク A — クエリ結果経路の per-row 世代 stamping 高速化（最優先）

### ⚠ Spike A0 実測結果（2026-06-08）— 仮説反転

当初仮説「プランキャッシュ不在の per-query 固定費 ~60µs」は **誤り**だった（`--spike-a` runner で実測）:

```
traversal build   : 0.05 µs/query
Optimize          : 0.25 µs/query
PhysicalPlanner   : 0.16 µs/query
Compile (opt+plan): 0.42 µs/query   ← プラン構築は誤差。キャッシュ不要
FULL (1-hop deg100): 62.3 µs/query (623 ns/edge)
FULL (stamping 無効): 7.4 µs/query (74 ns/edge)
→ 世代 stamping の寄与: 54.9 µs/query (549 ns/edge) = 全体の 88%
```

**真因**: `Execute`/`ExecuteCursor` が結果行ごとに `QueryRowMaterializer.StampNodeGenerations`
→ `INodeStore.CurrentGeneration(seq)` → `EntityVersionStore.Read`（**version sidecar の page PinForRead/Unpin**）を
呼ぶ（ARCH-5b、`src/Quiver/QueryResult.cs` 15-25 / `src/Quiver/GraphTransaction.cs` 558-577 /
`src/Quiver/IQueryCursor.cs` 36-55）。per-row の sidecar pin が 549 ns/edge を占める。
副次的に per-row `TupleSlot[]` / `QueryRow` 確保（6.5KB/query、~70 ns/edge 側）。

**世代の生成則**（`VersionedNodeStore.Allocate` 67-96）: `generation = Read(seq).Generation + 1`。
fresh seq / bulk は **gen=1**。gen>1 は **vacuum 回収で free list の seq を再利用したときのみ**。
→ **vacuum 再利用が無い DB（読取主体の常態）は全ライブノードが gen=1**。

### 改善方針（仮説反転を受けて）

**A-main（高インパクト・要 MVCC 慎重設計）**: 「slot 再利用なし」高速パス。
version store に `AnyReuse`（gen≥2 を払い出したか）を persist（header 1 ビット）。
`!AnyReuse` の間は stamping を **sidecar read 無しで gen=1 を pack**（per-row pin を完全に省く）。
vacuum 再利用が起きたら flag を立て per-row read にフォールバック。**正確性は厳密保持**
（再利用なし⇔全 gen=1 が成立）。reopen 跨ぎは header の persist で担保。

**A-sub（安全・小）**: streaming `PhysicalOperatorCursor` の per-row `TupleSlot[]`/`QueryRow` 確保を
バッファ再利用へ（`Current` は次 `MoveNext` まで有効の契約済み）。`Execute`（materialize 経路）は
各行独立が要るので対象外。

**Kill criteria**: 再利用なし DB の 1-hop（deg100）operator 経路を **≤ 150 ns/edge（現 623 から ≥4×）**。
クエリ結果・NodeId 往復一貫・世代スタンプ正当性・全 MVCC/SSN/generation テスト緑。
vacuum 再利用後の経路も従来どおり正しい世代を返すこと（回帰テスト追加）。

**制約（最重要）**: NodeId 同一性・往復一貫・stale 検出（TryResolve）の意味論を一切変えない。
`!AnyReuse` 不変条件が崩れる経路（vacuum/OP-3 free list 再利用）を漏れなく flag に反映する。

**対象**: `src/Quiver/QueryResult.cs`（stamp）、`src/Quiver/Stores/VersionedNodeStore.cs` /
`VersionedRelationshipStore.cs`（`CurrentGeneration` 高速パス + AnyReuse 追従）、
`src/Quiver/Stores/EntityVersionStore.cs`（AnyReuse の persist）、
`src/Quiver/IQueryCursor.cs`（cursor バッファ再利用）。
※ `LogicalOptimizer`/`PhysicalPlanner`/plan cache は **不要**（spike で棄却）。

### A-sub 着手手順（別セッション cold start 用）

**現状**: A-main は develop 採用済み（merge `8e8e0a8`、`docs/benchmarks/2026-06-08_TaskA_GenStampFastPath.md`）。
世代 stamping は高速化済みで、1-hop クエリ（`AsCursor` 経路）の残コスト ~78 ns/edge のうち、
per-row の `TupleSlot[]`/`byteData` 確保（`--spike-a` で allocated **6,584 bytes/query**）が主。
`QueryRow` は struct なので確保コストは配列側。

**目的**: streaming カーソル `PhysicalOperatorCursor`（`src/Quiver/IQueryCursor.cs`）の per-row 配列確保を
1 回確保 + 再利用に置き換え、per-row アロケーションをゼロ化する。

**契約根拠**: `IQueryCursor.Current` は「次の `MoveNext` まで有効」（同 interface の doc 明記）。
`TraversalCursor<T>` は `_projection(cursor.Current)` で即座に値型へ射影（コピーアウト）するため、
slots バッファを跨いで再利用しても安全。

**スコープ（重要）**:
- 対象は **streaming 経路のみ**（`GraphTransaction.ExecuteCursor` → `PhysicalOperatorCursor`）。
- `GraphTransaction.Execute`（materialize → `List<QueryRow>`、`ToList`/`Next`/`Count` が使う、`GraphTransaction.cs` 548-）は
  行が保持されるため **プール対象外**（各行独立の配列が要る）。触らない。

**実装手順**:
1. `PhysicalOperatorCursor` に再利用バッファ `private TupleSlot[]? _slots;`（初回 `MoveNext` で `cur.ColumnCount` 長を確保し以後再利用）。
2. `MoveNext` で `new TupleSlot[...]` を毎回確保せず `_slots` を上書き。`_current = new QueryRow(_slots, _byteData)`（struct なので確保なし）。
3. `byteData` は string/bytes 列が無ければ null のまま（典型の `NeighborOnly` では発生しない）。列がある場合のみ
   配列を保持し、行ごとに該当スロットを詰め直す（前行の残骸を残さない）。
4. （任意・別件）`GraphTraversal.Count()`（`src/Quiver/Client/GraphTraversal.cs`）は行を捨てるので
   materialize ではなく `ExecuteCursor` で数えるだけにすると確保を回避できる — A-sub の射程外なら触らない。

**Kill criteria**:
- `--spike-a` の `allocated` が 6,584 bytes/query → 一定（初回バッファのみ、数百 bytes 以下）。
- `--spike-a` FULL / `--basic-perf` wrapper の per-edge が 78 → **≤50 ns/edge**。
- クエリ結果・行順・値が不変。

**検証**: `dotnet build benchmarks/Quiver.Benchmarks -c Release` → `--spike-a` / `--basic-perf` before/after。
`dotnet test tests/Quiver.Tests` + `tests/Quiver.Operators.Tests`（カーソルは全 traversal が使う）。
緑 & kill criteria クリアで commit（`perf: [Task A-sub] streaming cursor バッファ再利用`）→ develop。

**対象ファイル**: `src/Quiver/IQueryCursor.cs`（`PhysicalOperatorCursor`）。射程を広げる場合のみ
`src/Quiver/Client/GraphTraversal.cs`（`Count`）。

---

## タスク B — 非bulk 読取経路の高速化（高優先）

**問題**: 非bulk DB は読取 linked-list 固定（2.3µs/edge）。pre-MVCC 比 ~4× 遅いのは per-edge の
MVCC 可視性チェック（sidecar lookup + registry）。

**Spike B1（配分特定）**: `EnumerateRelationships` 1-hop で `tx.Relationships.Read(relId)` の内訳
（`RelVersionMeta` xmin/xmax 読み・`CommittedTxRegistry` 照合・ページ pin）を計測。

**Kill criteria（B2 主軸）**: read-only / 全 committed 常態で linked-list 1-hop を
**≤ 0.8µs/edge（MVCC ohead を ≥3×）**。可視性正当性不変。
- アプローチ: 単一 read snapshot で「全 committed→可視」高速パスを設け per-edge 照合を short-circuit/バッチ化。

**Kill criteria（B1 補助・opt-in）**: 非bulk 書込後の自動 `CompactAdjacency` で読取が bulk-adjacency の **2× 以内**、ポーズ有界。
- アプローチ: `GraphDatabaseOptions.AdjacencyMaintenance`（既定 Off）、active tx 0 の隙に best-effort 起動
  （`AutoVacuumWorker` 配線流用）。**制約明記**: V2 payload lane 未対応・O(R) フルリビルド・active tx 中不可。
- → B2（普遍）を主、B1（opt-in）を従。

**対象**: `src/Quiver/Stores/VersionedRelationshipStore.cs`、`src/Quiver/Stores/EntityVersionStore.cs`、
`src/Quiver/Core/CommittedTxRegistry.cs`、`src/Quiver/Transactions/InlineGraphAccessMethods.cs` /
`src/Quiver/Backend/BinaryExpandCursor.cs`、`src/Quiver/Backend/BinaryGraphStorageBackend.cs`、`src/Quiver/GraphDatabase.cs`。

---

## タスク C — MVCC 書込増幅の削減（sidecar co-locate、最侵襲・format bump・最後）

**問題**: entity create 毎の sidecar 追加 PinForWrite + WAL PageImage で ~1.5× 増幅。単一ファイル化後の再評価。

**Spike C0（プロトタイプ）**: version sidecar（`NodeVersionMeta`/`RelVersionMeta`）を record と同一
container ページ / page-adjacent レーンに同居させ 1 page pin で record+meta カバー。worktree で現行比較。

**Kill criteria**: 書込 retention **pre-sidecar 比 ≥ 85%**（CreateNode 償却 ~17µs → ~12µs 目安）。
crash recovery 不変・SSN Pstamp/Sstamp 保持・read 側 `inUse=false` 早期 return 維持。
- **未達 / recovery・SSN に綻び → DEFER**（現行の承認済みトレードオフ維持）。

**実装（spike クリア時のみ）**: format **V8**（クリーンブレイク）。sidecar を slotted heap の record 隣接 /
同一テナントにレーン interleave。

**対象**: `src/Quiver/Stores/EntityVersionStore.cs` / `EntityVersionMeta.cs`、
`src/Quiver/Stores/VersionedNodeStore.cs` / `VersionedRelationshipStore.cs`、
`src/Quiver/Storage/VersionedRecordHeap.cs`、`src/Quiver/Core/FormatVersion.cs`（V8）、`src/Quiver/Wal/WalFileKind.cs` + recovery。

---

## 推奨順序

1. **A**（query 層に閉じる・format 変更なし・最高 ROI）— A0 → A1 → 必要なら A2。
2. **B**（B2 普遍を主、B1 opt-in を従）— A と独立に進行可。
3. **C**（format bump・最も厳しく gate）— spike が ≥85% を満たした場合のみ本実装。

各タスク = 「spike → kill criteria 判定 → 採否 → 本実装 → 全テスト緑 → README/bench 更新」を 1 サイクル。
A/B/C は別 commit。**C のみ spike 結果を提示してから本実装の承認を取る**。

## Verification

- **機能不変**: `dotnet test Quiver.slnx`（特に MVCC/SSN/crash contract、operators）を各タスクで緑。
  `Quiver.PublicApi.Tests` で公開 API 差分を承認管理。
- **性能**: `dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --basic-perf` を before/after。C は worktree baseline 比較。
- **回帰**: `Quiver.Benchmarks.RegressionCheck`（既存 sentinel）で他オペレータ劣化なし。
- **ドキュメント**: 確定行のみ README 表更新 + `docs/benchmarks/<date>_<task>.md`（gitignore）。

## 進捗

- [x] A0 spike（配分計測）— **仮説反転**: 真因は per-row 世代 stamping (sidecar read) 88%、plan cache 不要
- [x] A-main 実装 + 計測（7.7×）+ 回帰テスト + README/bench → **develop 採用** (merge 8e8e0a8)
- [x] A-sub: streaming cursor バッファ再利用 — **仮説反転（再）**: per-row 確保はボトルネックでなく寄与 ~3 ns/edge。
  allocated 6,584→1,848 B/query (−72%, per-row ゼロ化) は達成だが `≤50 ns/edge` は**未達**（72–73.5 ns/edge）。
  残 ~60 ns/edge は operator/volcano/MVCC 反復コスト＝**タスク B 領分**。アロケーション削減（正確・ゼロリスク・
  結果件数比例で効き GC 圧減）の価値でユーザ判断 **develop 採用**。`docs/benchmarks/2026-06-09_TaskA-sub_CursorBufferReuse.md`
- [x] B1 spike + B2 実装 — **仮説反転（再）**: linked-list overhead の真因は MVCC 可視性ではなく
  (1) `Read` の per-edge 2 回 pin + 破棄 ToArray (145 B/edge)、(2) `PagedFile.PinForRead/Write` の
  **pin ごと全 8KB CRC32**。B2 = 単一 pin combined read (`VersionedRecordHeap.TryReadHeadInto`) +
  **checksum 検証を load 時のみに** (`PagedFile`)。結果 linked-list 1-hop **2.228→0.110 µs/edge (20×)**、
  kill criterion ≤0.8 を 7× マージンで達成。read/write/2-hop/query 全般に波及 (write も ~1.2×、
  2-hop 3.7×、wrapper 1.75×)。全スイート緑 (crash/chaos/公開API 含む) → **develop 採用**。
  B1 opt-in (CompactAdjacency) は当面不要。`docs/benchmarks/2026-06-09_TaskB_ReadPathChecksumAtLoad.md`
- [x] C0 spike + format-stable 修正 — **仮説修正（再）**: sidecar 増幅の主因は co-locate で消える
  「別ページ pin」ではなく、`WalPageContext.LogPageImage` が **UnpinDirty ごとに WAL page-image を
  Encode (trim+RLE, 半埋め 8KB で ~5µs)** していたこと (`_pending` は latest-wins coalesce 済みなので
  中間 Encode は無駄)。format-stable 修正 = `_pending` を生 bytes 化し Encode を `FlushPending` (commit)
  でページ毎 1 回に。結果 **write ~4×** (CreateNode 14.2→3.5µs, CreateRel 29.5→6.7µs)、recovery 形式不変、
  全スイート緑 (crash/chaos 含) → **develop 採用**。
  **co-locate (format V8) は DEFER**: kill criterion「retention ≥85%」は未達 (CreateNode ~72%、ただし
  deferred-encode が分母も下げるため比は不変) だが、絶対 write は計画目標 ~12µs を大幅に下回り実害解消。
  残 ~0.9µs/create のための format bump + recovery 改修はリスク/効果不見合 (ユーザ判断)。
  `docs/benchmarks/2026-06-09_TaskC_DeferredWalEncode.md`
