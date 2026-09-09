using Microsoft.Extensions.Caching.Distributed;

namespace Service.Api.UnitTests.Infrastructure.Cache;

internal sealed class FakeDistributedCache : IDistributedCache
{
    public byte[]? Value { get; set; }
    public string? Key { get; private set; }
    public DistributedCacheEntryOptions? Options { get; private set; }
    public CancellationToken Token { get; private set; }
    public bool Fail { get; set; }
    public Exception? Failure { get; set; }
    public Task? PendingOperation { get; set; }
    public bool ObserveCancellation { get; set; } = true;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private async Task WaitAsync(CancellationToken token)
    {
        Started.TrySetResult();
        if (PendingOperation is not null)
            await (ObserveCancellation ? PendingOperation.WaitAsync(token) : PendingOperation);
    }
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        Record(key, token);
        await WaitAsync(token);
        return Value;
    }
    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Record(key, token);
        await WaitAsync(token);
        Value = value; Options = options;
    }
    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        Record(key, token);
        await WaitAsync(token);
        Value = null;
    }
    private void Record(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Key = key; Token = token;
        if (Failure is not null) throw Failure;
        if (Fail) throw new TimeoutException("Test failure");
    }
    public byte[]? Get(string key) => throw new NotSupportedException();
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
    public void Remove(string key) => throw new NotSupportedException();
    public void Refresh(string key) => throw new NotSupportedException();
    public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
}
