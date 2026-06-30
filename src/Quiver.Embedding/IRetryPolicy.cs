namespace Quiver.Embedding;

public interface IRetryPolicy
{
    /// <summary>
    /// <paramref name="ex"/> の発生後に再試行するかを判定する。
    /// 再試行する場合は <see langword="true"/> と 0 以上の <paramref name="delay"/> を返し、
    /// 永続的な失敗とする場合は <see langword="false"/> を返す。
    /// <paramref name="attemptNumber"/> は 1 始まり。
    /// </summary>
    bool ShouldRetry(Exception ex, int attemptNumber, out TimeSpan delay);
}

/// <summary>
/// 短い固定間隔で再試行する。
/// 一時的な障害が数秒以内に解消することの多いローカル LLM プロバイダに適する。
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
