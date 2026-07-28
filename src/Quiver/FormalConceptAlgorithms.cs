using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Quiver.Core;
using Quiver.Transactions;

namespace Quiver;

/// <summary>形式概念の列挙方法。</summary>
public enum FormalConceptEnumerationStrategy
{
    /// <summary>一括列挙では Close-by-One、継続可能なページ列挙では NextClosure を選ぶ。</summary>
    Auto = 0,
    /// <summary>継続トークンを持たない Close-by-One で一括列挙する。</summary>
    CloseByOne = 1,
    /// <summary>最後に処理した intent から再開できる NextClosure で列挙する。</summary>
    NextClosure = 2,
}

/// <summary>形式概念列挙の終了理由。</summary>
public enum FormalConceptTerminationReason
{
    /// <summary>文脈の全概念を列挙した。</summary>
    Completed = 0,
    /// <summary>一ページの件数に達した。<see cref="FormalConceptResult.Continuation"/> から再開できる。</summary>
    PageFull = 1,
    /// <summary>列挙全体の結果数上限に達した。</summary>
    MaxResultsReached = 2,
    /// <summary>closure 評価回数の上限に達した。</summary>
    WorkBudget = 3,
    /// <summary>協調的な時間上限に達した。</summary>
    TimeBudget = 4,
    /// <summary>キャンセルが要求された。</summary>
    Cancelled = 5,
    /// <summary>対象数の上限を超えた。概念は返さない。</summary>
    MaxObjectsReached = 6,
    /// <summary>属性数の上限を超えた。概念は返さない。</summary>
    MaxAttributesReached = 7,
    /// <summary>incidence 数の上限を超えた。概念は返さない。</summary>
    MaxIncidencesReached = 8,
}

/// <summary>Nexus incidence から形式概念を列挙する際のフィルタと有限実行上限。</summary>
public sealed record FormalConceptOptions
{
    /// <summary>結果に含める extent の最小要素数。</summary>
    public int MinExtent { get; init; }
    /// <summary>結果に含める intent の最小要素数。</summary>
    public int MinIntent { get; init; }
    /// <summary>一回の呼び出しで返す最大概念数。</summary>
    public int PageSize { get; init; } = 1_000;
    /// <summary>継続を含む列挙全体で返す最大概念数。全概念数と一致した場合は完了として報告する。</summary>
    public int MaxResults { get; init; } = 100_000;
    /// <summary>継続を含む列挙全体で実行する closure 評価の最大回数。</summary>
    public long MaxClosureEvaluations { get; init; } = 1_000_000;
    /// <summary>文脈へ取り込む対象の最大数。</summary>
    public int MaxObjects { get; init; } = 100_000;
    /// <summary>文脈へ取り込む属性の最大数。word bitmap を使うため 64 に制限されない。</summary>
    public int MaxAttributes { get; init; } = 4_096;
    /// <summary>文脈へ取り込む incidence の最大数。</summary>
    public long MaxIncidences { get; init; } = 1_000_000;
    /// <summary>一回の呼び出しに対する協調的な時間上限。無制限は <see cref="Timeout.InfiniteTimeSpan"/>。</summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>一回の呼び出しを協調的に中止するトークン。</summary>
    public CancellationToken CancellationToken { get; init; }
    /// <summary>使用する列挙方法。</summary>
    public FormalConceptEnumerationStrategy Strategy { get; init; } = FormalConceptEnumerationStrategy.Auto;
}

/// <summary>Galois 閉包で得た一つの形式概念。</summary>
/// <param name="Extent">対象の決定的な full packed-ID 順配列。</param>
/// <param name="Intent">属性 Nexus の決定的な full packed-ID 順配列。</param>
public sealed record FormalConcept(IReadOnlyList<EntityRef> Extent, IReadOnlyList<EntityRef> Intent);

/// <summary>
/// 同じ transaction object とその同じ snapshot/context だけで使える継続位置。
/// 永続化、reopen、別 transaction への受け渡しはサポートしない。
/// </summary>
public sealed class FormalConceptContinuation
{
    internal FormalConceptContinuation(
        IReadTransaction transaction,
        TransactionId transactionId,
        string contextFingerprint,
        string objectOrderFingerprint,
        string attributeOrderFingerprint,
        string optionsFingerprint,
        ulong[] intent,
        int emitted,
        long closureEvaluations,
        bool hasIntent,
        int version = 1)
    {
        Transaction = transaction;
        TransactionId = transactionId;
        ContextFingerprint = contextFingerprint;
        ObjectOrderFingerprint = objectOrderFingerprint;
        AttributeOrderFingerprint = attributeOrderFingerprint;
        OptionsFingerprint = optionsFingerprint;
        Intent = intent;
        Emitted = emitted;
        ClosureEvaluations = closureEvaluations;
        HasIntent = hasIntent;
        Version = version;
    }

    internal IReadTransaction Transaction { get; }
    internal ulong[] Intent { get; }
    internal int Emitted { get; }
    internal long ClosureEvaluations { get; }
    internal bool HasIntent { get; }
    /// <summary>継続を作成した transaction の ID。reopen 後の安定 snapshot ID ではない。</summary>
    public TransactionId TransactionId { get; }
    /// <summary>決定的な対象・属性・incidence の fingerprint。</summary>
    public string ContextFingerprint { get; }
    /// <summary>対象順の fingerprint。</summary>
    public string ObjectOrderFingerprint { get; }
    /// <summary>属性順の fingerprint。</summary>
    public string AttributeOrderFingerprint { get; }
    /// <summary>フィルタと列挙 option の fingerprint。</summary>
    public string OptionsFingerprint { get; }
    /// <summary>継続形式の version。</summary>
    public int Version { get; }
}

/// <summary>形式概念列挙の決定的な prefix と終了状態。</summary>
public sealed class FormalConceptResult
{
    internal FormalConceptResult(
        IReadOnlyList<FormalConcept> concepts,
        FormalConceptContinuation? continuation,
        FormalConceptTerminationReason reason,
        int objectCount,
        int attributeCount,
        long incidenceCount,
        long closureEvaluations,
        string? contextFingerprint,
        TransactionId transactionId)
    {
        Concepts = concepts;
        Continuation = continuation;
        TerminationReason = reason;
        ObjectCount = objectCount;
        AttributeCount = attributeCount;
        IncidenceCount = incidenceCount;
        ClosureEvaluations = closureEvaluations;
        ContextFingerprint = contextFingerprint;
        TransactionId = transactionId;
    }

    /// <summary>この呼び出しで完了した概念の決定的な prefix。</summary>
    public IReadOnlyList<FormalConcept> Concepts { get; }
    /// <summary>同じ transaction/snapshot/context で続ける位置。再開不能な終了では <c>null</c>。</summary>
    public FormalConceptContinuation? Continuation { get; }
    /// <summary>終了理由。</summary>
    public FormalConceptTerminationReason TerminationReason { get; }
    /// <summary>materialize した対象数。入力走査が完了しなければ 0。</summary>
    public int ObjectCount { get; }
    /// <summary>materialize した属性数。入力走査が完了しなければ 0。</summary>
    public int AttributeCount { get; }
    /// <summary>materialize した incidence 数。入力走査が完了しなければ 0。</summary>
    public long IncidenceCount { get; }
    /// <summary>継続を含む列挙全体で完了した closure 評価回数。</summary>
    public long ClosureEvaluations { get; }
    /// <summary>完了した materialization の context fingerprint。入力上限で停止した場合は <c>null</c>。</summary>
    public string? ContextFingerprint { get; }
    /// <summary>列挙を実行した transaction の ID。reopen 後の安定 snapshot ID ではない。</summary>
    public TransactionId TransactionId { get; }
    /// <summary>文脈の全概念を列挙したか。</summary>
    public bool IsComplete => TerminationReason == FormalConceptTerminationReason.Completed;
    /// <summary>結果数上限、作業上限、時間切れ、キャンセルにより打ち切られたか。</summary>
    public bool IsTruncated => TerminationReason is FormalConceptTerminationReason.MaxResultsReached
        or FormalConceptTerminationReason.WorkBudget
        or FormalConceptTerminationReason.TimeBudget
        or FormalConceptTerminationReason.Cancelled;
}

/// <summary>Nexus と member Vertex の incidence を formal context として形式概念を列挙する。</summary>
public static class FormalConceptAlgorithms
{
    /// <summary>
    /// 指定型の可視 Nexus を属性、指定ロールの member Vertex を対象として概念を列挙する。
    /// 対象 universe は指定ロールの incidence に実際に現れる Vertex だけから成る。
    /// 対象と属性は kind、Generation、Sequence を含む full packed-ID 順に固定する。
    /// continuation は作成元と同じ transaction object、snapshot、context、順序、filter、option でだけ再開できる。
    /// CancellationToken は呼び出しごとの停止要求でありfingerprintには含めないため、キャンセル後は新しいtokenで再開できる。
    /// Quiver は公開 stable snapshot ID を持たないため、transaction の reopen や別 transaction への継続を保証しない。
    /// </summary>
    public static FormalConceptResult EnumerateFormalConcepts(
        this IReadTransaction transaction,
        string nexusType,
        string? memberRole = null,
        FormalConceptOptions? options = null,
        FormalConceptContinuation? continuation = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(nexusType);
        options ??= new FormalConceptOptions();
        Validate(options);
        var elapsed = Stopwatch.StartNew();
        string optionFingerprint = FingerprintOptions(nexusType, memberRole, options);
        ValidateTokenIdentity(transaction, continuation, optionFingerprint);

        Materialization materialization = Materialize(transaction, nexusType, memberRole, options, elapsed);
        if (materialization.StopReason is { } stop)
        {
            FormalConceptContinuation? resumable = stop is FormalConceptTerminationReason.TimeBudget
                or FormalConceptTerminationReason.Cancelled
                ? continuation
                : null;
            return new([], resumable, stop, 0, 0, 0,
                continuation?.ClosureEvaluations ?? 0, null, transaction.Id);
        }
        Context context = materialization.Context!;
        ValidateTokenContext(continuation, context);

        FormalConceptEnumerationStrategy strategy = options.Strategy == FormalConceptEnumerationStrategy.Auto
            ? continuation is null && options.PageSize >= options.MaxResults
                ? FormalConceptEnumerationStrategy.CloseByOne
                : FormalConceptEnumerationStrategy.NextClosure
            : options.Strategy;
        if (continuation is not null && strategy == FormalConceptEnumerationStrategy.CloseByOne)
            throw new ArgumentException("CloseByOne は continuation から再開できません。", nameof(continuation));
        if (strategy == FormalConceptEnumerationStrategy.CloseByOne && options.PageSize < options.MaxResults)
            throw new ArgumentException("CloseByOne は PageSize が MaxResults 以上の一括列挙でだけ使用できます。", nameof(options));
        return strategy == FormalConceptEnumerationStrategy.CloseByOne
            ? EnumerateCloseByOne(transaction, context, options, optionFingerprint, elapsed)
            : EnumerateNextClosure(transaction, context, options, optionFingerprint, continuation, elapsed);
    }

    private static FormalConceptResult EnumerateNextClosure(
        IReadTransaction transaction,
        Context context,
        FormalConceptOptions options,
        string optionsFingerprint,
        FormalConceptContinuation? token,
        Stopwatch elapsed)
    {
        long work = token?.ClosureEvaluations ?? 0;
        int emitted = token?.Emitted ?? 0;
        bool resultLimitReached = emitted >= options.MaxResults;
        ulong[]? lastProcessed = token?.HasIntent == true ? token.Intent : null;
        Closure currentClosure;
        FormalConceptTerminationReason stop;
        if (token is null || !token.HasIntent)
        {
            if (!TryClose(context, new ulong[context.WordCount], options, elapsed, ref work, out currentClosure, out stop))
                return Stop([], transaction, context, optionsFingerprint, null, emitted, work, stop, options.TimeLimit);
        }
        else if (!TryNext(context, token.Intent, options, elapsed, ref work, out currentClosure, out stop))
        {
            return stop == FormalConceptTerminationReason.Completed
                ? Result([], null, FormalConceptTerminationReason.Completed, transaction, context, work)
                : Stop([], transaction, context, optionsFingerprint, token.Intent, emitted, work, stop, options.TimeLimit);
        }

        var page = new List<FormalConcept>(Math.Min(options.PageSize, 256));
        while (true)
        {
            ulong[] current = currentClosure.Intent;
            if (currentClosure.ExtentCount >= options.MinExtent && PopCount(current) >= options.MinIntent)
            {
                if (resultLimitReached)
                    return Result(page, null, FormalConceptTerminationReason.MaxResultsReached, transaction, context, work);
                if (!TryMaterializeConcept(context, current, options, elapsed, out FormalConcept? concept, out stop))
                    return Stop(page, transaction, context, optionsFingerprint, lastProcessed, emitted, work, stop, options.TimeLimit);
                page.Add(concept!);
                emitted++;
                resultLimitReached = emitted >= options.MaxResults;
                if (!resultLimitReached && page.Count >= options.PageSize)
                {
                    var nextToken = Token(transaction, context, optionsFingerprint, current, emitted, work, true);
                    return Result(page, nextToken, FormalConceptTerminationReason.PageFull, transaction, context, work);
                }
            }

            ulong[] processed = current;
            if (!TryNext(context, current, options, elapsed, ref work, out currentClosure, out stop))
            {
                return stop == FormalConceptTerminationReason.Completed
                    ? Result(page, null, FormalConceptTerminationReason.Completed, transaction, context, work)
                    : Stop(page, transaction, context, optionsFingerprint, processed, emitted, work, stop, options.TimeLimit);
            }
            lastProcessed = processed;
        }
    }

    private static FormalConceptResult EnumerateCloseByOne(
        IReadTransaction transaction,
        Context context,
        FormalConceptOptions options,
        string optionsFingerprint,
        Stopwatch elapsed)
    {
        long work = 0;
        var concepts = new List<FormalConcept>(Math.Min(options.MaxResults, 1024));
        FormalConceptTerminationReason reason = FormalConceptTerminationReason.Completed;
        bool resultLimitReached = false;
        if (!TryClose(context, new ulong[context.WordCount], options, elapsed, ref work, out Closure root, out reason))
            return Result([], null, reason, transaction, context, work);
        var stack = new Stack<CloseByOneFrame>();
        stack.Push(new(root.Intent, root.ExtentCount, 0, 0, false));
        while (stack.Count > 0 && reason == FormalConceptTerminationReason.Completed)
        {
            CloseByOneFrame frame = stack.Pop();
            if (!frame.Entered)
            {
                if (frame.ExtentCount >= options.MinExtent && PopCount(frame.Intent) >= options.MinIntent)
                {
                    if (resultLimitReached)
                    {
                        reason = FormalConceptTerminationReason.MaxResultsReached;
                        break;
                    }
                    if (!TryMaterializeConcept(context, frame.Intent, options, elapsed, out FormalConcept? concept, out reason)) break;
                    concepts.Add(concept!);
                    resultLimitReached = concepts.Count >= options.MaxResults;
                }
                frame = frame with { NextAttribute = frame.StartAttribute, Entered = true };
            }
            int attribute = frame.NextAttribute;
            while (attribute < context.AttributeCount && Contains(frame.Intent, attribute)) attribute++;
            if (attribute >= context.AttributeCount) continue;
            stack.Push(frame with { NextAttribute = attribute + 1 });
            ulong[] seed = (ulong[])frame.Intent.Clone();
            Set(seed, attribute);
            if (!TryClose(context, seed, options, elapsed, ref work, out Closure candidate, out reason)) break;
            if (AgreesBelow(candidate.Intent, frame.Intent, attribute))
                stack.Push(new(candidate.Intent, candidate.ExtentCount, attribute + 1, attribute + 1, false));
        }
        return Result(concepts, null, reason, transaction, context, work);
    }

    private static bool TryNext(Context context, ulong[] current, FormalConceptOptions options, Stopwatch elapsed,
        ref long work, out Closure next, out FormalConceptTerminationReason reason)
    {
        for (int attribute = context.AttributeCount - 1; attribute >= 0; attribute--)
        {
            if (!ObserveRuntime(options, elapsed, out reason)) { next = default; return false; }
            if (Contains(current, attribute)) continue;
            ulong[] seed = PrefixWith(current, attribute);
            if (!TryClose(context, seed, options, elapsed, ref work, out Closure closure, out reason))
            { next = default; return false; }
            if (!AgreesBelow(closure.Intent, current, attribute)) continue;
            next = closure;
            return true;
        }
        next = default;
        reason = FormalConceptTerminationReason.Completed;
        return false;
    }

    private static bool TryClose(Context context, ulong[] seed, FormalConceptOptions options, Stopwatch elapsed,
        ref long work, out Closure closure, out FormalConceptTerminationReason reason)
    {
        if (!ObserveRuntime(options, elapsed, out reason) || work >= options.MaxClosureEvaluations)
        {
            reason = reason == FormalConceptTerminationReason.Completed ? FormalConceptTerminationReason.WorkBudget : reason;
            closure = default;
            return false;
        }
        work++;
        ulong[] intent = AllBits(context.AttributeCount);
        int extent = 0;
        for (int obj = 0; obj < context.Rows.Length; obj++)
        {
            if ((obj & 63) == 0 && !ObserveRuntime(options, elapsed, out reason)) { closure = default; return false; }
            if (!IsSubset(seed, context.Rows[obj])) continue;
            And(intent, context.Rows[obj]);
            extent++;
        }
        closure = new(intent, extent);
        reason = FormalConceptTerminationReason.Completed;
        return true;
    }

    private static bool TryMaterializeConcept(Context context, ulong[] intent, FormalConceptOptions options,
        Stopwatch elapsed, out FormalConcept? concept, out FormalConceptTerminationReason reason)
    {
        var extent = new List<EntityRef>();
        for (int i = 0; i < context.Rows.Length; i++)
        {
            if ((i & 63) == 0 && !ObserveRuntime(options, elapsed, out reason)) { concept = null; return false; }
            if (IsSubset(intent, context.Rows[i])) extent.Add(context.Objects[i]);
        }
        var attributes = new List<EntityRef>(PopCount(intent));
        for (int i = 0; i < context.AttributeCount; i++) if (Contains(intent, i)) attributes.Add(context.Attributes[i]);
        concept = new(extent.ToArray(), attributes.ToArray());
        reason = FormalConceptTerminationReason.Completed;
        return true;
    }

    private static Materialization Materialize(IReadTransaction transaction, string nexusType, string? memberRole,
        FormalConceptOptions options, Stopwatch elapsed)
    {
        var attributes = new List<EntityRef>();
        var incidence = new HashSet<(EntityRef Object, EntityRef Attribute)>();
        var objects = new HashSet<EntityRef>();
        int scanned = 0;
        long scannedMembers = 0;
        foreach (NexusId nexus in transaction.Query.Nexuses().AsEnumerable())
        {
            if ((scanned++ & 63) == 0 && !ObserveRuntime(options, elapsed, out var stop)) return new(null, stop);
            if (!string.Equals(transaction.GetNexusType(nexus), nexusType, StringComparison.Ordinal)) continue;
            if (attributes.Count >= options.MaxAttributes) return new(null, FormalConceptTerminationReason.MaxAttributesReached);
            EntityRef attribute = EntityRef.From(nexus);
            attributes.Add(attribute);
            var members = transaction.GetMembers(nexus, memberRole);
            try
            {
                while (members.MoveNext())
                {
                    if ((scannedMembers++ & 255) == 0 && !ObserveRuntime(options, elapsed, out stop)) return new(null, stop);
                    EntityRef obj = EntityRef.From(members.Current.VertexId);
                    incidence.Add((obj, attribute));
                    objects.Add(obj);
                    if (objects.Count > options.MaxObjects) return new(null, FormalConceptTerminationReason.MaxObjectsReached);
                    if (incidence.Count > options.MaxIncidences) return new(null, FormalConceptTerminationReason.MaxIncidencesReached);
                }
            }
            finally { members.Dispose(); }
        }
        EntityRef[] orderedObjects = objects.OrderBy(Packed).ToArray();
        EntityRef[] orderedAttributes = attributes.OrderBy(Packed).ToArray();
        if (!ObserveRuntime(options, elapsed, out var phaseStop)) return new(null, phaseStop);
        var objectIndex = orderedObjects.Select((value, index) => (value, index)).ToDictionary(static x => x.value, static x => x.index);
        var attributeIndex = orderedAttributes.Select((value, index) => (value, index)).ToDictionary(static x => x.value, static x => x.index);
        int words = (orderedAttributes.Length + 63) / 64;
        var rows = new ulong[orderedObjects.Length][];
        for (int i = 0; i < rows.Length; i++)
        {
            if ((i & 255) == 0 && !ObserveRuntime(options, elapsed, out phaseStop)) return new(null, phaseStop);
            rows[i] = new ulong[words];
        }
        int converted = 0;
        foreach ((EntityRef obj, EntityRef attribute) in incidence)
        {
            if ((converted++ & 255) == 0 && !ObserveRuntime(options, elapsed, out phaseStop)) return new(null, phaseStop);
            Set(rows[objectIndex[obj]], attributeIndex[attribute]);
        }
        string objectOrder = FingerprintEntities(orderedObjects);
        string attributeOrder = FingerprintEntities(orderedAttributes);
        string contextFingerprint = FingerprintContext(orderedObjects, orderedAttributes, rows);
        return new(new(orderedObjects, orderedAttributes, rows, incidence.Count, objectOrder, attributeOrder, contextFingerprint), null);
    }

    private static void ValidateTokenIdentity(IReadTransaction transaction, FormalConceptContinuation? token, string optionsFingerprint)
    {
        if (token is null) return;
        if (token.Version != 1 || !ReferenceEquals(token.Transaction, transaction) || token.TransactionId != transaction.Id)
            throw new ArgumentException("continuation は作成元と同じ transaction object でだけ使用できます。", nameof(token));
        if (!string.Equals(token.OptionsFingerprint, optionsFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("continuation の filter または option が一致しません。", nameof(token));
    }

    private static void ValidateTokenContext(FormalConceptContinuation? token, Context context)
    {
        if (token is null) return;
        if (!string.Equals(token.ContextFingerprint, context.Fingerprint, StringComparison.Ordinal)
            || !string.Equals(token.ObjectOrderFingerprint, context.ObjectOrderFingerprint, StringComparison.Ordinal)
            || !string.Equals(token.AttributeOrderFingerprint, context.AttributeOrderFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("continuation 作成後に snapshot/context または決定順が変化しました。");
    }

    private static FormalConceptContinuation Token(IReadTransaction transaction, Context context, string options,
        ulong[]? intent, int emitted, long work, bool hasIntent) => new(transaction, transaction.Id, context.Fingerprint,
        context.ObjectOrderFingerprint, context.AttributeOrderFingerprint, options,
        intent is null ? new ulong[context.WordCount] : (ulong[])intent.Clone(), emitted, work, hasIntent);

    private static FormalConceptResult Stop(List<FormalConcept> concepts, IReadTransaction transaction, Context context,
        string options, ulong[]? lastProcessed, int emitted, long work, FormalConceptTerminationReason reason, TimeSpan timeLimit)
    {
        FormalConceptContinuation? token = reason == FormalConceptTerminationReason.WorkBudget
            || reason == FormalConceptTerminationReason.TimeBudget && timeLimit == TimeSpan.Zero
            ? null
            : Token(transaction, context, options, lastProcessed, emitted, work, lastProcessed is not null);
        return Result(concepts, token, reason, transaction, context, work);
    }

    private static FormalConceptResult Result(IReadOnlyList<FormalConcept> concepts, FormalConceptContinuation? token,
        FormalConceptTerminationReason reason, IReadTransaction transaction, Context context, long work) =>
        new(concepts, token, reason, context.Objects.Length, context.Attributes.Length, context.IncidenceCount,
            work, context.Fingerprint, transaction.Id);

    private static bool ObserveRuntime(FormalConceptOptions options, Stopwatch elapsed, out FormalConceptTerminationReason reason)
    {
        if (options.CancellationToken.IsCancellationRequested) { reason = FormalConceptTerminationReason.Cancelled; return false; }
        if (options.TimeLimit != Timeout.InfiniteTimeSpan && elapsed.Elapsed >= options.TimeLimit)
        { reason = FormalConceptTerminationReason.TimeBudget; return false; }
        reason = FormalConceptTerminationReason.Completed;
        return true;
    }

    private static void Validate(FormalConceptOptions options)
    {
        if (options.MinExtent < 0 || options.MinIntent < 0 || options.PageSize <= 0 || options.MaxResults <= 0
            || options.MaxClosureEvaluations <= 0 || options.MaxObjects <= 0 || options.MaxAttributes <= 0
            || options.MaxIncidences <= 0 || options.MaxIncidences > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "列挙上限は正、support は 0 以上でなければなりません。");
        if (options.TimeLimit < TimeSpan.Zero && options.TimeLimit != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "TimeLimit は 0 以上または無制限でなければなりません。");
        if (!Enum.IsDefined(options.Strategy)) throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static string FingerprintOptions(string type, string? role, FormalConceptOptions o) => Hash(writer =>
    {
        writer.Write(1); writer.Write(type); writer.Write(role ?? ""); writer.Write(o.MinExtent); writer.Write(o.MinIntent);
        writer.Write(o.PageSize); writer.Write(o.MaxResults); writer.Write(o.MaxClosureEvaluations); writer.Write(o.MaxObjects);
        writer.Write(o.MaxAttributes); writer.Write(o.MaxIncidences); writer.Write(o.TimeLimit.Ticks); writer.Write((int)o.Strategy);
    });
    private static string FingerprintEntities(EntityRef[] entities) => Hash(writer =>
    { writer.Write(entities.Length); foreach (EntityRef entity in entities) writer.Write(Packed(entity)); });
    private static string FingerprintContext(EntityRef[] objects, EntityRef[] attributes, ulong[][] rows) => Hash(writer =>
    {
        writer.Write(objects.Length); writer.Write(attributes.Length);
        foreach (EntityRef entity in objects) writer.Write(Packed(entity));
        foreach (EntityRef entity in attributes) writer.Write(Packed(entity));
        foreach (ulong[] row in rows) foreach (ulong word in row) writer.Write(word);
    });
    private static string Hash(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) write(writer);
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static long Packed(EntityRef entity) => EntityRef.Pack(entity.Kind, entity.Sequence, entity.Generation);
    private static bool Contains(ulong[] bits, int index) => (bits[index >> 6] & (1UL << (index & 63))) != 0;
    private static void Set(ulong[] bits, int index) => bits[index >> 6] |= 1UL << (index & 63);
    private static bool IsSubset(ulong[] subset, ulong[] superset)
    { for (int i = 0; i < subset.Length; i++) if ((subset[i] & ~superset[i]) != 0) return false; return true; }
    private static void And(ulong[] left, ulong[] right)
    { for (int i = 0; i < left.Length; i++) left[i] &= right[i]; }
    private static int PopCount(ulong[] bits)
    { int count = 0; foreach (ulong word in bits) count += BitOperations.PopCount(word); return count; }
    private static ulong[] AllBits(int count)
    {
        var bits = new ulong[(count + 63) / 64]; Array.Fill(bits, ulong.MaxValue);
        if (bits.Length > 0 && (count & 63) != 0) bits[^1] = (1UL << (count & 63)) - 1;
        return bits;
    }
    private static ulong[] PrefixWith(ulong[] current, int attribute)
    {
        var result = new ulong[current.Length]; int word = attribute >> 6;
        for (int i = 0; i < word; i++) result[i] = current[i];
        ulong lower = attribute == 0 ? 0 : (1UL << (attribute & 63)) - 1;
        if ((attribute & 63) == 0) lower = 0;
        result[word] = (current[word] & lower) | (1UL << (attribute & 63));
        return result;
    }
    private static bool AgreesBelow(ulong[] candidate, ulong[] current, int attribute)
    {
        int word = attribute >> 6;
        for (int i = 0; i < word; i++) if (candidate[i] != current[i]) return false;
        int bit = attribute & 63; ulong mask = bit == 0 ? 0 : (1UL << bit) - 1;
        return (candidate[word] & mask) == (current[word] & mask);
    }

    private sealed record Context(EntityRef[] Objects, EntityRef[] Attributes, ulong[][] Rows, long IncidenceCount,
        string ObjectOrderFingerprint, string AttributeOrderFingerprint, string Fingerprint)
    { internal int AttributeCount => Attributes.Length; internal int WordCount => (AttributeCount + 63) / 64; }
    private sealed record Materialization(Context? Context, FormalConceptTerminationReason? StopReason);
    private readonly record struct Closure(ulong[] Intent, int ExtentCount);
    private readonly record struct CloseByOneFrame(
        ulong[] Intent,
        int ExtentCount,
        int StartAttribute,
        int NextAttribute,
        bool Entered);
}
