using Microsoft.Extensions.Logging;

namespace AiAssistant.Application.Diagnostics;

/// <summary>
/// Central, sanitized trace for exceptions that are intentionally swallowed (timeouts, expected
/// cancellations, best-effort cleanup). Only the exception type and a fixed context are logged —
/// never the message, stack trace, file path or secret material. Configured once by the composition
/// root; silent when no host is running (e.g. in isolated unit tests).
/// </summary>
public static partial class SuppressedExceptionLog
{
    private static ILogger? _logger;

    public static void Configure(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _logger = factory.CreateLogger("AiAssistant.SuppressedExceptions");
    }

    [LoggerMessage(
        EventId = 9100,
        Level = LogLevel.Debug,
        Message = "Suppressed exception in {Context}: {ExceptionType}")]
    private static partial void LogSuppressed(ILogger logger, string context, string exceptionType, Exception? ex);

    /// <summary>Records a deliberately suppressed exception without exposing any payload from it.</summary>
    public static void Trace(string context, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var logger = _logger;
        if (logger is not null)
        {
            LogSuppressed(logger, context, exception.GetType().Name, exception);
        }
    }
}
