using BLL.Interfaces;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using DAL.Repositories.Models;

namespace BLL;

/// <summary>Entrega recuperaciones persistidas con configuración validada e idempotencia estable.</summary>
public sealed class PasswordRecoveryDispatcher : IPasswordRecoveryDispatcher
{
    private readonly IAccountRepository repository;
    private readonly IDataProtectionProvider protection;
    private readonly IConfiguration configuration;
    private readonly TimeProvider clock;
    private readonly IPasswordRecoveryEmailSender sender;

    /// <summary>Recibe persistencia, protección, configuración, reloj y transporte de recuperación.</summary>
    public PasswordRecoveryDispatcher(IAccountRepository repository, IDataProtectionProvider protection,
        IConfiguration configuration, TimeProvider clock, IPasswordRecoveryEmailSender sender)
    {
        this.repository = repository;
        this.protection = protection;
        this.configuration = configuration;
        this.clock = clock;
        this.sender = sender;
    }

    /// <summary>Ejecuta mantenimiento acotado sin consultar configuración ni reservar envíos.</summary>
    public Task CleanupAsync(CancellationToken cancellationToken = default) => repository.CleanupAsync(clock.GetUtcNow(), cancellationToken);

    /// <summary>Valida antes de reservar y congela el mensaje protegido antes de entregarlo o reintentarlo.</summary>
    public async Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default)
    {
        var ring = configuration["Activation:KeyRingPath"];
        var apiKey = configuration["Resend:ApiKey"];
        var from = configuration["Resend:FromAddress"];
        if (string.IsNullOrWhiteSpace(ring) || !Path.IsPathFullyQualified(ring) || !Directory.Exists(ring) ||
            string.IsNullOrWhiteSpace(apiKey) || !apiKey.StartsWith("re_", StringComparison.Ordinal) ||
            !System.Net.Mail.MailAddress.TryCreate(from, out var address) || address.Address != from ||
            !Uri.TryCreate(configuration["PasswordRecovery:FrontendUrl"], UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Recovery delivery configuration is incomplete or unsafe.");
        var delivery = await repository.LeaseDueAsync(clock.GetUtcNow(), cancellationToken);
        if (delivery is null) return false;
        var message = await ReadOrFreezeAsync(delivery, from!, uri, cancellationToken);
        if (message is null) return true;
        if (!await repository.IsCurrentAsync(delivery, clock.GetUtcNow(), cancellationToken)) return true;
        var accepted = false;
        try
        {
            accepted = await sender.SendAsync("password-recovery/" + delivery.RequestId.ToString("N"), message.From, message.To,
                message.Link, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* Unknown acceptance: retain the identical payload and provider key. */ }
        await repository.CompleteAsync(delivery, clock.GetUtcNow(), accepted, cancellationToken);
        return true;
    }

    /// <summary>Recupera el mensaje congelado o lo protege y persiste una sola vez bajo la reserva vigente.</summary>
    private async Task<RecoveryMessage?> ReadOrFreezeAsync(PasswordRecoveryDelivery delivery, string from, Uri frontend,
        CancellationToken cancellationToken)
    {
        var protector = protection.CreateProtector("Static-X.PasswordRecoveryMessage.v1", delivery.RequestId.ToString("N"));
        if (delivery.ProtectedPayload.StartsWith(PasswordRecoveryDelivery.SnapshotPrefix, StringComparison.Ordinal))
            return JsonSerializer.Deserialize<RecoveryMessage>(protector.Unprotect(delivery.ProtectedPayload[PasswordRecoveryDelivery.SnapshotPrefix.Length..]))
                ?? throw new InvalidOperationException("Recovery delivery snapshot is unavailable.");
        var token = protection.CreateProtector("Static-X.PasswordRecoveryDelivery.v1", delivery.RequestId.ToString("N"))
            .Unprotect(delivery.ProtectedPayload);
        var message = new RecoveryMessage(from, delivery.Email, frontend.AbsoluteUri + "#reset-password=" + Uri.EscapeDataString(token));
        var snapshot = PasswordRecoveryDelivery.SnapshotPrefix + protector.Protect(JsonSerializer.Serialize(message));
        return await repository.FreezeDeliveryAsync(delivery, snapshot, clock.GetUtcNow(), cancellationToken) ? message : null;
    }

    /// <summary>Conserva el remitente, el destinatario y el enlace exactos del primer intento de entrega.</summary>
    private sealed class RecoveryMessage
    {
        public string From { get; }
        public string To { get; }
        public string Link { get; }

        /// <summary>Inicializa los valores inmutables que se guardan exclusivamente dentro del sobre protegido.</summary>
        public RecoveryMessage(string from, string to, string link)
        {
            From = from;
            To = to;
            Link = link;
        }
    }
}
