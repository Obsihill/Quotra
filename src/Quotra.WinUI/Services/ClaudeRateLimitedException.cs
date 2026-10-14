namespace Quotra.WinUI.Services;

internal sealed class ClaudeRateLimitedException(string message, DateTimeOffset retryAt)
    : InvalidOperationException(message)
{
    internal DateTimeOffset RetryAt { get; } = retryAt;
}
