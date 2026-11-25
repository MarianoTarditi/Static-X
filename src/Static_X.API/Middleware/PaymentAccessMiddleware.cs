using System.Security.Claims;
using BLL.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Auth;

namespace Static_X.API.Middleware;

public sealed class PaymentAccessMiddleware
{
    private readonly RequestDelegate next;
    private readonly ILogger<PaymentAccessMiddleware> logger;

    public PaymentAccessMiddleware(RequestDelegate next, ILogger<PaymentAccessMiddleware> logger)
    { this.next = next; this.logger = logger; }

    public async Task InvokeAsync(HttpContext context, IPaymentService payments)
    {
        var endpoint = context.GetEndpoint();
        var paymentApi = endpoint?.Metadata.GetMetadata<PaymentApiAttribute>() is not null;
        if (paymentApi) context.Response.Headers.CacheControl = "no-store";
        if (context.User.Identity?.IsAuthenticated == true && context.User.IsInRole("Student") &&
            endpoint?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() is not null &&
            endpoint.Metadata.GetMetadata<AllowWithoutPaymentAttribute>() is null)
        {
            try
            {
                if (!AccountScope.TryGetGymId(context.User, out var gym) || gym is null ||
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { } account ||
                    !await payments.HasCoverageAsync(gym.Value, account, context.RequestAborted))
                { await Error(context, 403, "Active payment coverage is required."); return; }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Live payment coverage lookup failed.");
                await Error(context, 503, "Payment coverage is temporarily unavailable."); return;
            }
        }
        if (!paymentApi) { await next(context); return; }
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { await Error(context, 403, "Payment operation is not permitted."); }
        catch (KeyNotFoundException) { await Error(context, 404, "Payment target not found."); }
        catch (ArgumentException) { await Error(context, 400, "Invalid payment request."); }
        catch (InvalidOperationException) { await Error(context, 409, "Payment state conflicts with this request. Request a new preview; tariff dates cannot be replaced."); }
        catch (Exception exception)
        {
            logger.LogError(exception, "Payment operation failed.");
            await Error(context, 503, "Payment service is temporarily unavailable.");
        }
    }

    private static Task Error(HttpContext context, int status, string title)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title },
            options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
