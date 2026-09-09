using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Service.Api.Exceptions;
using Xunit;
namespace Service.Api.UnitTests.Exceptions;
public class ApiExceptionHandlerTests
{
    [Theory]
    [InlineData(404, true)] [InlineData(400, true)] [InlineData(500, true)]
    [InlineData(404, false)] [InlineData(400, false)] [InlineData(500, false)]
    public async Task TryHandleAsync_WhenExceptionRaised_SelectsSafeProblemAndWriterBranch(int status, bool handled)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider, TraceIdentifier = "trace-test" };
        using var body = new MemoryStream();
        context.Response.Body = body;
        var logs = new RecordingLogger<ApiExceptionHandler>();
        var writer = new Writer { Handled = handled };
        Exception error = status switch { 404 => new NotFoundException("Safe missing"), 400 => new RequestValidationException("Safe invalid"), _ => Unexpected() };
        Assert.True(await new ApiExceptionHandler(writer, logs).TryHandleAsync(context, error, default));
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(1, writer.Calls);
        Assert.Same(context, writer.Context!.HttpContext);
        var problem = writer.Context.ProblemDetails;
        Assert.Equal(status, problem.Status);
        Assert.Equal(status switch { 404 => "Not Found", 400 => "Bad Request", _ => "Internal Server Error" }, problem.Title);
        Assert.Equal(status == 500 ? "An unexpected error occurred." : error.Message, problem.Detail);
        Assert.Empty(problem.Extensions);
        if (handled) Assert.Equal(0, body.Length);
        else
        {
            Assert.Equal("application/problem+json", context.Response.ContentType);
            using var json = JsonDocument.Parse(body.ToArray());
            Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(problem.Title, json.RootElement.GetProperty("title").GetString());
            Assert.Equal(problem.Detail, json.RootElement.GetProperty("detail").GetString());
            Assert.DoesNotContain("SECRET", json.RootElement.ToString());
        }
        if (status != 500) Assert.Empty(logs.Entries);
        else
        {
            var log = Assert.Single(logs.Entries);
            Assert.Equal(LogLevel.Error, log.Level);
            Assert.Null(log.Exception);
            Assert.Equal(typeof(InvalidOperationException).FullName, log.State["ExceptionType"]);
            Assert.Equal(context.TraceIdentifier, log.State["TraceId"]);
            Assert.NotNull(error.StackTrace);
            Assert.Equal(error.StackTrace, log.State["StackTrace"]);
            Assert.DoesNotContain("SECRET", log.Message + string.Join(" ", log.State.Values));
        }
    }
    [Fact]
    public async Task TryHandleAsync_WhenWriterThrows_PropagatesWithoutFallback()
    {
        var failure = new InvalidOperationException("Writer failure");
        var writer = new Writer { Failure = failure };
        var context = new DefaultHttpContext();
        using var body = new MemoryStream(); context.Response.Body = body;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ApiExceptionHandler(writer, new RecordingLogger<ApiExceptionHandler>()).TryHandleAsync(context, new NotFoundException("Missing"), default)));
        Assert.Equal(1, writer.Calls);
        Assert.Equal(0, body.Length);
    }
    [Fact]
    public async Task TryHandleAsync_WhenFallbackCancelled_PropagatesCallerCancellation()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream(); context.Response.Body = body;
        var writer = new Writer();
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new ApiExceptionHandler(writer, new RecordingLogger<ApiExceptionHandler>()).TryHandleAsync(context, new NotFoundException("Missing"), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(0, body.Length);
    }
    private static Exception Unexpected()
    {
        try { throw new InvalidOperationException("SECRET credentials", new Exception("SECRET inner")); }
        catch (Exception error) { error.Data["private"] = "SECRET data"; return error; }
    }
    private sealed class Writer : IProblemDetailsService
    {
        public bool Handled { get; init; }
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public ProblemDetailsContext? Context { get; private set; }
        public ValueTask WriteAsync(ProblemDetailsContext context) => throw new NotSupportedException();
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Calls++; Context = context;
            return Failure is null ? ValueTask.FromResult(Handled) : ValueTask.FromException<bool>(Failure);
        }
    }
}
