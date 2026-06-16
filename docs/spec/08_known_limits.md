# Known Limits & Bugs

> as-built specification (v1 baseline, 2026-06-16 audit)

## Silent-Corruption Bugs (Audit) {#audit}

The following bugs were identified during the v1 consolidation audit. They represent
potential silent-corruption scenarios under specific conditions.

### Bug #1: Recovery Pass-3 Loser-Undo Clobber {#recovery-clobber}

- **Severity**: HIGH — **FIXED (2026-06-16)**
- **Location**: `RecoveryManager.RecoverLogical()` (`src/Quiver/Transactions/RecoveryManager.cs`)
- **Description**: Pass 2b redoes presume-committed FtLeafMutation records (state-setting,
  last-write-wins). Pass 3 then undoes loser transactions' FtLeafMutation records in LIFO
  order. The bug: Pass 3 did not check whether a presume-committed transaction had already
  established the key being undone. Because a loser `Delete(K)` carries the old value (for
  undo re-insertion) and its undo is `UpsertRaw(K, oldValue)` — an *unconditional* overwrite
  — undoing a loser mutation for a key that a committed tx had re-established in Pass 2b
  clobbered the committed value (or resurrected a key the committed state had removed).
- **Trigger**: an aborted (or torn) FT-maintaining transaction whose `FtLeafMutation` for a
  key K is in the WAL, followed by a committed transaction that re-writes K, then a crash.
  This is reachable even on the single-writer engine via abort-then-commit-then-kill
  (an aborted tx's logical mutations are re-undone by recovery because the in-process
  inverse is not durable under Suppressed leaf mode).
- **Fix**: Pass 2b records the set of `(tenant, key)` it re-applies. Pass 3 skips logical
  undo for any key in that committed set — the committed value established by Pass 2b is
  authoritative under state-setting last-write-wins semantics.
- **Regression test**:
  `FullTextCrashContractTests.AbortedTx_logical_undo_must_not_clobber_committed_key_after_recovery`.

### Bug #2: FT Savepoint Partial Rollback {#ft-savepoint}

- **Severity**: MEDIUM — **FIXED (2026-06-16)**
- **Location**: `WalPageContext` / `AbortUndoHandler` / `Transaction.RollbackTo`
- **Description**: The FT logical undo log was a flat `List<FtUndoEntry>`, not bucketed
  by savepoint level. `RollbackTo(savepoint)` called `UndoPartial` for physical pages
  but never undid FT mutations — only full abort did. Worse, because `FtLeafMutation`
  records are written *eagerly* to the WAL, the savepoint-discarded mutations remained in
  the committed transaction's WAL, so even an in-process-only undo would be re-applied by
  recovery's Pass 2b (redo of committed mutations).
- **Trigger**: `tx.Savepoint()` → FT-maintaining write → `tx.RollbackTo(sp)` → `tx.Commit()`
  (and the crash variant: same followed by a kill).
- **Symptom**: stale postings survive the partial rollback and are committed, causing
  false-positive search hits and dropped terms on a live entity. Orphan GC cannot clean
  these because the entity is alive.
- **Fix** (two layers):
  1. **In-process**: `_ftUndoStack` is now bucketed by savepoint level, parallel to
     `_beforeImageStack`. `RollbackTo` collects the buckets `>= level` and applies the
     inverse to the live FT trees (`UndoFtLogicalPartial`).
  2. **WAL (crash path)**: each inverse is also written as a **compensating
     `FtLeafMutation`** record (`LogFtLeafCompensation`). Recovery Pass 2b replays
     forward-then-compensator and converges to the rolled-back state. Compensators are
     deliberately NOT added to the undo stack, so a later full abort does not re-undo them
     (which would double-revert).
- **Regression tests** (`FullTextCrashContractTests`):
  `RollbackToSavepoint_undoes_fulltext_mutations_in_process`,
  `RollbackToSavepoint_fulltext_undo_survives_kill`,
  `RollbackToSavepoint_then_full_abort_restores_committed_state`,
  `NestedSavepoint_release_then_rollback_undoes_merged_fulltext`.

### Bug #3: WAND Early-Termination Bound {#wand-early-term}

- **Severity**: MEDIUM (未修正、documented)
- **Location**: `Bm25Scorer.RankWand()` (`src/Quiver/Operators/Bm25Scorer.cs`)
- **Description**: The per-term upper bound is computed from `maxTf` and `normMin` at
  snapshot time. If these statistics are stale (concurrent writers changed term frequencies
  or document lengths), the upper bound may underestimate actual scores. This can cause
  WAND to skip documents that should appear in the top-k.
- **Trigger**: high-concurrency FT write workload during search
- **Impact**: incomplete top-k results (missing relevant documents). Not data corruption,
  but search quality degradation.

### Bug #4: Norms Snapshot Staleness {#norm-snapshot}

- **Severity**: LOW
- **Location**: `FullTextIndex.CollectTermStats()`
- **Description**: norms scan runs concurrently with writers. `minDocLen` may be outdated,
  causing BM25 normalization to slightly over-score long documents.
- **Impact**: minor ranking quality variation under concurrent writes.

### Bug #5: Concurrent FT + Abort Leaf Inconsistency {#concurrent-ft-abort}

- **Severity**: LOW
- **Location**: `AbortUndoHandler.UndoFtLogical()` (`src/Quiver/Transactions/AbortUndoHandler.cs`)
- **Description**: leaf state-setting mutations are not atomic with page CLR. Under
  extreme concurrency (one tx aborting while another inserts into the same leaf page),
  the leaf state may be inconsistent -- missing entries or orphans.
- **Trigger**: requires tight concurrent FT abort + insert timing.
- **Impact**: postings index inconsistency, recoverable via orphan GC.

## MVP Limitations {#mvp-limits}

### HNSW Overwrite {#hnsw-overwrite}

`HnswIndex.Insert(seq)` for an existing sequence updates the vector payload but does
not re-link the HNSW graph topology. The old graph links remain, pointing to the new
vector. This may reduce search quality when vectors change significantly.

Mitigation: automatic rebuild when tombstone count exceeds live count.

### Single-Writer {#single-writer}

Quiver supports one concurrent write transaction at a time. Multiple read-only
transactions can run concurrently with a single writer.

### No Automatic Migration {#no-migration}

Opening a database with a different `FormatVersion` throws `FormatVersionMismatchException`.
There is no automatic migration path. Databases must be recreated from source data.

### In-Process Only {#in-process}

Quiver runs in the application process. There is no server mode, network protocol,
or inter-process access. The `*.quiver` file is opened with exclusive file lock
(`FileShare.None`).

### Checkpoint WAL Truncation {#wal-truncation}

WAL truncation only occurs after a complete checkpoint (Begin + End). If the application
runs for extended periods without a checkpoint (e.g., very large long-running transactions),
the WAL file grows unboundedly.
