using BLL.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Static_X.API.Middleware;

// Registered only on the student API branch; existing account/activation behavior is unchanged.
public sealed class StudentApiErrorsMiddleware
{
    private readonly RequestDelegate next;
    private readonly ILogger<StudentApiErrorsMiddleware> logger;

    public StudentApiErrorsMiddleware(RequestDelegate next, ILogger<StudentApiErrorsMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted &&
            !(exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested))
        {
            var (status, title) = exception switch
            {
                StudentEmailConflictException => (409, "A student with this email already exists in this gym."),
                ArgumentException => (400, "Invalid student data."),
                _ => (500, "An unexpected error occurred.")
            };
            if (status == 500)
            {
                // Do not log exception messages or payloads: provider errors may contain PII/secrets.
                logger.LogError("Student API request failed. Trace identifier: {TraceIdentifier}", context.TraceIdentifier);
            }
            context.Response.Clear();
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title },
                options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
        }
    }
}
