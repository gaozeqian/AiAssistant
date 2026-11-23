namespace AiAssistant.Testing.Common;

/// <summary>Deterministic async assertions with bounded polling instead of arbitrary sleeps.</summary>
public static class AsyncAssert
{
    public static async Task EventuallyAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(message ?? "Condition was not satisfied before the timeout.");
    }

    public static async Task<T> CompletesWithinAsync<T>(Task<T> task, TimeSpan timeout, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(message ?? $"Task did not complete within {timeout}.");
        }
    }

    public static async Task CompletesWithinAsync(Task task, TimeSpan timeout, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(message ?? $"Task did not complete within {timeout}.");
        }
    }
}
