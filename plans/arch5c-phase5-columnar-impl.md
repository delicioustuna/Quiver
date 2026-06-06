# ARCH-5c Phase 5 — 永続 MVCC 列指向 本実装プラン

> ブランチ: develop / 作成: 2026-06-06 / 前提: spike (`spike/columnar-mvcc`) で 3 gating kill criteria 全 PASS、本流取り込み決定。spike の `MvccScalarColumn` 設計 (dense head value/xmin/xmax + 超過版 delta、visibility は `Visibility.IsVisible`) を production 化する。
> 設計根拠: `plans/columnar-mvcc-spike.md` (計測), `docs/design/11` §7.1。

## 確定済み (spike 実測)
- projection MVCC ~325× / point MVCC ~57× / write-amp ~1.0× (group-commit fsync 共有)。
- 列は head dense + delta の base+delta。visibility は既存 MVCC と同一判定で apples-to-apples。

## 設計判断 (着手承認で確定する)
- **D1 登録モデル**: どの (entity-kind, scalar key) を列化するか。
  - (A) **opt-in 明示 API** (`CreateColumn(kind, key)`): WAL byte 増幅を制御、hot key をユーザが宣言。**推奨**。
  - (B) 索引登録済み scalar key を全自動: zero-config だが全 key が +~8KB WAL/tx を払い WAL 肥大の懸念。
  - (C) projection で hot と観測した key を自動列化: 複雑、後続。
- **D2 永続レイアウト**: head = container テナントの dense ページ (seq→{value8/xmin8/xmax8})、delta = overflow ログ (seq, value, xmin, xmax の追記)。in-memory cache は open 時に rebuild (まず eager、後で lazy/mmap 検討)。FormatVersion V6→V7。
- **D3 recovery**: 列ページは container WAL 対象 (graph と同一)。crash 後は物理ページ redo + open 時 cache rebuild。delta は冪等再生。

## 段階計画 (各段で build + test 緑、develop 直接)
- **5a 列ストア基盤 (永続)**: `MvccScalarColumn` を永続・recoverable 化 (container テナント head+delta、WAL、open rebuild)。単体 + reopen テスト。format V7。
- **5b 登録 + catalog**: `CreateColumn`/`DropColumn` API + catalog 永続化 (D1 で確定したモデル)。
- **5c write 経路統合**: `GraphTransaction.SetProperty`(node/rel) が列 key のとき同 tx で列維持 (spike hook の production 化)。overwrite/delete→delta。
- **5d read/optimizer 統合**: projection/集約/filter operator が列 key で列スキャンを使用。optimizer コストモデルで列スキャン選択。
- **5e compaction**: horizon 未満 delta を merge、vacuum / autovacuum へ配線。
- **5f breadth**: 全 scalar 型 (Bool/Int32/Int64/Double) + node 列 + 多 key。
- **5g hardening**: crash contract (両 backend) / property test / TS-6 bench regression / PublicApi 再承認 / format bump 確定。

## 完了条件
- 列 key の projection/集約が inline 比 ≥ 10× (spike 325× を production で維持) / write-amp ≤ 2× / crash recovery 緑 / 全スイート緑。
- 対象ワークロード (mutation:projection 比) を製品要件として確認済み。

## 結論欄
- 進行中。完了: 5a (4c0ca8f, 列ストア永続基盤) / 5b (bfdee14, opt-in 登録+catalog) / 5c (ef5b692, write 経路統合) / 5d (8284b2e, read/optimizer 統合) / 5e (cd3af9c, delta compaction を vacuum へ配線) / 5f (eeeace0, breadth 検証 test-only)。
- 5c メモ: abort 正当性は MVCC 可視性 (before-image undo → ReloadColumns で head cache 再構築 → OnRolledBack で delta prune)。delta は in-memory 維持 (永続 delta-log は 5e/compaction で再検討、ユーザ選択)。
- 5d メモ: full-scan 集約 (g.Nodes()/g.Relationships() の Sum/SumLong/Mean/Max/Min) を列スキャン化。ScalarColumnStore に scalar 型ヘッダ + TryAggregate、QueryOptimizer.ShouldUseColumnAggregate コスト判定、ITransaction.Snapshot/Committed 露出、g.Relationships() 新 API + PropertyLookup の EntityKind 対応。数値型のみ列、filter 付きは row fallback。列==行は「列あり vs DropColumn 後」で同値検証。opt-in 並行性 (CreateColumn 跨ぎ writer) は非保証→5g。
- 5e メモ: ColumnManager.Compact を Vacuum (node 回収後・committed prune 前) に配線。VacuumReport.ReclaimedColumnVersions 追加。AutoVacuum は backend.Vacuum() 経由で自動的に periodic 化。
- 5f メモ: breadth は本体変更不要 (汎用実装済) → test-only で固定。Bool 列は値保持のみ (数値集約は row フォールバック)。
- 残: 5g (hardening: crash contract 両 backend / property test / TS-6 bench / PublicApi 再承認 / format bump 確定 / opt-in 並行性)。
