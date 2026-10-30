using Microsoft.AspNetCore.Mvc.Filters;

namespace Static_X.API.Filters;

/// <summary>Prevents caching administrative query results, including framework-generated errors.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AdministrativeQueryResponseAttribute : Attribute, IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
