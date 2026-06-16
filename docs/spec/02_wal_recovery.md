# WAL & Recovery

> as-built specification (v1 baseline)

## Write-Ahead Logging {#wal}

Quiver uses ARIES-style write-ahead logging for crash recovery. The WAL file (`*.quiver-wal`)
is a sequential log of variable-length records, each carrying a monotonic LSN.

### WAL Record Types {#record-types}

| Type | Value | Purpose |
|---|---|---|
| `Begin` | 1 | Transaction start |
| `Commit` | 2 | Transaction commit (durability boundary) |
| `Abort` | 3 | Explicit abort |
| `PageImage` | 10 | Full after-image of a page (redo) |
| `PageDelta` | 11 | Delta update to a page |
| `CompensationLogRecord` | 12 | Before-image for undo (CLR) |
| `IndexMutation` | 13 | Legacy logical B+Tree mutation (reserved, no longer emitted) |
| `CheckpointBegin` | 14 | Checkpoint start sentinel |
| `CheckpointEnd` | 15 | Checkpoint completion sentinel |
| `FileTruncate` | 16 | Vacuum page-file truncation |
| `FtLeafMutation` | 17 | Full-text postings/norms leaf logical mutation |
| `FtStructureImage` | 18 | Full-text B+Tree structure page after-image (nested top action) |
| `Checkpoint` | 100 | Legacy single-record checkpoint (read-only compat) |
| `EndOfSegment` | 0xFE | Segment boundary marker |

### Write-Ahead Guarantee {#write-ahead}

Before a dirty page is evicted from the buffer pool (STEAL), the WAL must contain at least the
CLR (CompensationLogRecord = before-image) for that page. On `UnpinDirty`, the after-image
(PageImage) is also logged. This ensures both redo and undo are possible after a crash.

### Presume-Committed {#presume-committed}

Quiver uses a **presume-committed** recovery strategy. A transaction is considered committed
if and only if a `Commit` record is found in the WAL. Transactions without a Commit record
are presumed to be losers and are undone.

## Two-Phase Recovery {#two-phase-recovery}

`RecoveryManager` implements recovery in two phases:

### Pass 1: Analysis + Redo {#pass-1-redo}

Scans the WAL from the most recent completed checkpoint (identified by matching
`CheckpointBegin`/`CheckpointEnd` pairs). For each record:

- **PageImage / PageDelta**: redo by writing the page image to the data file
- **FtStructureImage**: unconditionally redo (nested top action -- never undone)
- **FtLeafMutation (committed tx)**: redo via state-setting `ApplyFtLeafRedo`
- **Begin / Commit / Abort**: track transaction status
- **FileTruncate**: re-apply truncation idempotently

Winner (committed) transactions' page images are redone forward. This restores the
data file to at least the state at the last WAL flush.

### Pass 2: Undo (Loser Transactions) {#pass-2-undo}

For each transaction that has a `Begin` but no `Commit` or `Abort` (loser):

- **CLR (CompensationLogRecord)**: restore the before-image to the data file
- **FtLeafMutation**: apply inverse operation (undo) in LIFO order

Undo processes loser transactions' records in reverse LSN order to restore
the pre-transaction state.

## Checkpoint {#checkpoint}

`Checkpointer` (`src/Quiver/Transactions/Checkpointer.cs`) performs periodic checkpointing:

### Checkpoint Phases {#checkpoint-phases}

| Phase | Action |
|---|---|
| `AfterBegin` | Write `CheckpointBegin` to WAL |
| `AfterDataFlush` | Flush all dirty pages from buffer pool to data file (`PageManager.FlushAll`) |
| `AfterIndexFlush` | Flush all indexes (`IndexManager.FlushAll`) |
| `AfterEnd` | Write `CheckpointEnd` to WAL |
| `AfterTruncate` | Truncate WAL prefix (remove records before this checkpoint) |

### Atomicity {#checkpoint-atomicity}

A checkpoint is considered complete only when both `CheckpointBegin` and `CheckpointEnd`
are present. If a crash occurs mid-checkpoint (e.g., after data flush but before End),
recovery falls back to the previous completed checkpoint and replays from there. This
prevents partial-flush states from being treated as durable.

### Checkpoint Triggering {#checkpoint-trigger}

Checkpointing is triggered when WAL bytes written exceed `GraphDatabaseOptions.CheckpointThresholdBytes`
and no active transactions are in flight.

## LSN {#lsn}

The Log Sequence Number (LSN) is a monotonically increasing int64. Each page header stores
the LSN of its last modification. Recovery uses page LSN vs. WAL record LSN to determine
whether a redo is needed (skip if page LSN >= record LSN).
