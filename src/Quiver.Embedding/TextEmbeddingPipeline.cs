using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Quiver.Core;
using Quiver.Embedding.Providers;
using Quiver.Embedding.Text;
using Quiver.Text;
using Quiver.Transactions;

namespace Quiver.Embedding;

/// <summary>
/// VEC-4 helper. Orchestrates the
/// <c>normalize → emoji policy → truncate → hash → dedup → embed → SetVector
/// → MarkCompleted</c> flow on top of an <see cref="IGraphEngine"/> adapter,
/// without taking any compile-time dependency on engine internals.
/// </summary>
/// <remarks>
/// Three entry points:
/// <list type="bullet">
/// <item><see cref="EnqueueAsync(EntityRef, string, string, CancellationToken)"/> — direct enqueue. Used by scanners or manual backfill.</item>
/// <item><see cref="EnqueueOnCommit"/> — register a post-commit hook on a transaction so enqueue fires only after WAL fsync (Y').</item>
/// <item><see cref="ScanAndEnqueueAsync"/> — startup / migration walk to pick up stale or never-seen entities (Z').</item>
/// </list>
/// A single background worker drains the channel; concurrency is capped by
/// the provider's <see cref="IEmbeddingProvider.MaxConcurrency"/>. Cancellation
/// of <see cref="RunAsync"/> drains the worker but does not throw.
/// </remarks>
public sealed class TextEmbeddingPipeline : IAsyncDisposable
{
    private readonly IGraphEngine _engine;
    private readonly IEmbeddingProvider _provider;
    private readonly ITextNormalizer _normalizer;
    private readonly ITextTruncator _truncator;
    private readonly IEmbeddingTaskLog _taskLog;
    private readonly IRetryPolicy _retry;
    private readonly TextEmbeddingPipelineOptions _options;

    private readonly Channel<QueueItem> _channel;
    private readonly SemaphoreSlim _gate;
    private int _pending;

    public TextEmbeddingPipeline(
        IGraphEngine engine,
        IEmbeddingProvider provider,
        ITextNormalizer normalizer,
        ITextTruncator truncator,
        IEmbeddingTaskLog taskLog,
        IRetryPolicy retryPolicy,
        TextEmbeddingPipelineOptions? options = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _truncator = truncator ?? throw new ArgumentNullException(nameof(truncator));
        _taskLog = taskLog ?? throw new ArgumentNullException(nameof(taskLog));
        _retry = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _options = options ?? new TextEmbeddingPipelineOptions();

        var channelOptions = new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = _options.Backpressure switch
            {
                BackpressureMode.DropOldest => BoundedChannelFullMode.DropOldest,
                BackpressureMode.DropNewest => BoundedChannelFullMode.DropNewest,
                BackpressureMode.ThrowOnFull => BoundedChannelFullMode.Wait, // surfaced manually below
                _ => BoundedChannelFullMode.Wait,
            },
            SingleReader = true,
            SingleWriter = false,
        };
        _channel = Channel.CreateBounded<QueueItem>(channelOptions);
        _gate = new SemaphoreSlim(Math.Max(1, provider.MaxConcurrency));
    }

    public int PendingTasks => Volatile.Read(ref _pending);

    /// <summary>
    /// Normalize / truncate / hash / dedup-check the input, then drop a job
    /// onto the worker queue. No provider call is made on this thread.
    /// </summary>
    public async ValueTask EnqueueAsync(
        EntityRef entity,
        string indexName,
        string sourceText,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        ArgumentNullException.ThrowIfNull(sourceText);

        var prepared = Prepare(sourceText);
        var key = new EmbeddingTaskKey(entity.Kind, entity.Id, indexName, _provider.ProviderId);

        var info = await _taskLog.GetInfoAsync(key, ct).ConfigureAwait(false);
        if (info.State == EmbeddingTaskState.Completed && info.LastContentHash == prepared.ContentHash)
            return; // idempotent: same text, already done.

        Interlocked.Increment(ref _pending);
        try
        {
            if (_options.Backpressure == BackpressureMode.ThrowOnFull)
            {
                if (!_channel.Writer.TryWrite(new QueueItem(entity, indexName, prepared)))
                {
                    Interlocked.Decrement(ref _pending);
                    throw new InvalidOperationException(
                        "Embedding pipeline queue is full and Backpressure=ThrowOnFull.");
                }
            }
            else
            {
                await _channel.Writer.WriteAsync(new QueueItem(entity, indexName, prepared), ct)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            Interlocked.Decrement(ref _pending);
            throw;
        }
    }

    /// <summary>
    /// post-commit フック (VEC-3) を登録し、トランザクションの WAL コミットが永続化された後に
    /// <see cref="EnqueueAsync(EntityRef, string, string, CancellationToken)"/> を呼び出す。
    /// テキストは登録時にキャプチャするため、フックスレッドで該当プロパティを再読み出しする必要は無い。
    /// </summary>
    public void EnqueueOnCommit(
        ICommitHookRegistrar tx,
        EntityRef entity,
        string indexName,
        string sourceText)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentException.ThrowIfNullOrEmpty(indexName);
        ArgumentNullException.ThrowIfNull(sourceText);

        tx.OnCommitted(() =>
        {
            // フックの契約上、例外はキャッチされて無視される — それでもベストエフォートで enqueue を試みる。
            // fire-and-forget。ここで取りこぼした分は ScanAndEnqueueAsync が拾う。
            _ = EnqueueAsync(entity, indexName, sourceText, CancellationToken.None);
        });
    }

    /// <summary>
    /// クエリ文字列をプロバイダ経由で同期的に埋め込み、
    /// <c>IVectorStore.KnnSearch</c> に渡せる生ベクトルを返す。
    /// キューとタスクログをバイパスする経路。
    /// </summary>
    public async ValueTask<ReadOnlyMemory<float>> EmbedQueryAsync(string queryText, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        var prepared = Prepare(queryText);
        var result = await _provider.EmbedAsync(
            new EmbeddingRequest(prepared.Text, EmbeddingPurpose.Query), ct).ConfigureAwait(false);
        return result.Vector;
    }

    /// <summary>
    /// Z' pattern: walk the engine for the configured kind, read the source
    /// property, and enqueue anything whose task log entry is missing,
    /// failed, or whose content hash no longer matches the live text.
    /// </summary>
    public async Task ScanAndEnqueueAsync(EmbeddingScanSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // await をまたいでセッションを保持しないよう、1 セッション内でエンティティ一覧を
        // 一度にマテリアライズする。
        var staged = new List<(EntityRef Entity, string Text)>();
        using (var session = _engine.BeginRead())
        {
            foreach (var entity in session.EnumerateEntities(spec.Kind))
            {
                ct.ThrowIfCancellationRequested();
                if (!session.TryReadStringProperty(entity, spec.SourcePropertyName, out var text))
                    continue;
                staged.Add((entity, text));
            }
        }

        foreach (var (entity, text) in staged)
        {
            ct.ThrowIfCancellationRequested();
            await EnqueueAsync(entity, spec.TargetIndexName, text, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Block until the queue is empty. Used by migration tooling.</summary>
    public async Task WhenDrainedAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _pending) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Drive the worker loop. Returns when <paramref name="ct"/> fires.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await ProcessAsync(item, ct).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    private async Task ProcessAsync(QueueItem item, CancellationToken ct)
    {
        var key = new EmbeddingTaskKey(item.Entity.Kind, item.Entity.Id, item.IndexName, _provider.ProviderId);

        await _taskLog.MarkInProgressAsync(key, item.Prepared.ContentHash, ct).ConfigureAwait(false);

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var result = await _provider.EmbedAsync(
                    new EmbeddingRequest(item.Prepared.Text, EmbeddingPurpose.Document),
                    ct).ConfigureAwait(false);
                if (result.Vector.Length != _provider.Dimensions)
                {
                    throw new InvalidOperationException(
                        $"Provider '{_provider.ProviderId}' returned {result.Vector.Length} dims, expected {_provider.Dimensions}.");
                }
                _engine.Vectors.SetVector(item.Entity.Kind, item.Entity.Id, item.IndexName, result.Vector.Span);
                await _taskLog.MarkCompletedAsync(key, item.Prepared.ContentHash, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_retry.ShouldRetry(ex, attempt, out var delay))
                {
                    await _taskLog.MarkFailedAsync(key, ex.Message, retryable: false, ct).ConfigureAwait(false);
                    return;
                }
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private PreparedText Prepare(string raw)
    {
        var normalized = _normalizer.Normalize(raw.AsSpan());
        var afterEmoji = _options.EmojiPolicy.Apply(normalized.Text);
        var truncated = _truncator.Truncate(
            afterEmoji,
            _provider.Limits.MaxLength,
            _provider.Limits.CountingMode,
            _provider.Limits.DefaultTruncation);
        var hash = ContentHash(truncated);
        return new PreparedText(truncated, hash);
    }

    private static string ContentHash(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _provider.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private readonly record struct PreparedText(string Text, string ContentHash);

    private readonly record struct QueueItem(EntityRef Entity, string IndexName, PreparedText Prepared);
}
