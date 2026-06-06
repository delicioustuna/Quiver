using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>
/// ARCH-5c Phase 5 (5a): 1 つの scalar key に対する **永続 MVCC 列セグメント**。spike
/// (`MvccScalarColumn`) で 3 gating kill criteria を満たした設計を production 化したもの。
/// 指示書 `plans/arch5c-phase5-columnar-impl.md`。
///
/// <para><b>レイアウト</b>:</para>
/// <list type="bullet">
///   <item><b>head ページ</b> (永続, container テナント): seq 直接 index の dense レコード
///     <c>[value:8 | xmin:8 | xmax:8]</c> = 24B/entry。340 entry/page。durability の正本で、
///     graph tx 内の書き込みは同 container の WAL に乗り commit fsync を共有する (write-amp ~1.0×)。</item>
///   <item><b>in-memory cache</b>: open 時に head ページから dense 配列へ load する read 加速層。
///     projection の逐次走査はこの配列を舐めるので per-entry の page pin が無い (~325× の源)。</item>
///   <item><b>delta</b> (超過版): overwrite で旧 head を退避する版チェーン。5a では in-memory
///     (Dictionary)。**delta の永続化は 5c (write 経路統合) で追加**する — 5a は write 未配線で
///     head 永続 + cache rebuild + MVCC read/merge ロジックの基盤を確立する段。</item>
/// </list>
///
/// <para>可視性は inline 経路と同一の <see cref="Visibility.IsVisible"/> を使う (apples-to-apples)。</para>
/// </summary>
internal sealed class ScalarColumnStore
{
    private const int EntrySize = 24;       // value8 + xmin8 + xmax8
    private const int OffValue = 0;
    private const int OffXmin = 8;
    private const int OffXmax = 16;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaHwm = 0;            // i64 (採番済み seq 数)
    private const int MetaValueType = 9;      // byte: 0=未設定 / 255=mixed / else (byte)PropertyValueType
    private const int MetaFormatVersion = 31; // byte
    private const byte ValueTypeUnset = 0;
    private const byte ValueTypeMixed = 255;
    private static int EntriesPerPage => RecordPageMapping.PageBodySize / EntrySize; // 340

    private readonly IPagedFile _file;
    private long _hwm;
    // Phase 5d: 列の scalar 型 (集約で raw bits を解釈するのに必要)。homogeneous 前提で、
    // 異なる scalar 型が来たら mixed (255) に倒し、集約は row path フォールバックさせる。
    private byte _valueType;

    // read 加速 cache (head の写し)。open 時に head ページから rebuild。
    private long[] _value;
    private long[] _xmin;
    private long[] _xmax;
    // 超過版 delta (5a: in-memory)。
    private readonly Dictionary<long, List<(long Val, long Xmin, long Xmax)>> _delta = new();

    public ScalarColumnStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _hwm = 0;
            _valueType = ValueTypeUnset;
            SaveMeta(initialise: true);
            _value = new long[16];
            _xmin = new long[16];
            _xmax = new long[16];
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
            _value = new long[Math.Max(16, _hwm)];
            _xmin = new long[Math.Max(16, _hwm)];
            _xmax = new long[Math.Max(16, _hwm)];
            RebuildCacheFromPages();
        }
    }

    /// <summary>採番済み seq 数 (= 最大 seq + 1)。</summary>
    public long Hwm => _hwm;

    /// <summary>列の scalar 型 (未設定なら <see cref="PropertyValueType.Null"/>)。</summary>
    public PropertyValueType ValueType => _valueType is ValueTypeUnset or ValueTypeMixed
        ? default
        : (PropertyValueType)_valueType;

    /// <summary>複数の scalar 型が混在した列か (集約は row path フォールバックすべき)。</summary>
    public bool IsMixed => _valueType == ValueTypeMixed;

    /// <summary>delta に積まれた超過版の総数 (compaction 計測 / テスト用)。</summary>
    public int DeltaVersionCount
    {
        get { int n = 0; foreach (var kv in _delta) n += kv.Value.Count; return n; }
    }

    /// <summary>
    /// seq の値を txId で set/上書きする。旧 head (live) は xmax=txId を立てて delta へ退避する。
    /// head ページ (永続) + cache を更新する。
    /// </summary>
    public void Set(long seq, long value, long txId, PropertyValueType valueType = PropertyValueType.Int64)
    {
        EnsureCapacity(seq);
        if (_xmin[seq] != 0 && _xmax[seq] == 0)
        {
            // 既存 live head を delta へ退避 (旧 xmax = txId)。
            if (!_delta.TryGetValue(seq, out var list))
                _delta[seq] = list = new List<(long, long, long)>();
            list.Add((_value[seq], _xmin[seq], txId));
        }
        _value[seq] = value;
        _xmin[seq] = txId;
        _xmax[seq] = 0;
        bool metaDirty = false;
        if (seq >= _hwm) { _hwm = seq + 1; metaDirty = true; }
        // Phase 5d: scalar 型を記録 (homogeneous 前提)。異種が来たら mixed に倒す。
        if (_valueType == ValueTypeUnset) { _valueType = (byte)valueType; metaDirty = true; }
        else if (_valueType != ValueTypeMixed && _valueType != (byte)valueType) { _valueType = ValueTypeMixed; metaDirty = true; }
        if (metaDirty) SaveMeta();
        WriteHeadPage(seq, value, txId, 0);
    }

    /// <summary>論理削除: head に xmax を立てる (cache + head ページ)。</summary>
    public void Delete(long seq, long txId)
    {
        if (seq < 0 || seq >= _hwm || _xmin[seq] == 0 || _xmax[seq] != 0) return;
        _xmax[seq] = txId;
        WriteHeadPage(seq, _value[seq], _xmin[seq], txId);
    }

    /// <summary>捕捉 snapshot から見える値を引く (head→delta の順で最初の可視版)。</summary>
    public bool TryGet(long seq, in SnapshotState snap, TransactionId self, CommittedTxRegistry committed, out long value)
    {
        value = 0;
        if (seq < 0 || seq >= _hwm || _xmin[seq] == 0) return false;
        if (Visibility.IsVisible(_xmin[seq], _xmax[seq], in snap, self, committed)) { value = _value[seq]; return true; }
        if (_delta.TryGetValue(seq, out var list))
            for (int i = list.Count - 1; i >= 0; i--)
                if (Visibility.IsVisible(list[i].Xmin, list[i].Xmax, in snap, self, committed)) { value = list[i].Val; return true; }
        return false;
    }

    /// <summary>可視な全エントリの値を合計する (projection スキャンの代表ワークロード)。</summary>
    public long ProjectSum(in SnapshotState snap, TransactionId self, CommittedTxRegistry committed)
    {
        long sum = 0;
        long n = _hwm;
        for (long s = 0; s < n; s++)
            if (TryGet(s, in snap, self, committed, out long v)) sum += v;
        return sum;
    }

    /// <summary>
    /// Phase 5d: 可視な全エントリで count/sum/min/max を 1 パスで集計する (集約 operator の列スキャン)。
    /// raw bits を <see cref="ValueType"/> で数値解釈する。mixed / 未設定の列は false を返し、
    /// 呼び出し側 (集約) が row path へフォールバックする。<paramref name="longSum"/> は整数型の
    /// 厳密な long 合計 (SumLong 用)、Double 型では参考値。
    /// </summary>
    public bool TryAggregate(in SnapshotState snap, TransactionId self, CommittedTxRegistry committed,
        out long count, out double sum, out double min, out double max, out long longSum)
    {
        count = 0; sum = 0; min = 0; max = 0; longSum = 0;
        if (_valueType is ValueTypeUnset or ValueTypeMixed) return false;
        var vt = (PropertyValueType)_valueType;
        bool first = true;
        long n = _hwm;
        for (long s = 0; s < n; s++)
        {
            if (!TryGet(s, in snap, self, committed, out long bits)) continue;
            double d = BitsToDouble(bits, vt);
            sum += d;
            longSum += BitsToLong(bits, vt);
            count++;
            if (first) { min = max = d; first = false; }
            else { if (d < min) min = d; if (d > max) max = d; }
        }
        return true;
    }

    private static double BitsToDouble(long bits, PropertyValueType vt) => vt switch
    {
        PropertyValueType.Double => BitConverter.Int64BitsToDouble(bits),
        PropertyValueType.Int32 => (int)bits,
        PropertyValueType.Bool => bits != 0 ? 1.0 : 0.0,
        _ => bits, // Int64
    };

    private static long BitsToLong(long bits, PropertyValueType vt) => vt switch
    {
        PropertyValueType.Double => (long)BitConverter.Int64BitsToDouble(bits),
        PropertyValueType.Int32 => (int)bits,
        PropertyValueType.Bool => bits != 0 ? 1L : 0L,
        _ => bits, // Int64
    };

    /// <summary>compaction: horizon 未満で xmax コミット済みの delta 版を捨てる。回収数を返す。</summary>
    public int Merge(long horizon, CommittedTxRegistry committed)
    {
        int removed = 0;
        foreach (var kv in _delta)
        {
            var list = kv.Value;
            int before = list.Count;
            list.RemoveAll(e => e.Xmax != 0 && e.Xmax < horizon && committed.IsCommitted(e.Xmax));
            removed += before - list.Count;
        }
        return removed;
    }

    /// <summary>FT-15 / recovery 用: head ページから cache と hwm を読み直す。</summary>
    public void ReloadFromPages()
    {
        LoadMeta();
        EnsureCapacity(_hwm > 0 ? _hwm - 1 : 0);
        RebuildCacheFromPages();
    }

    /// <summary>
    /// Phase 5c (abort): 中止した <paramref name="txId"/> が積んだ delta 版を捨てる。head は
    /// abort の before-image undo + <see cref="ReloadFromPages"/> で旧版へ復元済みなので、
    /// その tx が退避させた (xmax==txId) / 自分で再上書きした (xmin==txId) delta エントリは
    /// 不可視のゴミになる。これを除去して slow leak を防ぐ。回収数を返す。
    /// 並行する他 tx の delta (別 txId) には触れない。
    /// </summary>
    public int PruneTx(long txId)
    {
        int removed = 0;
        foreach (var kv in _delta)
        {
            var list = kv.Value;
            int before = list.Count;
            list.RemoveAll(e => e.Xmin == txId || e.Xmax == txId);
            removed += before - list.Count;
        }
        return removed;
    }

    // --- private ---

    private void WriteHeadPage(long seq, long value, long xmin, long xmax)
    {
        var (pid, off) = Location(seq);
        while (_file.PageCount <= pid.Value)
            _file.AllocatePage(PageKind.ItemPointerMap);
        var ph = _file.PinForWrite(pid);
        var rec = ph.Data.Slice(off, EntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(rec[OffValue..], value);
        BinaryPrimitives.WriteInt64LittleEndian(rec[OffXmin..], xmin);
        BinaryPrimitives.WriteInt64LittleEndian(rec[OffXmax..], xmax);
        _file.UnpinDirty(pid, 0);
    }

    private void RebuildCacheFromPages()
    {
        for (long seq = 0; seq < _hwm; seq++)
        {
            var (pid, off) = Location(seq);
            if (_file.PageCount <= pid.Value) { _value[seq] = 0; _xmin[seq] = 0; _xmax[seq] = 0; continue; }
            using var h = _file.PinForRead(pid);
            ReadOnlySpan<byte> rec = h.Data.Slice(off, EntrySize);
            _value[seq] = BinaryPrimitives.ReadInt64LittleEndian(rec[OffValue..]);
            _xmin[seq] = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmin..]);
            _xmax[seq] = BinaryPrimitives.ReadInt64LittleEndian(rec[OffXmax..]);
        }
    }

    private (PageId pageId, int offset) Location(long seq)
    {
        int epp = EntriesPerPage;
        return (new PageId(seq / epp + 2), (int)(seq % epp) * EntrySize);
    }

    private void EnsureCapacity(long seq)
    {
        if (seq < _value.Length) return;
        int next = _value.Length;
        while (next <= seq) next *= 2;
        Array.Resize(ref _value, next);
        Array.Resize(ref _xmin, next);
        Array.Resize(ref _xmax, next);
    }

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaHwm..]);
        _valueType = h.Data[MetaValueType];
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != FormatVersion.Current)
            throw new FormatVersionMismatchException("scalarcolumn", v, FormatVersion.Current);
    }

    private void SaveMeta(bool initialise = false)
    {
        using var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        ph.Data[MetaValueType] = _valueType;
        if (initialise)
            ph.Data[MetaFormatVersion] = FormatVersion.Current;
    }
}
