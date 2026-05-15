namespace Quiver.Embedding;

public interface IRetryPolicy
{
    /// <summary>
    /// Decide whether to retry after <paramref name="ex"/>. Returns true and a
    /// non-negative <paramref name="delay"/> to retry; false to mark the task
    /// as permanently failed. <paramref name="attemptNumber"/> is 1-based.
    /// </summary>
    bool ShouldRetry(Exception ex, int attemptNumber, out TimeSpan delay);
}

/// <summary>
/// Short fixed-interval retry; suitable for local-LLM providers where a
/// transient failure usually clears within a second or two.
/// </summary>
public sealed class LocalLmRetryPolicy(int maxAttempts = 3, int delayMs = 250) : IRetryPolicy
{
    public bool ShouldRetry(Exception ex, int attemptNumber, out TimeSpan delay)
    {
        if (attemptNumber >= maxAttempts || ex is OperationCanceledException)
        {
            delay = TimeSpan.Zero;
            return false;
        }
        delay = TimeSpan.FromMilliseconds(delayMs);
        return true;
    }
}

public sealed class NoRetryPolicy : IRetryPolicy
{
    public bool ShouldRetry(Exception ex, int attemptNumber, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        return false;
    }
}
