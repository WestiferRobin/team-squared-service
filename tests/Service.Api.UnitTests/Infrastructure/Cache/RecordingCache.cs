using Service.Api.Infrastructure.Cache;
namespace Service.Api.UnitTests.Infrastructure.Cache;
internal sealed class RecordingCache : ICache
{
    public List<(string Operation, string Key, Type? Type, object? Value, TimeSpan? Ttl, CancellationToken Token)> Calls { get; } = [];
    public object? Result { get; set; }
    public Exception? Failure { get; set; }
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        Calls.Add(("get", key, typeof(T), null, null, cancellationToken));
        return Failure is null ? Task.FromResult((T?)Result) : Task.FromException<T?>(Failure);
    }
    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        Calls.Add(("set", key, typeof(T), value, ttl, cancellationToken));
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Calls.Add(("remove", key, null, null, null, cancellationToken));
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
