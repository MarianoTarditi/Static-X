using Microsoft.AspNetCore.Mvc;

namespace Static_X.API.Middleware;

public sealed class InvitationApiErrorsMiddleware
{
    private readonly RequestDelegate next;
    private readonly ILogger<InvitationApiErrorsMiddleware> logger;

    public InvitationApiErrorsMiddleware(RequestDelegate next, ILogger<InvitationApiErrorsMiddleware> logger)
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
        try { await next(context); }
        catch (Exception exception) when (!context.Response.HasStarted &&
            !(exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested))
        {
            var (status, title) = exception switch
            {
                UnauthorizedAccessException => (403, "Invitation access forbidden."),
                ArgumentException => (400, "Invalid invitation request."),
                BLL.Exceptions.InvitationCooldownException => (409, "Invitation resend is available one hour after last queueing."),
                BLL.Exceptions.ProvisioningConflictException => (409, "Invitation conflicts with existing state."),
                InvalidOperationException => (503, "Invitation service unavailable."),
                _ => (500, "An unexpected error occurred.")
            };
            if (status >= 500) logger.LogError("Invitation request failed. Trace identifier: {TraceIdentifier}", context.TraceIdentifier);
            context.Response.Clear();
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title },
                options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
        }
    }
}
