using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// Clean-slate CSR relationship persistence spike.
/// The prototype writes explicit commit frames for base segments, locator sidecars,
/// delta records, and deletion bitmaps, then recovers only fully committed frame groups.
/// </summary>
public static class CleanSlateCsrPersistenceSpikeRunner
{
    private const double BaselinePointUpdateUs = 1163.80;
    private const double RequiredPointUpdateUs = BaselinePointUpdateUs * 3.0;

    public static int Run(IReadOnlyList<string> args)
    {
        int degree = Parse(args, 0, 100);
        int pointUpdates = Parse(args, 1, 160);
        int mergeDeltaCount = Parse(args, 2, 100_000);

        Console.WriteLine("=== Clean-slate CSR relationship persistence spike ===");
        Console.WriteLine(
            $"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine(
            $"degree={degree}, point_updates={pointUpdates}, merge_delta_count={mergeDeltaCount}");
        Console.WriteLine();

        string dir = BenchTempDir.Create("clean_slate_csr_persist");
        Directory.CreateDirectory(dir);
        try
        {
            var contractStore = CsrPrototypeStore.Create(Path.Combine(dir, "contract.bin"), degree);
            var crashStore = CsrPrototypeStore.Create(Path.Combine(dir, "crash.bin"), degree);
            var updateStore = CsrPrototypeStore.Create(Path.Combine(dir, "updates.bin"), degree);

            ContractResult contract = ValidateContracts(contractStore);
            CrashResult crash = ValidateCrashRecovery(dir, crashStore);
            UpdateResult updates = MeasurePointUpdates(updateStore, pointUpdates);
            MergeResult merge = MeasureMerge(Path.Combine(dir, "merge.bin"), degree, mergeDeltaCount);

            bool pointUpdatePass = updates.P50Us <= RequiredPointUpdateUs;
            bool contractPass = contract.Passed && crash.Passed;
            bool mergeGateMeasured = mergeDeltaCount >= 1_000_000;
            bool mergePass = !mergeGateMeasured || merge.ElapsedMs <= 2_000.0;
            bool pass = contractPass && pointUpdatePass && mergePass;

            Console.WriteLine(
                $"contracts, direct_lookup={contract.DirectLookup}, snapshot={contract.Snapshot}, " +
                $"delete={contract.Delete}, reuse={contract.Reuse}, merge={contract.Merge}, " +
                $"traversal={contract.Traversal}, result={(contract.Passed ? "PASS" : "FAIL")}");
            Console.WriteLine(
                $"crash_recovery, cases={crash.Cases}, passed={crash.PassedCases}, " +
                $"result={(crash.Passed ? "PASS" : "FAIL")}");
            Console.WriteLine(
                $"point_update_commit, p50_us={updates.P50Us:F2}, p95_us={updates.P95Us:F2}, " +
                $"required_us={RequiredPointUpdateUs:F2}, result={(pointUpdatePass ? "PASS" : "FAIL")}");
            Console.WriteLine(
                $"merge, delta_count={mergeDeltaCount}, elapsed_ms={merge.ElapsedMs:F2}, " +
                $"records_after_merge={merge.RecordCount}, " +
                $"result={(mergeGateMeasured ? (mergePass ? "PASS" : "FAIL") : "REFERENCE_ONLY")}");
            Console.WriteLine(
                "csv,csr_persistence,contract_pass,crash_pass,point_update_p50_us," +
                "point_update_p95_us,merge_delta_count,merge_ms,result");
            Console.WriteLine(
                $"csv,csr_persistence,{contract.Passed},{crash.Passed},{updates.P50Us:F2}," +
                $"{updates.P95Us:F2},{mergeDeltaCount},{merge.ElapsedMs:F2},{(pass ? "PASS" : "FAIL")}");

            return pass ? 0 : 2;
        }
        finally
        {
            BenchTempDir.Delete(dir);
        }
    }

    private static int Parse(IReadOnlyList<string> args, int index, int fallback)
        => index < args.Count && int.TryParse(args[index], out int value) && value > 0
            ? value
            : fallback;

    private static ContractResult ValidateContracts(CsrPrototypeStore store)
    {
        var first = new RelHandle(0, 0);
        bool direct = store.TryLookup(first, out var before) && before.Score == 0;

        var snapshot = store.Current.Clone();
        store.CommitUpdate(first.Sequence, 7);
        bool snapshotStable =
            snapshot.TryLookup(first, out var snapshotRecord) &&
            snapshotRecord.Score == 0 &&
            store.TryLookup(first, out var afterUpdate) &&
            afterUpdate.Score == 7;

        long deleteSeq = store.FirstSecondHopSequence;
        var deleteHandle = new RelHandle(deleteSeq, 0);
        store.CommitDelete(deleteSeq);
        bool delete =
            !store.TryLookup(deleteHandle, out _) &&
            !store.Current.Outgoing(deleteSeqSource: 1).Any(r => r.Sequence == deleteSeq);

        store.CommitReuse(deleteSeq, 1, source: 1, target: 10_000_000, score: 11);
        bool reuse =
            !store.TryLookup(deleteHandle, out _) &&
            store.TryLookup(new RelHandle(deleteSeq, 1), out var reused) &&
            reused.Score == 11;

        long insertedSeq = store.NextSequence;
        store.CommitInsert(source: 0, target: 20_000_000, score: 1);
        bool traversal = store.Current.Outgoing(deleteSeqSource: 0).Any(r => r.Sequence == insertedSeq);

        store.CommitMerge();
        bool merge =
            store.TryLookup(first, out var afterMerge) &&
            afterMerge.Score == 7 &&
            store.TryLookup(new RelHandle(deleteSeq, 1), out var reusedAfterMerge) &&
            reusedAfterMerge.Score == 11;

        return new ContractResult(direct, snapshotStable, delete, reuse, merge, traversal);
    }

    private static CrashResult ValidateCrashRecovery(string dir, CsrPrototypeStore baseline)
    {
        int passed = 0;
        int cases = 0;

        CheckCase(
            "partial-update-without-commit",
            frames =>
            {
                frames.RemoveAll(f => f.Part == FramePart.Commit);
            },
            recovered =>
                recovered.TryLookup(new RelHandle(1, 0), out var record) && record.Score == 1);

        CheckCase(
            "committed-update",
            _ => { },
            recovered =>
                recovered.TryLookup(new RelHandle(1, 0), out var record) && record.Score == 42);

        CheckMergeHalfCase();
        CheckDeleteMergeCase();

        return new CrashResult(cases, passed);

        void CheckCase(
            string name,
            Action<List<FrameWrite>> mutate,
            Func<CsrState, bool> assert)
        {
            cases++;
            string casePath = Path.Combine(dir, $"{name}.bin");
            File.Copy(baseline.Path, casePath, overwrite: true);
            var frames = CsrPrototypeStore.MakeUpdateFrames(baseline.Current, 1, 42, baseline.Current.CommitId + 1);
            mutate(frames);
            CsrPrototypeStore.AppendFrames(casePath, frames, flush: true);
            var recovered = CsrPrototypeStore.Recover(casePath);
            if (assert(recovered))
                passed++;
        }

        void CheckMergeHalfCase()
        {
            cases++;
            string casePath = Path.Combine(dir, "half-merge.bin");
            File.Copy(baseline.Path, casePath, overwrite: true);
            var mergeState = baseline.Current.Clone();
            mergeState.ApplyDelta(new RelRecord(baseline.NextSequence, 0, 0, 30_000_000, 1, false));
            mergeState.Locators[baseline.NextSequence] = Locator.LiveDelta(0);
            var frames = CsrPrototypeStore.MakeMergeFrames(mergeState, baseline.Current.CommitId + 1);
            frames.RemoveAll(f => f.Part == FramePart.BackwardBase);
            CsrPrototypeStore.AppendFrames(casePath, frames, flush: true);
            var recovered = CsrPrototypeStore.Recover(casePath);
            if (!recovered.TryLookup(new RelHandle(baseline.NextSequence, 0), out _))
                passed++;
        }

        void CheckDeleteMergeCase()
        {
            cases++;
            string casePath = Path.Combine(dir, "delete-then-merge.bin");
            File.Copy(baseline.Path, casePath, overwrite: true);
            var working = CsrPrototypeStore.Open(casePath);
            long seq = baseline.FirstSecondHopSequence + 1;
            working.CommitDelete(seq);
            working.CommitMerge();
            var recovered = CsrPrototypeStore.Recover(casePath);
            if (!recovered.TryLookup(new RelHandle(seq, 0), out _))
                passed++;
        }
    }

    private static UpdateResult MeasurePointUpdates(CsrPrototypeStore store, int pointUpdates)
    {
        long[] ticks = new long[pointUpdates];
        long sequence = 1;
        for (int i = 0; i < Math.Min(12, pointUpdates); i++)
            store.CommitUpdate(sequence, i);

        for (int i = 0; i < pointUpdates; i++)
        {
            long started = Stopwatch.GetTimestamp();
            store.CommitUpdate(sequence, i + 1000);
            ticks[i] = Stopwatch.GetTimestamp() - started;
        }

        return new UpdateResult(PercentileUs(ticks, 0.50), PercentileUs(ticks, 0.95));
    }

    private static MergeResult MeasureMerge(string baselinePath, int degree, int mergeDeltaCount)
    {
        string mergePath = baselinePath + ".merge";
        var store = CsrPrototypeStore.Create(mergePath, degree);
        for (int i = 0; i < mergeDeltaCount; i++)
        {
            long seq = store.NextSequence++;
            int source = i % Math.Max(1, degree);
            store.Current.ApplyDelta(new RelRecord(seq, 0, source, 40_000_000 + i, i & 1, false));
            store.Current.Locators[seq] = Locator.LiveDelta(0);
        }

        var sw = Stopwatch.StartNew();
        store.CommitMerge();
        sw.Stop();

        if (!store.TryLookup(new RelHandle(store.NextSequence - 1, 0), out _))
            throw new InvalidOperationException("Merged delta record was not visible after merge.");

        return new MergeResult(sw.Elapsed.TotalMilliseconds, store.Current.ForwardBase.Count);
    }

    private static double PercentileUs(long[] ticks, double p)
        => PercentileTicks(ticks, p) * 1_000_000.0 / Stopwatch.Frequency;

    private static long PercentileTicks(long[] ticks, double p)
    {
        var sorted = (long[])ticks.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private readonly record struct ContractResult(
        bool DirectLookup,
        bool Snapshot,
        bool Delete,
        bool Reuse,
        bool Merge,
        bool Traversal)
    {
        public bool Passed => DirectLookup && Snapshot && Delete && Reuse && Merge && Traversal;
    }

    private readonly record struct CrashResult(int Cases, int PassedCases)
    {
        public bool Passed => Cases == PassedCases;
    }

    private readonly record struct UpdateResult(double P50Us, double P95Us);

    private readonly record struct MergeResult(double ElapsedMs, int RecordCount);

    private readonly record struct RelHandle(long Sequence, int Generation);

    private readonly record struct RelRecord(
        long Sequence,
        int Generation,
        int Source,
        int Target,
        long Score,
        bool Overflow);

    private readonly record struct Locator(int Generation, bool Live, bool Delta, int Ordinal)
    {
        public static Locator LiveBase(int generation, int ordinal) => new(generation, true, false, ordinal);

        public static Locator LiveDelta(int generation) => new(generation, true, true, -1);

        public static Locator Deleted(int generation) => new(generation, false, false, -1);
    }

    private sealed class CsrState
    {
        public long CommitId { get; set; }

        public List<RelRecord> ForwardBase { get; } = [];

        public List<RelRecord> BackwardBase { get; } = [];

        public Dictionary<long, Locator> Locators { get; } = [];

        public Dictionary<long, RelRecord> Delta { get; } = [];

        public HashSet<long> Deleted { get; } = [];

        public CsrState Clone()
        {
            var clone = new CsrState { CommitId = CommitId };
            clone.ForwardBase.AddRange(ForwardBase);
            clone.BackwardBase.AddRange(BackwardBase);
            foreach (var pair in Locators)
                clone.Locators.Add(pair.Key, pair.Value);
            foreach (var pair in Delta)
                clone.Delta.Add(pair.Key, pair.Value);
            foreach (long seq in Deleted)
                clone.Deleted.Add(seq);
            return clone;
        }

        public bool TryLookup(RelHandle handle, out RelRecord record)
        {
            record = default;
            if (!Locators.TryGetValue(handle.Sequence, out var locator) ||
                locator.Generation != handle.Generation ||
                !locator.Live ||
                Deleted.Contains(handle.Sequence))
            {
                return false;
            }

            if (locator.Delta)
                return Delta.TryGetValue(handle.Sequence, out record);

            if ((uint)locator.Ordinal >= (uint)ForwardBase.Count)
                return false;

            record = ForwardBase[locator.Ordinal];
            return record.Sequence == handle.Sequence && record.Generation == handle.Generation;
        }

        public IEnumerable<RelRecord> Outgoing(int deleteSeqSource)
        {
            foreach (var record in ForwardBase)
            {
                if (record.Source == deleteSeqSource &&
                    !Deleted.Contains(record.Sequence) &&
                    Locators.TryGetValue(record.Sequence, out var locator) &&
                    locator.Live)
                {
                    yield return record;
                }
            }

            foreach (var record in Delta.Values)
            {
                if (record.Source == deleteSeqSource &&
                    !Deleted.Contains(record.Sequence) &&
                    Locators.TryGetValue(record.Sequence, out var locator) &&
                    locator.Live)
                {
                    yield return record;
                }
            }
        }

        public void ApplyBase(List<RelRecord> records)
        {
            ForwardBase.Clear();
            ForwardBase.AddRange(records.OrderBy(r => r.Source).ThenBy(r => r.Target).ThenBy(r => r.Sequence));
            BackwardBase.Clear();
            BackwardBase.AddRange(records.OrderBy(r => r.Target).ThenBy(r => r.Source).ThenBy(r => r.Sequence));
        }

        public void ApplyDelta(RelRecord record)
        {
            Delta[record.Sequence] = record;
            Deleted.Remove(record.Sequence);
        }
    }

    private enum FramePart
    {
        ForwardBase = 1,
        BackwardBase = 2,
        Locator = 4,
        Delta = 8,
        Deletion = 16,
        Commit = 32
    }

    private readonly record struct FrameWrite(long CommitId, FramePart Part, byte[] Payload);

    private sealed class PendingCommit
    {
        public long CommitId { get; }

        public Dictionary<FramePart, byte[]> Parts { get; } = [];

        public PendingCommit(long commitId) => CommitId = commitId;

        public void Add(FramePart part, byte[] payload) => Parts[part] = payload;

        public bool IsComplete(int requiredMask)
            => (Parts.Keys.Aggregate(0, (mask, part) => mask | (int)part) & requiredMask) == requiredMask;
    }

    private sealed class CsrPrototypeStore
    {
        private const int Magic = 0x51525343;
        private const int Version = 1;

        public string Path { get; }

        public CsrState Current { get; }

        public long FirstSecondHopSequence { get; }

        public long NextSequence { get; set; }

        private CsrPrototypeStore(string path, CsrState current, long firstSecondHopSequence, long nextSequence)
        {
            Path = path;
            Current = current;
            FirstSecondHopSequence = firstSecondHopSequence;
            NextSequence = nextSequence;
        }

        public static CsrPrototypeStore Create(string path, int degree)
        {
            var state = new CsrState();
            long seq = 0;
            long firstSecondHop = -1;
            var records = new List<RelRecord>();
            for (int mid = 0; mid < degree; mid++)
            {
                int midNode = 1 + mid;
                records.Add(new RelRecord(seq++, 0, 0, midNode, 0, false));
                for (int leaf = 0; leaf < degree; leaf++)
                {
                    if (firstSecondHop < 0)
                        firstSecondHop = seq;
                    int leafNode = 1 + degree + mid * degree + leaf;
                    long score = (mid + leaf) % 4 == 0 ? 1 : 0;
                    records.Add(new RelRecord(seq++, 0, midNode, leafNode, score, false));
                }
            }

            state.ApplyBase(records);
            for (int i = 0; i < state.ForwardBase.Count; i++)
            {
                var record = state.ForwardBase[i];
                state.Locators[record.Sequence] = Locator.LiveBase(record.Generation, i);
            }

            state.CommitId = 1;
            var frames = MakeFullSnapshotFrames(state, state.CommitId);
            if (File.Exists(path))
                File.Delete(path);
            AppendFrames(path, frames, flush: true);
            return new CsrPrototypeStore(path, state, firstSecondHop, seq);
        }

        public static CsrPrototypeStore Open(string path)
        {
            var recovered = Recover(path);
            long next = recovered.Locators.Count == 0 ? 0 : recovered.Locators.Keys.Max() + 1;
            long firstSecondHop = recovered.ForwardBase.FirstOrDefault(r => r.Source != 0).Sequence;
            return new CsrPrototypeStore(path, recovered, firstSecondHop, next);
        }

        public static CsrState Recover(string path)
        {
            var state = new CsrState();
            PendingCommit? pending = null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            while (TryReadFrame(stream, out long commitId, out FramePart part, out byte[] payload))
            {
                if (pending is null || pending.CommitId != commitId)
                    pending = new PendingCommit(commitId);

                pending.Add(part, payload);
                if (part == FramePart.Commit)
                {
                    int requiredMask = ReadRequiredMask(payload);
                    if (pending.IsComplete(requiredMask))
                        ApplyPending(state, pending, commitId);
                    pending = null;
                }
            }

            return state;
        }

        public bool TryLookup(RelHandle handle, out RelRecord record)
            => Current.TryLookup(handle, out record);

        public void CommitUpdate(long sequence, long score)
        {
            if (!Current.TryLookup(new RelHandle(sequence, Current.Locators[sequence].Generation), out var before))
                throw new InvalidOperationException("Cannot update a missing relationship.");

            var frames = MakeUpdateFrames(Current, sequence, score, Current.CommitId + 1);
            AppendFrames(Path, frames, flush: true);
            var updated = before with { Score = score };
            Current.ApplyDelta(updated);
            Current.Locators[sequence] = Locator.LiveDelta(updated.Generation);
            Current.CommitId++;
        }

        public void CommitDelete(long sequence)
        {
            if (!Current.Locators.TryGetValue(sequence, out var locator))
                throw new InvalidOperationException("Cannot delete a missing relationship.");

            long commitId = Current.CommitId + 1;
            var frames = new List<FrameWrite>
            {
                new(commitId, FramePart.Deletion, SerializeDeleted([sequence])),
                new(commitId, FramePart.Locator, SerializeLocators([(sequence, Locator.Deleted(locator.Generation))])),
                new(commitId, FramePart.Commit, SerializeCommit((int)FramePart.Deletion | (int)FramePart.Locator))
            };
            AppendFrames(Path, frames, flush: true);
            Current.Deleted.Add(sequence);
            Current.Delta.Remove(sequence);
            Current.Locators[sequence] = Locator.Deleted(locator.Generation);
            Current.CommitId = commitId;
        }

        public void CommitReuse(long sequence, int generation, int source, int target, long score)
        {
            CommitDeltaRecord(new RelRecord(sequence, generation, source, target, score, false));
        }

        public void CommitInsert(int source, int target, long score)
        {
            CommitDeltaRecord(new RelRecord(NextSequence++, 0, source, target, score, false));
        }

        public void CommitMerge()
        {
            long commitId = Current.CommitId + 1;
            var frames = MakeMergeFrames(Current, commitId);
            AppendFrames(Path, frames, flush: true);
            ApplyPending(Current, FramesToPending(frames, commitId), commitId);
        }

        public static List<FrameWrite> MakeUpdateFrames(
            CsrState state,
            long sequence,
            long score,
            long commitId)
        {
            if (!state.TryLookup(new RelHandle(sequence, state.Locators[sequence].Generation), out var before))
                throw new InvalidOperationException("Cannot create update frames for a missing relationship.");

            var updated = before with { Score = score };
            return
            [
                new(commitId, FramePart.Delta, SerializeRecords([updated])),
                new(commitId, FramePart.Locator, SerializeLocators([(sequence, Locator.LiveDelta(updated.Generation))])),
                new(commitId, FramePart.Commit, SerializeCommit((int)FramePart.Delta | (int)FramePart.Locator))
            ];
        }

        public static List<FrameWrite> MakeMergeFrames(CsrState state, long commitId)
        {
            var live = new Dictionary<long, RelRecord>();
            foreach (var record in state.ForwardBase)
            {
                if (!state.Deleted.Contains(record.Sequence) &&
                    state.Locators.TryGetValue(record.Sequence, out var locator) &&
                    locator.Live)
                {
                    live[record.Sequence] = record;
                }
            }

            foreach (var record in state.Delta.Values)
            {
                if (!state.Deleted.Contains(record.Sequence) &&
                    state.Locators.TryGetValue(record.Sequence, out var locator) &&
                    locator.Live)
                {
                    live[record.Sequence] = record;
                }
            }

            var merged = live.Values.OrderBy(r => r.Source).ThenBy(r => r.Target).ThenBy(r => r.Sequence).ToList();
            var locators = new List<(long, Locator)>();
            for (int i = 0; i < merged.Count; i++)
                locators.Add((merged[i].Sequence, Locator.LiveBase(merged[i].Generation, i)));
            foreach (var pair in state.Locators)
            {
                if (!live.ContainsKey(pair.Key))
                    locators.Add((pair.Key, Locator.Deleted(pair.Value.Generation)));
            }

            int required =
                (int)FramePart.ForwardBase |
                (int)FramePart.BackwardBase |
                (int)FramePart.Locator |
                (int)FramePart.Delta |
                (int)FramePart.Deletion;

            return
            [
                new(commitId, FramePart.ForwardBase, SerializeRecords(merged)),
                new(commitId, FramePart.BackwardBase, SerializeRecords(merged.OrderBy(r => r.Target).ThenBy(r => r.Source).ThenBy(r => r.Sequence))),
                new(commitId, FramePart.Locator, SerializeLocators(locators)),
                new(commitId, FramePart.Delta, SerializeRecords([])),
                new(commitId, FramePart.Deletion, SerializeDeleted([])),
                new(commitId, FramePart.Commit, SerializeCommit(required))
            ];
        }

        public static void AppendFrames(string path, IEnumerable<FrameWrite> frames, bool flush)
        {
            using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough);
            foreach (var frame in frames)
                WriteFrame(stream, frame);
            if (flush)
                stream.Flush(flushToDisk: true);
        }

        private static List<FrameWrite> MakeFullSnapshotFrames(CsrState state, long commitId)
        {
            int required =
                (int)FramePart.ForwardBase |
                (int)FramePart.BackwardBase |
                (int)FramePart.Locator |
                (int)FramePart.Delta |
                (int)FramePart.Deletion;

            return
            [
                new(commitId, FramePart.ForwardBase, SerializeRecords(state.ForwardBase)),
                new(commitId, FramePart.BackwardBase, SerializeRecords(state.BackwardBase)),
                new(commitId, FramePart.Locator, SerializeLocators(state.Locators.Select(p => (p.Key, p.Value)))),
                new(commitId, FramePart.Delta, SerializeRecords(state.Delta.Values)),
                new(commitId, FramePart.Deletion, SerializeDeleted(state.Deleted)),
                new(commitId, FramePart.Commit, SerializeCommit(required))
            ];
        }

        private void CommitDeltaRecord(RelRecord record)
        {
            long commitId = Current.CommitId + 1;
            var frames = new List<FrameWrite>
            {
                new(commitId, FramePart.Delta, SerializeRecords([record])),
                new(commitId, FramePart.Locator, SerializeLocators([(record.Sequence, Locator.LiveDelta(record.Generation))])),
                new(commitId, FramePart.Commit, SerializeCommit((int)FramePart.Delta | (int)FramePart.Locator))
            };
            AppendFrames(Path, frames, flush: true);
            Current.ApplyDelta(record);
            Current.Locators[record.Sequence] = Locator.LiveDelta(record.Generation);
            Current.CommitId = commitId;
        }

        private static PendingCommit FramesToPending(IEnumerable<FrameWrite> frames, long commitId)
        {
            var pending = new PendingCommit(commitId);
            foreach (var frame in frames)
                pending.Add(frame.Part, frame.Payload);
            return pending;
        }

        private static void ApplyPending(CsrState state, PendingCommit pending, long commitId)
        {
            if (pending.Parts.TryGetValue(FramePart.ForwardBase, out var forward))
                state.ForwardBase.ReplaceWith(ReadRecords(forward));
            if (pending.Parts.TryGetValue(FramePart.BackwardBase, out var backward))
                state.BackwardBase.ReplaceWith(ReadRecords(backward));
            if (pending.Parts.TryGetValue(FramePart.Delta, out var delta))
            {
                var records = ReadRecords(delta);
                if (records.Count == 0 && pending.Parts.ContainsKey(FramePart.ForwardBase))
                    state.Delta.Clear();
                foreach (var record in records)
                    state.ApplyDelta(record);
            }
            if (pending.Parts.TryGetValue(FramePart.Deletion, out var deletion))
            {
                var deleted = ReadDeleted(deletion);
                if (deleted.Count == 0 && pending.Parts.ContainsKey(FramePart.ForwardBase))
                    state.Deleted.Clear();
                foreach (long seq in deleted)
                    state.Deleted.Add(seq);
            }
            if (pending.Parts.TryGetValue(FramePart.Locator, out var locatorPayload))
            {
                foreach (var (seq, locator) in ReadLocators(locatorPayload))
                    state.Locators[seq] = locator;
            }

            state.CommitId = commitId;
        }

        private static void WriteFrame(Stream stream, FrameWrite frame)
        {
            uint checksum = Checksum(frame.CommitId, frame.Part, frame.Payload);
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(frame.CommitId);
            writer.Write((int)frame.Part);
            writer.Write(frame.Payload.Length);
            writer.Write(checksum);
            writer.Write(frame.Payload);
        }

        private static bool TryReadFrame(
            Stream stream,
            out long commitId,
            out FramePart part,
            out byte[] payload)
        {
            commitId = 0;
            part = default;
            payload = [];
            const int HeaderSize = sizeof(int) + sizeof(int) + sizeof(long) + sizeof(int) + sizeof(int) + sizeof(uint);
            if (stream.Length - stream.Position < HeaderSize)
                return false;

            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int magic = reader.ReadInt32();
            int version = reader.ReadInt32();
            commitId = reader.ReadInt64();
            part = (FramePart)reader.ReadInt32();
            int length = reader.ReadInt32();
            uint checksum = reader.ReadUInt32();
            if (magic != Magic || version != Version || length < 0 || stream.Length - stream.Position < length)
                return false;

            payload = reader.ReadBytes(length);
            return checksum == Checksum(commitId, part, payload);
        }

        private static byte[] SerializeRecords(IEnumerable<RelRecord> records)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            var list = records as ICollection<RelRecord> ?? records.ToArray();
            writer.Write(list.Count);
            foreach (var record in list)
            {
                writer.Write(record.Sequence);
                writer.Write(record.Generation);
                writer.Write(record.Source);
                writer.Write(record.Target);
                writer.Write(record.Score);
                writer.Write(record.Overflow);
            }
            return ms.ToArray();
        }

        private static List<RelRecord> ReadRecords(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms);
            int count = reader.ReadInt32();
            var records = new List<RelRecord>(count);
            for (int i = 0; i < count; i++)
            {
                records.Add(new RelRecord(
                    reader.ReadInt64(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt64(),
                    reader.ReadBoolean()));
            }
            return records;
        }

        private static byte[] SerializeLocators(IEnumerable<(long Sequence, Locator Locator)> locators)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            var list = locators as ICollection<(long, Locator)> ?? locators.ToArray();
            writer.Write(list.Count);
            foreach (var (sequence, locator) in list)
            {
                writer.Write(sequence);
                writer.Write(locator.Generation);
                writer.Write(locator.Live);
                writer.Write(locator.Delta);
                writer.Write(locator.Ordinal);
            }
            return ms.ToArray();
        }

        private static List<(long Sequence, Locator Locator)> ReadLocators(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms);
            int count = reader.ReadInt32();
            var locators = new List<(long, Locator)>(count);
            for (int i = 0; i < count; i++)
            {
                locators.Add((
                    reader.ReadInt64(),
                    new Locator(
                        reader.ReadInt32(),
                        reader.ReadBoolean(),
                        reader.ReadBoolean(),
                        reader.ReadInt32())));
            }
            return locators;
        }

        private static byte[] SerializeDeleted(IEnumerable<long> deleted)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            var list = deleted as ICollection<long> ?? deleted.ToArray();
            writer.Write(list.Count);
            foreach (long seq in list)
                writer.Write(seq);
            return ms.ToArray();
        }

        private static HashSet<long> ReadDeleted(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms);
            int count = reader.ReadInt32();
            var deleted = new HashSet<long>();
            for (int i = 0; i < count; i++)
                deleted.Add(reader.ReadInt64());
            return deleted;
        }

        private static byte[] SerializeCommit(int requiredMask)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(requiredMask);
            return ms.ToArray();
        }

        private static int ReadRequiredMask(byte[] payload)
        {
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms);
            return reader.ReadInt32();
        }

        private static uint Checksum(long commitId, FramePart part, byte[] payload)
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            void Add(byte b) => hash = (hash ^ b) * prime;

            for (int i = 0; i < sizeof(long); i++)
                Add((byte)(commitId >> (i * 8)));
            int partValue = (int)part;
            for (int i = 0; i < sizeof(int); i++)
                Add((byte)(partValue >> (i * 8)));
            foreach (byte b in payload)
                Add(b);
            return hash;
        }
    }
}

file static class ListExtensions
{
    public static void ReplaceWith<T>(this List<T> list, IEnumerable<T> values)
    {
        list.Clear();
        list.AddRange(values);
    }
}
