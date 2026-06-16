# Known Limits

> as-built specification (v1 baseline)

This documents the engine's current known limitations. Defects found and fixed during the
v1 consolidation audit are not tracked here — they are covered by regression tests and git
history.

## Concurrency, Threading & Safe Use {#concurrency}

The engine runs **one write transaction at a time alongside any number of concurrent readers**.
The rules below are the supported contract; following them keeps the database correct and
crash-safe.

### Process & threading rules {#threading}

- **One process per database.** A `*.quiver` file is opened with an exclusive OS file lock
  (`FileShare.None`); a second process cannot open it. There is no multi-process or networked
  access — put your own service in front if you need that.
- **Share one `GraphDatabase` per database, across threads.** The instance is thread-safe; open
  it once and reuse it for the process lifetime. Do not open the same file twice in one process.
- **A transaction is single-threaded and thread-affine.** A transaction — and any cursor or
  enumerator obtained from it — must be created and used entirely on one thread. Its write and
  MVCC contexts are thread-local (`[ThreadStatic]`), so handing a live transaction to another
  thread (via `Task.Run`, an `await` continuation that resumes on a different thread,
  `Parallel.For`, etc.) is unsupported and can silently skip WAL logging. Begin, use, and
  commit/dispose a transaction in one synchronous scope on one thread; do not `await` between
  `Begin` and `Commit`.

### One writer; readers are concurrent {#one-writer}

- **Only one write transaction may be in flight at a time.** The engine does *not* reject a
  second concurrent writer at `BeginTransaction()` — it is the application's responsibility to
  serialize writes (see [serializing writes](#write-serialization)). All secondary-index and
  full-text mutations are additionally funnelled through a single global index lock, so no two
  transactions ever mutate an index / postings concurrently regardless of locking mode.
- **Readers never block, and are never blocked.** `BeginReadOnlyTransaction()` takes a
  consistent committed snapshot as of its start (snapshot isolation) and, by default, acquires
  no locks. Any number of readers run in parallel, on their own threads, concurrently with the
  single writer. A reader does not observe writes committed after it began — open a new reader
  to see newer state.

### Keep transactions short {#short-transactions}

Checkpointing, `Vacuum()`, and WAL truncation run only when **no** transaction is active
(`ActiveCount == 0`), and the oldest open transaction pins the WAL truncation horizon. A
transaction left open — **read or write** — therefore blocks WAL truncation and space
reclamation, so the WAL file grows for as long as it is held. Open a transaction, do the work,
then commit or dispose promptly. Never hold a transaction open across user think-time, UI
events, or network calls.

### Commit & durability {#commit-durability}

`Commit()` returns only after the WAL has been fsync'd; once it returns, the data survives a
process kill or power loss (recovery replays it on reopen — see
[02_wal_recovery.md](02_wal_recovery.md)). A transaction disposed without `Commit()` (including
when an exception unwinds the `using` scope) is rolled back. Partly-applied transactions are
never visible.

### Retrying on contention {#retry}

If you do drive writes from several threads, lock contention aborts the losing transaction with
`DeadlockException`, `TransactionException` (lock-wait timeout), or — under
`IsolationLevel.Serializable` — `SerializabilityException`. These are *transient*: the aborted
transaction made no durable change, so retry the **whole** transaction (never a partial one)
with a small bounded backoff:

```csharp
T WithRetry<T>(Func<T> runTxn, int maxAttempts = 5)
{
    for (int attempt = 1; ; attempt++)
    {
        try { return runTxn(); }
        catch (Exception e) when (
            e is DeadlockException or TransactionException or SerializabilityException
            && attempt < maxAttempts)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(2 * attempt)); // back off, retry whole txn
        }
    }
}
```

### Serializing writes (recommended) {#write-serialization}

Because the supported shape is single-writer, funnel all writes through one writer. Two patterns:

1. **A write gate** — guard every write transaction with a `SemaphoreSlim(1, 1)` (or a `lock`):

   ```csharp
   private static readonly SemaphoreSlim WriteGate = new(1, 1);

   async Task WriteAsync(Action<IGraphTransaction> work)
   {
       await WriteGate.WaitAsync();
       try
       {
           using var tx = db.BeginTransaction(); // begin + use + commit, all on this thread
           work(tx);
           tx.Commit();
       }
       finally { WriteGate.Release(); }
   }
   ```

2. **A dedicated writer thread** — push write jobs onto a queue (e.g.
   `System.Threading.Channels.Channel<T>`) drained by one background thread that begins,
   executes, and commits each transaction on that thread. This also gives natural batching.

Reads need no gate: open `BeginReadOnlyTransaction()` on any thread and run them concurrently
with each other and with the writer.

Supporting validated concurrent writers (and finer-grained index locking) is future work.

## BM25 corpus statistics are snapshot-based {#bm25-stats}

When a `GraphStats` snapshot is supplied to `Search`, BM25 scoring takes the corpus document
count `N`, average document length `avgdl`, and per-term `df` from that snapshot instead of
re-scanning the norms / postings on every query (the FTS-4 optimization). A snapshot reused
after the index changes is therefore approximate — `avgdl` / `df` may lag the live index and
shift BM25 scores slightly. This is an accepted tradeoff: BM25 is robust to corpus-stat
staleness, and `avgdl` scales every document's length normalization uniformly.

It affects scoring only. WAND top-k pruning uses a staleness-independent per-term upper bound
(`idf * (K1 + 1)`), so it never drops a document relative to the exact full scan, and both
query paths use the same snapshot basis, so they stay mutually consistent.

## HNSW Overwrite {#hnsw-overwrite}

`HnswIndex.Insert(seq)` for an existing sequence updates the vector payload but does
not re-link the HNSW graph topology. The old graph links remain, pointing to the new
vector. This may reduce search quality when vectors change significantly.

Mitigation: automatic rebuild when tombstone count exceeds live count.

## No Automatic Migration {#no-migration}

Opening a database with a different `FormatVersion` throws `FormatVersionMismatchException`.
There is no automatic migration path. Databases must be recreated from source data.

## In-Process Only {#in-process}

Quiver runs in the application process. There is no server mode, network protocol,
or inter-process access. The `*.quiver` file is opened with exclusive file lock
(`FileShare.None`).

## Checkpoint WAL Truncation {#wal-truncation}

WAL truncation only occurs after a complete checkpoint (Begin + End). If the application
runs for extended periods without a checkpoint (e.g., very large long-running transactions),
the WAL file grows unboundedly.
