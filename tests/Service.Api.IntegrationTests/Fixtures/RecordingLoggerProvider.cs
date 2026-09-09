using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Service.Api.IntegrationTests.Fixtures;

public sealed class RecordingLoggerProvider(params string[] additionalCategories) : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Recorder(Messages, categoryName, additionalCategories);
    public void Dispose() { }

    private sealed class Recorder(ConcurrentQueue<(string Category, LogLevel Level, string Message)> messages, string category, string[] additionalCategories) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (additionalCategories.Contains(category) || category is "Service.Api.Exceptions.ApiExceptionHandler"
                or "Service.Api.Services.ItemService"
                or "Service.Api.Services.ActionService"
                or "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware")
                messages.Enqueue((category, logLevel, formatter(state, exception) + exception));
        }
    }
}
