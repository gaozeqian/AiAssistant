namespace AiAssistant.LocalApi;

/// <summary>Fixed one-minute sliding window used to bound local request volume and token guessing.</summary>
internal sealed class SlidingWindowRateLimiter(int maxRequestsPerMinute)
{
    private readonly Queue<DateTimeOffset> _hits = new();
    private readonly Lock _sync = new();

    public int MaxRequestsPerMinute { get; } = Math.Max(1, maxRequestsPerMinute);

    public bool TryAcquire(DateTimeOffset now, out int retryAfterSeconds)
    {
        lock (_sync)
        {
            var windowStart = now.AddMinutes(-1);
            while (_hits.Count > 0 && _hits.Peek() <= windowStart)
            {
                _hits.Dequeue();
            }

            if (_hits.Count >= MaxRequestsPerMinute)
            {
                var wait = _hits.Peek().AddMinutes(1) - now;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
                return false;
            }

            _hits.Enqueue(now);
            retryAfterSeconds = 0;
            return true;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _hits.Clear();
        }
    }
}
