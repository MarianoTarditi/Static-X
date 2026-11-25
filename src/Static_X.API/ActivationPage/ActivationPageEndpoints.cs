namespace Static_X.API.ActivationPage;

internal static class ActivationPageEndpoints
{
    public static void Map(WebApplication app)
    {
        MapResource(app, "/activate", "index.html", "text/html");
        MapResource(app, "/activate/activation.js", "activation.js", "text/javascript");
        MapResource(app, "/activate/activation.css", "activation.css", "text/css");
    }

    private static void MapResource(WebApplication app, string path, string resource, string contentType)
    {
        app.MapGet(path, IResult (HttpContext context) =>
        {
            // Routing itself accepts case and trailing-slash aliases.
            if (!context.Request.IsHttps || !string.Equals(context.Request.Path.Value, path, StringComparison.Ordinal))
                return TypedResults.NotFound();

            var headers = context.Response.Headers;
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; " +
                "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

            var stream = typeof(ActivationPageEndpoints).Assembly.GetManifestResourceStream(typeof(ActivationPageEndpoints), resource)
                ?? throw new InvalidOperationException("An embedded activation page resource is missing.");
            return TypedResults.File(stream, contentType + "; charset=utf-8");
        }).AllowAnonymous().ExcludeFromDescription();
    }
}
