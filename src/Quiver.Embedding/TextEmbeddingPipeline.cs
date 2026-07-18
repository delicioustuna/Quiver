using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Quiver;
using Quiver.Core;
using Quiver.Embedding.Providers;
using Quiver.Embedding.Text;
using Quiver.Storage.Records;
using Quiver.Text;
using Quiver.Transactions;

namespace Quiver.Embedding;

/// <summary>
/// テキストからベクトルを生成して保存するパイプライン。
/// <c>normalize → emoji policy → truncate → hash → dedup → embed → SetVector
/// → MarkCompleted</c> の処理を transaction-scoped vector property API 上で組み立てる。
/// </summary>
/// <remarks>
/// 入口は次の 3 つ。
/// <list type="bullet">
/// <item><see cref="EnqueueAsync(EntityRef, string, string, CancellationToken)"/> — 直接キューへ追加する。スキャンや手動バックフィルで使用する。</item>
/// <item><see cref="EnqueueOnCommit"/> — トランザクションに post-commit フックを登録し、WAL の fsync 後にだけキューへ追加する。</item>
/// <item><see cref="ScanAndEnqueueAsync"/> — 起動時やマイグレーション時に走査し、古いエンティティや未処理のエンティティを拾う。</item>
/// </list>
/// 1 つのバックグラウンドワーカーがチャネルを消費し、並行数はプロバイダの
/// <see cref="IEmbeddingProvider.MaxConcurrency"/> で制限する。
/// <see cref="RunAsync"/> をキャンセルすると、ワーカーは例外を送出せずに終了する。
/// </remarks>
public sealed class TextEmbeddingPipeline : IAsyncDisposable
{
    private readonly QuiverDatabase _database;
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
        QuiverDatabase database,
        IEmbeddingProvider provider,
        ITextNormalizer normalizer,
        ITextTruncator truncator,
        IEmbeddingTaskLog taskLog,
        IRetryPolicy retryPolicy,
        TextEmbeddingPipelineOptions? options = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
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
                BackpressureMode.ThrowOnFull => BoundedChannelFullMode.Wait, // 下で明示的に例外化する
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
    /// 入力を正規化、切り詰め、ハッシュ化して重複を確認し、ワーカーキューへ追加する。
    /// このスレッドではプロバイダを呼び出さない。
    /// </summary>
    public async ValueTask EnqueueAsync(
        EntityRef entity,
        string targetPropertyName,
        string sourceText,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPropertyName);
        ArgumentNullException.ThrowIfNull(sourceText);

        var prepared = Prepare(sourceText);
        var key = new EmbeddingTaskKey(
            entity,
            targetPropertyName,
            _provider.ProviderId,
            _options.NormalizationProfile);

        var info = await _taskLog.GetInfoAsync(key, ct).ConfigureAwait(false);
        if (info.State == EmbeddingTaskState.Completed && info.LastContentHash == prepared.ContentHash)
            return; // 同じテキストで完了済みなら冪等に終了する。

        Interlocked.Increment(ref _pending);
        try
        {
            if (_options.Backpressure == BackpressureMode.ThrowOnFull)
            {
                if (!_channel.Writer.TryWrite(new QueueItem(entity, targetPropertyName, prepared)))
                {
                    Interlocked.Decrement(ref _pending);
                    throw new InvalidOperationException(
                        "Embedding pipeline queue is full and Backpressure=ThrowOnFull.");
                }
            }
            else
            {
                await _channel.Writer.WriteAsync(new QueueItem(entity, targetPropertyName, prepared), ct)
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
    /// post-commit フックを登録し、トランザクションの WAL コミットが永続化された後に
    /// <see cref="EnqueueAsync(EntityRef, string, string, CancellationToken)"/> を呼び出す。
    /// テキストは登録時にキャプチャするため、フックスレッドで該当プロパティを再読み出しする必要は無い。
    /// </summary>
    public void EnqueueOnCommit(
        ICommitHookRegistrar tx,
        EntityRef entity,
        string targetPropertyName,
        string sourceText)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentException.ThrowIfNullOrEmpty(targetPropertyName);
        ArgumentNullException.ThrowIfNull(sourceText);

        tx.OnCommitted(() =>
        {
            // フックの契約上、例外はキャッチされて無視される — それでもベストエフォートで enqueue を試みる。
            // fire-and-forget。ここで取りこぼした分は ScanAndEnqueueAsync が拾う。
            _ = EnqueueAsync(entity, targetPropertyName, sourceText, CancellationToken.None);
        });
    }

    /// <summary>
    /// クエリ文字列をプロバイダ経由で同期的に埋め込み、
    /// <see cref="IReadTransaction.KnnSearch"/> に渡せる生ベクトルを返す。
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
    /// 設定された種類のエンティティを走査して元プロパティを読み取り、
    /// タスクログが無いもの、失敗したもの、またはコンテンツハッシュが現在のテキストと
    /// 一致しないものをキューへ追加する。
    /// </summary>
    public async Task ScanAndEnqueueAsync(EmbeddingScanSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // await をまたいでセッションを保持しないよう、1 セッション内でエンティティ一覧を
        // 一度にマテリアライズする。
        var staged = new List<(EntityRef Entity, string Text)>();
        using (var transaction = _database.BeginReadTransaction())
        {
            foreach (EntityRef entity in EnumerateEntities(transaction, spec.Kind))
            {
                ct.ThrowIfCancellationRequested();
                if (!TryReadStringProperty(
                        transaction,
                        entity,
                        spec.SourcePropertyName,
                        out string text))
                    continue;
                staged.Add((entity, text));
            }
        }

        foreach (var (entity, text) in staged)
        {
            ct.ThrowIfCancellationRequested();
            await EnqueueAsync(entity, spec.TargetPropertyName, text, ct).ConfigureAwait(false);
        }
    }

    /// <summary>キューが空になるまで待機する。マイグレーションツールから使用する。</summary>
    public async Task WhenDrainedAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _pending) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    /// <summary>ワーカーループを実行し、<paramref name="ct"/> がキャンセルされると終了する。</summary>
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
        catch (OperationCanceledException) { /* 通常のシャットダウン */ }
    }

    private async Task ProcessAsync(QueueItem item, CancellationToken ct)
    {
        var key = new EmbeddingTaskKey(
            item.Entity,
            item.TargetPropertyName,
            _provider.ProviderId,
            _options.NormalizationProfile);

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
                using (var transaction = _database.BeginWriteTransaction())
                {
                    transaction.SetVectorProperty(
                        item.Entity,
                        item.TargetPropertyName,
                        result.Vector.Span);
                    transaction.Commit();
                }
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

    private static IEnumerable<EntityRef> EnumerateEntities(
        IReadTransaction transaction,
        EntityKind kind)
        => kind switch
        {
            EntityKind.Vertex => transaction.Query.Vertices().ToList().Select(EntityRef.From),
            EntityKind.Edge => transaction.Query.Edges().ToList().Select(EntityRef.From),
            EntityKind.Nexus => transaction.Query.Nexuses().ToList().Select(EntityRef.From),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static bool TryReadStringProperty(
        IReadTransaction transaction,
        EntityRef entity,
        string propertyKey,
        out string text)
    {
        PropertyValue value;
        switch (entity.Kind)
        {
            case EntityKind.Vertex:
                var vertex = new VertexId(entity.Value);
                value = transaction.GetProperty(vertex, propertyKey);
                break;
            case EntityKind.Edge:
                var edge = new EdgeId(entity.Value);
                value = transaction.GetProperty(edge, propertyKey);
                break;
            case EntityKind.Nexus:
                var nexus = new NexusId(entity.Value);
                value = transaction.GetProperty(nexus, propertyKey);
                break;
            default:
                text = string.Empty;
                return false;
        }

        if (value.Type != PropertyValueType.String)
        {
            text = string.Empty;
            return false;
        }
        text = Encoding.UTF8.GetString(value.Utf8StringValue);
        return true;
    }

    private readonly record struct QueueItem(
        EntityRef Entity,
        string TargetPropertyName,
        PreparedText Prepared);
}
