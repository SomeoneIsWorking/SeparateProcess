using Microsoft.Extensions.Logging;

namespace SeparateProcess;

public class ProcessLogger(Action<LogLevel, string> sendLog) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        sendLog(logLevel, message);
    }
}

public class ProcessLoggerProvider(Action<LogLevel, string> sendLog) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ProcessLogger(sendLog);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}