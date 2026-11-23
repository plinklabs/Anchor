using Microsoft.AspNetCore.SignalR.Client;

namespace FocusAgent.Core.Realtime;

internal sealed class SignalRBackoffPolicy : IRetryPolicy
{
    private readonly TimeSpan _max;

    public SignalRBackoffPolicy(TimeSpan max) => _max = max;

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        // Exponential backoff starting at 1s, doubling, capped at _max.
        var attempt = retryContext.PreviousRetryCount;
        var seconds = Math.Min(_max.TotalSeconds, Math.Pow(2, Math.Min(attempt, 10)));
        return TimeSpan.FromSeconds(seconds);
    }
}
