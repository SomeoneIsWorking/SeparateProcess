using System;
using Microsoft.Extensions.Logging;

namespace SeparateProcess;

public class ProcessLogger : ILogger
{
    private readonly Action<LogLevel, string> sendLog;

    public ProcessLogger(Action<LogLevel, string> sendLog)
    {
        this.sendLog = sendLog;
    }

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