using Microsoft.AspNetCore.DataProtection;

namespace BLL;

// Invoked explicitly by a worker/CLI in a later unit; never run during API startup.
public sealed class ActivationDispatcher(IActivationDeliveryStore store, IDataProtectionProvider protection,
    IConfiguration configuration, TimeProvider clock, IActivationEmailSender senderClient)
{
    public async Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default)
    {
        var ring = configuration["Activation:KeyRingPath"];
        var apiKey = configuration["Resend:ApiKey"];
        var sender = configuration["Resend:FromAddress"];
        var frontend = configuration["Activation:FrontendUrl"];
        if (string.IsNullOrWhiteSpace(ring) || !Path.IsPathFullyQualified(ring) || !Directory.Exists(ring) ||
            string.IsNullOrWhiteSpace(apiKey) || !apiKey.StartsWith("re_", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sender) || !sender.Contains('@') ||
            !Uri.TryCreate(frontend, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Activation delivery configuration is incomplete or unsafe.");

        var delivery = await store.LeaseDueAsync(clock.GetUtcNow(), cancellationToken);
        if (delivery is null) return false;
        // Do not log the decrypted secret, the URL, provider exceptions, or recipient addresses.
        var token = protection.CreateProtector("Static-X.ActivationDelivery.v1", delivery.InvitationId.ToString("N"))
            .Unprotect(delivery.ProtectedToken);
        if (!await store.IsCurrentAsync(delivery, clock.GetUtcNow(), cancellationToken)) return true;
        var link = uri.AbsoluteUri + "#activation=" + Uri.EscapeDataString(token);
        var accepted = false;
        try
        {
            accepted = await senderClient.SendAsync("activation/" + delivery.JobId.ToString("N"),
                sender, delivery.Email, link, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* Unknown acceptance: retry with the same provider idempotency key. */ }
        await store.CompleteAsync(delivery, clock.GetUtcNow(), accepted, cancellationToken);
        return true;
    }
}
