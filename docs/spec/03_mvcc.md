# MVCC & Transactions

> as-built specification (v1 baseline)

## Isolation Level {#isolation}

Quiver supports **snapshot isolation**. Each transaction sees a consistent snapshot of the
database as of its start LSN (`SnapshotLsn`). Writers do not block readers; concurrent
readers see their own consistent snapshot.

## Transaction Lifecycle {#lifecycle}

```
Active → Preparing → Committed
  │
  └──────────────→ Aborted
```

| State | Value | Meaning |
|---|---|---|
| `Active` | 1 | In progress, reads and writes allowed |
| `Preparing` | 2 | Commit preparation phase |
| `Committed` | 3 | Durably committed (WAL flushed) |
| `Aborted` | 4 | Rolled back (explicitly or on Dispose without Commit) |

## Transaction ID {#tx-id}

`TransactionId` is a monotonically increasing identifier. The `CommittedTxRegistry` tracks
which transaction IDs have committed, enabling visibility decisions.

## Snapshot State {#snapshot}

`SnapshotState` captures the set of committed transactions visible to a given transaction.
Column-scan aggregations use this directly for visibility checks without going through
the operator pipeline.

## Commit {#commit}

1. Write `Commit` record to WAL
2. Flush WAL to disk (synchronous)
3. Fire `OnCommitted` hooks
4. Trigger checkpoint if threshold reached and no active transactions

## Abort / Rollback {#abort}

`AbortUndoHandler` processes undo:

1. **Physical undo**: restore before-images from the `_beforeImageStack` in LIFO order
   (page-level undo via CLR records)
2. **Logical undo**: `UndoFtLogical` reverses FT leaf mutations in LIFO order
3. Write `Abort` record to WAL
4. Fire `OnRolledBack` hooks

Dispose without Commit triggers implicit abort.

## Savepoints {#savepoints}

`SavepointId` identifies a savepoint within a transaction. Savepoints follow SQL semantics:

- `Savepoint(name?)` → creates a savepoint, returns `SavepointId`
- `RollbackTo(SavepointId)` → undoes changes after the savepoint, invalidates inner savepoints
- `ReleaseSavepoint(SavepointId)` → consumes the savepoint, merges changes into parent scope

### Before-Image Stack {#before-image-stack}

`WalPageContext` maintains a `_beforeImageStack` of per-savepoint buckets. Each `PinForWrite`
captures a before-image into the current bucket. `RollbackTo` restores before-images from
buckets newer than the target savepoint.

### Limitation: FT Savepoint {#ft-savepoint-limitation}

The FT logical undo log (`_ftUndoLog`) is a flat `List<FtUndoEntry>` rather than
savepoint-bucketed. `RollbackTo` does NOT undo FT leaf mutations -- only full abort does.
This is a known limitation (see [08_known_limits.md](08_known_limits.md#ft-savepoint)).

## Read-Only Transactions {#read-only}

Read-only transactions acquire a snapshot but do not write to the WAL or acquire write locks.
They use `IsolationLevel.SnapshotIsolation` with `readOnly: true`.
