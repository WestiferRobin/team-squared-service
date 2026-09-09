using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Service.Api.Exceptions;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            NotFoundException => (404, "Not Found", exception.Message),
            RequestValidationException => (400, "Bad Request", exception.Message),
            _ => (500, "Internal Server Error", "An unexpected error occurred.")
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            // Raw exception messages/inner exceptions may contain connection credentials.
            logger.LogError("Unexpected {ExceptionType}; trace {TraceId}; stack {StackTrace}",
                exception.GetType().FullName, httpContext.TraceIdentifier, exception.StackTrace);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        if (!await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem
        }))
        {
            // Preserve the API error contract even for an unsupported Accept header.
            await httpContext.Response.WriteAsJsonAsync(problem,
                options: null, contentType: "application/problem+json",
                cancellationToken: cancellationToken);
        }

        return true;
    }
}
