using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Quiver.Core;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.PropertyTests;

/// <summary>
/// TS-3 Property 1: WAL の append/read round-trip と replay 冪等性。
///
/// 検証する不変量:
///   (a) ランダムな (txId, type, payload) 列を append → read で完全復元できる
///   (b) ランダム列を 2 回連続で read しても同じ結果になる (reader idempotency)
///   (c) FlushedLsn は append された LSN を後追いで超えない
/// </summary>
public class WalReplayProperties
{
    /// <summary>
    /// FsCheck の <see cref="Gen"/> で「実際に WAL に書ける」レコードだけを生成する。
    /// EndOfSegment / CheckpointBegin/End は payload 形式に制約があるため除外し、
    /// payload は 0..512 バイトの任意バイト列に絞る。
    /// </summary>
    public sealed record WalOp(byte TypeRaw, long TxId, byte[] Payload)
    {
        public WalRecordType Type => (WalRecordType)TypeRaw;
    }

    public static Arbitrary<WalOp> WalOpArb()
    {
        byte[] safeTypes = [
            (byte)WalRecordType.Begin,
            (byte)WalRecordType.Commit,
            (byte)WalRecordType.Abort,
            (byte)WalRecordType.PageDelta,
            (byte)WalRecordType.IndexMutation,
        ];
        var typeGen = Gen.Elements(safeTypes);
        var txGen = ArbMap.Default.GeneratorFor<long>()
            .Select(v => Math.Abs(v % 100000L) + 1);
        var payloadGen = Gen.Sized(s =>
        {
            int len = Math.Min(Math.Max(s, 0), 64);
            return ArbMap.Default.GeneratorFor<byte>()
                .ListOf(len)
                .Select(l => l.ToArray());
        });
        return typeGen
            .SelectMany(t => txGen.SelectMany(tx => payloadGen.Select(p => new WalOp(t, tx, p))))
            .ToArbitrary();
    }

    [Property(MaxTest = 200, Arbitrary = [typeof(WalReplayProperties)])]
    public Property AppendThenRead_PreservesAllRecordsInOrder(WalOp[] ops)
    {
        return Prop.ForAll(Gen.Constant(ops).ToArbitrary(), seq =>
        {
            if (seq.Length == 0) return true;
            string dir = Path.Combine(Path.GetTempPath(), "qpt_wal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                long[] lsns = new long[seq.Length];
                using (var wal = new WriteAheadLog(dir))
                {
                    for (int i = 0; i < seq.Length; i++)
                    {
                        var op = seq[i];
                        lsns[i] = wal.Append(op.Type, new TransactionId(op.TxId), op.Payload);
                    }
                    wal.FlushTo(lsns[^1]);
                    wal.FlushedLsn.Should().BeGreaterThanOrEqualTo(lsns[^1]);
                }
                // 別 instance で読み直して完全復元できることを確認 (cross-process durability)。
                using (var wal2 = new WriteAheadLog(dir))
                using (var reader = wal2.OpenReader(0))
                {
                    for (int i = 0; i < seq.Length; i++)
                    {
                        if (!reader.TryReadNext(out var rec)) return false;
                        if (rec.Type != seq[i].Type) return false;
                        if (rec.TransactionId.Value != seq[i].TxId) return false;
                        if (rec.Lsn != lsns[i]) return false;
                        if (!rec.Payload.Span.SequenceEqual(seq[i].Payload)) return false;
                    }
                    if (reader.TryReadNext(out _)) return false;
                }
                return true;
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });
    }

    [Property(MaxTest = 100, Arbitrary = [typeof(WalReplayProperties)])]
    public Property ReReadingSameWal_ProducesSameSequence(WalOp[] ops)
    {
        return Prop.ForAll(Gen.Constant(ops).ToArbitrary(), seq =>
        {
            if (seq.Length == 0) return true;
            string dir = Path.Combine(Path.GetTempPath(), "qpt_wal_idem_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using var wal = new WriteAheadLog(dir);
                long lastLsn = -1;
                foreach (var op in seq)
                    lastLsn = wal.Append(op.Type, new TransactionId(op.TxId), op.Payload);
                wal.FlushTo(lastLsn);

                // 2 回連続で読み直しても完全に同じ payload 列が観測できる (replay 冪等性の最小単位)。
                var first = ReadAll(wal);
                var second = ReadAll(wal);
                if (first.Count != second.Count) return false;
                for (int i = 0; i < first.Count; i++)
                {
                    if (first[i].Lsn != second[i].Lsn) return false;
                    if (first[i].Type != second[i].Type) return false;
                    if (first[i].TxId != second[i].TxId) return false;
                    if (!first[i].Payload.SequenceEqual(second[i].Payload)) return false;
                }
                return true;
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });
    }

    private static List<(long Lsn, WalRecordType Type, long TxId, byte[] Payload)> ReadAll(WriteAheadLog wal)
    {
        var result = new List<(long, WalRecordType, long, byte[])>();
        using var reader = wal.OpenReader(0);
        while (reader.TryReadNext(out var rec))
            result.Add((rec.Lsn, rec.Type, rec.TransactionId.Value, rec.Payload.ToArray()));
        return result;
    }

    [Property(MaxTest = 100, Arbitrary = [typeof(WalReplayProperties)])]
    public Property FlushedLsn_NeverExceedsCurrentLsn(WalOp[] ops)
    {
        return Prop.ForAll(Gen.Constant(ops).ToArbitrary(), seq =>
        {
            if (seq.Length == 0) return true;
            string dir = Path.Combine(Path.GetTempPath(), "qpt_wal_flush_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using var wal = new WriteAheadLog(dir);
                foreach (var op in seq)
                {
                    long lsn = wal.Append(op.Type, new TransactionId(op.TxId), op.Payload);
                    if (wal.FlushedLsn > wal.CurrentLsn) return false;
                    if (lsn > wal.CurrentLsn) return false;
                }
                wal.FlushTo(wal.CurrentLsn);
                if (wal.FlushedLsn < wal.CurrentLsn) return false;
                return true;
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });
    }
}
