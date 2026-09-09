using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StackExchange.Redis;
namespace Service.Api.IntegrationTests.Fixtures;

// Transport fault control only: all protocol responses come from the real Redis server.
internal sealed class RedisProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private readonly ConcurrentBag<Task> workers = new();
    private readonly string host;
    private readonly int port;
    private readonly Task accept;
    private volatile bool reject;
    private TaskCompletionSource? gate;
    private string? watchedKey;
    private long observationGeneration;
    private TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object sync = new();
    public string Connection { get; }
    public RedisProxy(string upstream)
    {
        var options = ConfigurationOptions.Parse(upstream);
        var endpoint = options.EndPoints.Single();
        (host, port) = endpoint switch { IPEndPoint ip => (ip.Address.ToString(), ip.Port), DnsEndPoint dns => (dns.Host, dns.Port), _ => throw new NotSupportedException() };
        listener.Start();
        options.EndPoints.Clear(); options.EndPoints.Add(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        options.AbortOnConnectFail = false; options.ConnectTimeout = 500; options.ConnectRetry = 0;
        options.AsyncTimeout = 10000; options.SyncTimeout = 10000; options.ReconnectRetryPolicy = new LinearRetry(100);
        Connection = options.ToString();
        accept = Accept();
    }
    public Task PauseResponsesFor(string key) => ObserveRequestsFor(key, pauseResponses: true);

    public Task ObserveRequestsFor(string key, bool pauseResponses = false)
    {
        lock (sync)
        {
            observationGeneration++;
            watchedKey = key;
            observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            gate?.TrySetResult();
            gate = pauseResponses ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            return observed.Task;
        }
    }
    public void Resume()
    {
        lock (sync) { gate?.TrySetResult(); gate = null; watchedKey = null; }
        reject = false;
    }
    public void Disconnect()
    {
        reject = true;
        foreach (var client in clients.Keys) client.Dispose();
    }
    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (reject) { client.Dispose(); continue; }
                clients.TryAdd(client, 0); workers.Add(Forward(client));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task Forward(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        {
            clients.TryAdd(upstream, 0);
            try
            {
                await upstream.ConnectAsync(host, port, stop.Token);
                var outgoing = Copy(client.GetStream(), upstream.GetStream(), false);
                var incoming = Copy(upstream.GetStream(), client.GetStream(), true);
                await Task.WhenAny(outgoing, incoming);
                client.Dispose(); upstream.Dispose();
                await Task.WhenAll(outgoing, incoming);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { clients.TryRemove(client, out _); clients.TryRemove(upstream, out _); }
        }
    }
    private async Task Copy(NetworkStream source, NetworkStream destination, bool response)
    {
        var bytes = new byte[8192];
        var recent = "";
        long generation = -1;
        while (true)
        {
            var count = await source.ReadAsync(bytes, stop.Token);
            if (count == 0) return;
            Task? hold = null;
            TaskCompletionSource? match = null;
            lock (sync)
            {
                if (response) hold = gate?.Task;
                else
                {
                    // Each stream discards its history at the new observation boundary.
                    if (generation != observationGeneration)
                    {
                        recent = "";
                        generation = observationGeneration;
                    }
                    recent += Encoding.UTF8.GetString(bytes, 0, count);
                    if (watchedKey is not null && recent.Contains(watchedKey, StringComparison.Ordinal))
                        match = observed;
                    if (recent.Length > 16384) recent = recent[^8192..];
                }
            }
            if (hold is not null) await hold.WaitAsync(stop.Token);
            await destination.WriteAsync(bytes.AsMemory(0, count), stop.Token);
            // Signal only after forwarding the matching fresh request to the real provider.
            match?.TrySetResult();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Resume(); stop.Cancel(); listener.Stop();
        foreach (var client in clients.Keys) client.Dispose();
        await accept.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5));
        stop.Dispose();
    }
}
