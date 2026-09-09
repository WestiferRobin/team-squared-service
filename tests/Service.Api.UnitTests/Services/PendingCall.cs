namespace Service.Api.UnitTests.Services;

// A dependency that has definitely started but cannot finish until the test releases it.
internal sealed class PendingCall : IDisposable
{
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Started => started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    public CancellationToken Token { get; private set; }

    public Task InvokeAsync(CancellationToken token)
    {
        Token = token;
        started.TrySetResult();
        return completion.Task.WaitAsync(token);
    }

    public void Complete() => completion.TrySetResult();
    public void Fail(Exception error) => completion.TrySetException(error);
    public void Dispose() => completion.TrySetResult();
}
