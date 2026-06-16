# Full-Text Search

> as-built specification (v1 baseline)

## Architecture {#architecture}

Each full-text index consists of two B+Trees sharing a container tenant:

| B+Tree | Key | Value | Purpose |
|---|---|---|---|
| **Postings** | `(term, entityId)` composite | `tf` (uint16, saturated) | Inverted index |
| **Norms** | `entityId` (int64) | `docLen` (document length) | BM25 length normalization |

## Tokenizer {#tokenizer}

`MixedBigramTokenizer` (default, id = `mixed-bigram-v1`):
- CJK characters: bigram decomposition
- Latin/ASCII: whitespace-delimited, lowercased
- Mixed: seamless transition between CJK bigrams and Latin tokens

## Postings Key Encoding {#postings-key}

`PostingsKey.Encode(termUtf8, entityId)` produces a byte[] key for the B+Tree:
- Term bytes (variable length) followed by entity ID (int64 big-endian)
- `PostingsKey.TermRange(termUtf8)` returns `(lower, upper)` bounds for prefix scan
  over all entities matching a term

## BM25 Scoring {#bm25}

Okapi BM25 with standard parameters:

| Parameter | Value |
|---|---|
| k1 | 1.2 |
| b | 0.75 |

### Formulas {#bm25-formulas}

```
IDF(term) = log(1 + (N - df + 0.5) / (df + 0.5))

Score(term, doc) = IDF × (tf × (k1 + 1)) / (tf + k1 × (1 - b + b × docLen / avgdl))
```

Where:
- `N` = total document count
- `df` = document frequency (number of docs containing the term)
- `tf` = term frequency in the document
- `docLen` = document length (from norms)
- `avgdl` = average document length

### Corpus Statistics {#corpus-stats}

`Bm25CorpusStats`: DocumentCount, AverageDocLength, per-term stats (optional).
`Bm25TermStats`: `{term -> (df, maxTf)}`, MinDocLen.

## WAND Top-K {#wand}

`Bm25Scorer.RankWand()` implements the Weighted AND (WAND) algorithm for efficient
top-k retrieval:

1. For each query term, compute an **upper bound** on its contribution:
   `ub = IDF × (maxTf × (k1 + 1)) / (maxTf + k1 × normMin)`
2. Sort term cursors by current document ID
3. **Pivot**: find the first document where cumulative upper bounds of aligned
   cursors exceed `theta` (the k-th best score seen so far)
4. If a full evaluation of the pivot document yields a score > theta, insert
   into the top-k heap
5. Advance lagging cursors via `SeekTo` to skip non-candidate documents

## Reciprocal Rank Fusion (RRF) {#rrf}

When combining full-text and vector search results (hybrid search), RRF merges
ranked lists:

```
RRF_score(doc) = sum(1 / (k + rank_i(doc)))
```

where `k` is a smoothing constant (default 60) and `rank_i` is the document's
position in result list `i`.

## Logical WAL {#logical-wal}

Full-text postings and norms use **logical WAL records** (`FtLeafMutation`) instead
of page-image logging for leaf updates. This dramatically reduces WAL amplification
for high-volume text ingestion.

### Record Types {#ft-wal-records}

| WAL Type | Value | Purpose |
|---|---|---|
| `FtLeafMutation` | 17 | State-setting leaf mutation (Upsert or Delete) |
| `FtStructureImage` | 18 | Structure page (split/merge/root) after-image |

### Journaling Modes {#ft-journaling}

| Mode | Leaf | Structure (SMO) |
|---|---|---|
| **Suppressed** | No page-image; FtLeafMutation only | N/A |
| **RedoOnly** | N/A | Eager PageImage, nested top action (no undo) |
| **Full** | Standard page-image + CLR | Standard page-image + CLR |

Leaf updates use Suppressed mode: only FtLeafMutation records are logged.
Structure modifications (split, merge, root changes) use RedoOnly mode: after-images
are written as `FtStructureImage` records, which are unconditionally redone during
recovery and never undone (nested top action semantics).

### Recovery {#ft-recovery}

During recovery:
- **Pass 2a**: `FtStructureImage` records are unconditionally redone (nested top action)
- **Pass 2b**: `FtLeafMutation` records of committed transactions are redone via
  `ApplyFtLeafRedo` (state-setting, idempotent)
- **Pass 3**: `FtLeafMutation` records of loser transactions are undone via
  inverse operation (Upsert becomes Delete, Delete becomes Upsert with saved value)

### WAL Amplification {#wal-amplification}

Logical WAL reduces postings WAL amplification from ~50-100x (page-image per leaf touch)
to ~5x (logical mutation per posting). Measured at batch=10: 4.70x.
