using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Service.Api.IntegrationTests.Fixtures;

public sealed class RecordingLoggerProvider(params string[] additionalCategories) : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Messages { get; } = new();
    public ConcurrentQueue<(string Category, LogLevel Level, Exception? Exception, IReadOnlyDictionary<string, object?> State)> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Recorder(Messages, Entries, categoryName, additionalCategories);
    public void Dispose() { }

    private sealed class Recorder(ConcurrentQueue<(string Category, LogLevel Level, string Message)> messages, ConcurrentQueue<(string Category, LogLevel Level, Exception? Exception, IReadOnlyDictionary<string, object?> State)> entries, string category, string[] additionalCategories) : ILogger
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
            {
                messages.Enqueue((category, logLevel, formatter(state, exception) + exception));
                entries.Enqueue((category, logLevel, exception,
                    state is IEnumerable<KeyValuePair<string, object?>> properties
                        ? properties.ToDictionary(pair => pair.Key, pair => pair.Value)
                        : new Dictionary<string, object?>()));
            }
        }
    }
}
