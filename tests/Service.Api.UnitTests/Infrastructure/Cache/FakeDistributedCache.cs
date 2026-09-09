using Microsoft.Extensions.Caching.Distributed;

namespace Service.Api.UnitTests.Infrastructure.Cache;

internal sealed class FakeDistributedCache : IDistributedCache
{
    public List<string> Calls { get; } = [];
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public byte[]? Value { get; set; }
    public string? Key { get; private set; }
    public DistributedCacheEntryOptions? Options { get; private set; }
    public CancellationToken Token { get; private set; }
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
        Calls.Add("get"); Record(key, token);
        await WaitAsync(token);
        Finished.TrySetResult();
        return Value;
    }
    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Calls.Add("set"); Record(key, token);
        await WaitAsync(token);
        Value = value; Options = options; Finished.TrySetResult();
    }
    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        Calls.Add("remove"); Record(key, token);
        await WaitAsync(token);
        Value = null; Finished.TrySetResult();
    }
    private void Record(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Key = key; Token = token;
        if (Failure is not null) throw Failure;
    }
    public byte[]? Get(string key) => throw new NotSupportedException();
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
    public void Remove(string key) => throw new NotSupportedException();
    public void Refresh(string key) => throw new NotSupportedException();
    public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
}
