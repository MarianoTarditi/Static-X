using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AutoMapper;
using BLL.DTOs.Payments;
using BLL.DTOs.Students;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;
using Microsoft.AspNetCore.DataProtection;

namespace BLL;

/// <summary>Valida pagos manuales y calcula cobertura y tarifas según el calendario de Buenos Aires.</summary>
public sealed class PaymentService : IPaymentService
{
    private readonly IPaymentRepository repository;
    private readonly TimeProvider time;
    private readonly IMapper mapper;
    private readonly IDataProtector protector;
    private static readonly TimeZoneInfo BusinessZone = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    /// <summary>Recibe persistencia, reloj, mapeador y protección duradera de confirmaciones.</summary>
    public PaymentService(IPaymentRepository repository, TimeProvider time, IMapper mapper, IDataProtectionProvider protection)
    {
        this.repository = repository; this.time = time; this.mapper = mapper;
        protector = protection.CreateProtector("Static-X.PaymentPreview.v1");
    }

    /// <summary>Obtiene la fecha comercial sin depender de la zona horaria del servidor.</summary>
    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), BusinessZone).DateTime);

    /// <summary>Resume fichas activas y renovaciones estimadas con una única fecha comercial de referencia.</summary>
    public async Task<PaymentSummaryResponse> SummaryAsync(int gymId, AccountRole role, CancellationToken cancellationToken)
    {
        Staff(gymId, role);
        var today = Today();
        var nextMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        var result = await repository.SummaryAsync(gymId, today, today.AddDays(6), nextMonth, cancellationToken);
        return new PaymentSummaryResponse(today, result.ActiveStudents, result.NoValidPayment, result.Expired, result.DueSoon,
            result.Current, result.RenewalCount, result.RenewalCents);
    }

    /// <summary>Exige administrador y fechas inclusivas acotadas para informar cobros y anulaciones por medio.</summary>
    public async Task<PaymentCollectionResponse> CollectionsAsync(int gymId, AccountRole role, PaymentCollectionRequest request, CancellationToken cancellationToken)
    {
        Staff(gymId, role);
        if (role != AccountRole.GymAdmin) throw new UnauthorizedAccessException();
        ArgumentNullException.ThrowIfNull(request);
        if (request.From is null || request.To is null || request.From == default(DateOnly) || request.To == default(DateOnly) ||
            request.To < request.From || request.To.Value.DayNumber - request.From.Value.DayNumber >= 366)
            throw new ArgumentException("Provide an inclusive paid-on range of 1..366 days.");
        var today = Today();
        var rows = await repository.CollectionsAsync(gymId, request.From.Value, request.To.Value, cancellationToken);
        var methods = Enum.GetValues<PaymentMethod>().Select(method =>
        {
            var row = rows.SingleOrDefault(r => r.Method == method);
            return new PaymentCollectionMethodResponse(method, row?.ValidCount ?? 0, row?.ValidCents ?? 0, row?.VoidCount ?? 0, row?.VoidCents ?? 0);
        }).ToArray();
        return new PaymentCollectionResponse(today, request.From.Value, request.To.Value, methods);
    }

    /// <summary>Exige autoridad de personal y un gimnasio confiable válido.</summary>
    private static void Staff(int gymId, AccountRole role)
    {
        if (gymId <= 0 || role is not (AccountRole.GymAdmin or AccountRole.Trainer)) throw new UnauthorizedAccessException();
    }

    /// <summary>Valida identificadores opacos de operadores y cuentas.</summary>
    private static void Actor(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 450) throw new ArgumentException("Invalid account identifier.");
    }

    /// <summary>Valida los límites de paginación sin desbordamiento.</summary>
    private static int Offset(PaymentListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Page < 1 || request.PageSize is < 1 or > 100 || (long)(request.Page - 1) * request.PageSize > int.MaxValue ||
            request.Search?.Length > 320 || (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))) throw new ArgumentException("Invalid payment query.");
        return (request.Page - 1) * request.PageSize;
    }

    /// <summary>Valida la fecha y el medio del pago y exige una ficha activa con tarifa histórica.</summary>
    private async Task<PaymentSnapshot> ValidateAsync(int gymId, int studentId, DateOnly paidOn, PaymentMethod method, CancellationToken cancellationToken)
    {
        if (studentId <= 0 || paidOn == default || paidOn > Today() || !Enum.IsDefined(method)) throw new ArgumentException("Invalid payment date or method.");
        var snapshot = await repository.SnapshotAsync(gymId, studentId, paidOn, cancellationToken) ?? throw new KeyNotFoundException();
        if (!snapshot.Student.IsActive) throw new InvalidOperationException("The student ficha must be active.");
        if (snapshot.Price is null) throw new InvalidOperationException("Configure an initial tariff covering the payment date.");
        if (snapshot.LastPayment is not null && paidOn < snapshot.LastPayment.PaidOn) throw new ArgumentException("Payment date precedes the last payment.");
        return snapshot;
    }

    /// <summary>Construye una extensión de treinta días preservando el inicio del intervalo continuo.</summary>
    private static Payment Terms(PaymentSnapshot snapshot, DateOnly paidOn, PaymentMethod method)
    {
        var prior = snapshot.LastPayment;
        var start = prior is not null && prior.CoverageEndExclusive > paidOn ? prior.CoverageEndExclusive : paidOn;
        return new Payment
        {
            GymId = snapshot.Student.GymId,
            StudentId = snapshot.Student.Id,
            Sequence = checked(snapshot.Student.PaymentRevision + 1),
            PreviousPaymentId = prior?.Id,
            PriceId = snapshot.Price!.Id,
            AmountCents = snapshot.Price.AmountCents,
            PaidOn = paidOn,
            Method = method,
            ExtensionStart = start,
            CoverageStart = prior is not null && prior.CoverageEndExclusive >= paidOn ? prior.CoverageStart : paidOn,
            CoverageEndExclusive = start.AddDays(30)
        };
    }

    /// <summary>Vincula la confirmación al actor, fecha comercial, revisión y términos calculados.</summary>
    private string Confirmation(Payment payment, string actorId, long revision) =>
        FormattableString.Invariant($"{payment.GymId}|{payment.StudentId}|{actorId}|{revision}|{payment.PriceId}|{payment.AmountCents}|{payment.PaidOn:yyyy-MM-dd}|{(int)payment.Method}|{payment.CoverageStart:yyyy-MM-dd}|{payment.ExtensionStart:yyyy-MM-dd}|{payment.CoverageEndExclusive:yyyy-MM-dd}|{Today():yyyy-MM-dd}");

    /// <summary>Prepara términos confiables y protege su revisión contra alteraciones del cliente.</summary>
    public async Task<PaymentPreviewResponse> PreviewAsync(int gymId, AccountRole role, string actorId, int studentId, PaymentPreviewRequest request, CancellationToken cancellationToken)
    {
        Staff(gymId, role); Actor(actorId); ArgumentNullException.ThrowIfNull(request);
        var snapshot = await ValidateAsync(gymId, studentId, request.PaidOn, request.Method, cancellationToken);
        var terms = Terms(snapshot, request.PaidOn, request.Method);
        return new PaymentPreviewResponse(snapshot.Student.PaymentRevision, terms.AmountCents, terms.ExtensionStart, terms.CoverageStart, terms.CoverageEndExclusive,
            protector.Protect(Confirmation(terms, actorId, snapshot.Student.PaymentRevision)));
    }

    /// <summary>Reproduce claves idénticas o registra una confirmación vigente sin aceptar precio del cliente.</summary>
    public async Task<PaymentResponse> RegisterAsync(int gymId, AccountRole role, string actorId, int studentId, RegisterPaymentRequest request, CancellationToken cancellationToken)
    {
        Staff(gymId, role); Actor(actorId); ArgumentNullException.ThrowIfNull(request);
        if (studentId <= 0 || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 ||
            string.IsNullOrWhiteSpace(request.PreviewToken) || request.PreviewToken.Length > 4096) throw new ArgumentException("Invalid confirmation.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{actorId}|{studentId}|{request.PaidOn:yyyy-MM-dd}|{(int)request.Method}|{request.PreviewToken}"))));
        var replay = await ReplayAsync(gymId, actorId, request.IdempotencyKey, fingerprint, cancellationToken);
        if (replay is not null) return replay;
        try
        {
            var snapshot = await ValidateAsync(gymId, studentId, request.PaidOn, request.Method, cancellationToken);
            var payment = Terms(snapshot, request.PaidOn, request.Method);
            string confirmation;
            try { confirmation = protector.Unprotect(request.PreviewToken); }
            catch (CryptographicException) { throw new InvalidOperationException("Invalid preview. Request a new preview."); }
            if (confirmation != Confirmation(payment, actorId, snapshot.Student.PaymentRevision)) throw new InvalidOperationException("Stale preview. Request a new preview.");
            payment.ActorId = actorId; payment.IdempotencyKey = request.IdempotencyKey; payment.RequestFingerprint = fingerprint; payment.CreatedAt = time.GetUtcNow();
            return mapper.Map<PaymentResponse>(await repository.RegisterAsync(payment, snapshot.Student.PaymentRevision, Today(), cancellationToken));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Another identical request may have committed between the first replay lookup and snapshot validation.
            replay = await ReplayAsync(gymId, actorId, request.IdempotencyKey, fingerprint, cancellationToken);
            if (replay is not null) return replay;
            throw;
        }
    }

    /// <summary>Reproduce únicamente la solicitud persistida del mismo actor y contenido confirmado.</summary>
    private async Task<PaymentResponse?> ReplayAsync(int gymId, string actorId, string key, string fingerprint, CancellationToken cancellationToken)
    {
        var replay = await repository.FindReplayAsync(gymId, key, cancellationToken);
        if (replay is null) return null;
        if (replay.ActorId != actorId || replay.RequestFingerprint != fingerprint) throw new InvalidOperationException("Idempotency key already used with different confirmation.");
        return mapper.Map<PaymentResponse>(replay);
    }

    /// <summary>Permite al administrador fijar la tarifa inicial o programar una nueva para mañana.</summary>
    public async Task<PassPriceResponse> SetPriceAsync(int gymId, AccountRole role, string actorId, SetPassPriceRequest request, CancellationToken cancellationToken)
    {
        Staff(gymId, role); if (role != AccountRole.GymAdmin) throw new UnauthorizedAccessException(); Actor(actorId); ArgumentNullException.ThrowIfNull(request);
        if (request.AmountCents is <= 0 or > 10000000000) throw new ArgumentException("Price must be 1..10000000000 ARS cents.");
        var initial = (await repository.PricesAsync(gymId, cancellationToken)).Count == 0;
        var effective = initial ? request.InitialEffectiveFrom ?? throw new ArgumentException("Initial effective date is required.") : Today().AddDays(1);
        if (initial && (effective == default || effective > Today()) || !initial && request.InitialEffectiveFrom.HasValue) throw new ArgumentException("Invalid effective date.");
        var price = new GymPassPrice { GymId = gymId, AmountCents = request.AmountCents, EffectiveFrom = effective, CreatedBy = actorId, CreatedAt = time.GetUtcNow() };
        return mapper.Map<PassPriceResponse>(await repository.SetPriceAsync(price, initial, cancellationToken));
    }

    /// <summary>Obtiene las tarifas históricas y programadas para el personal del gimnasio.</summary>
    public async Task<IReadOnlyList<PassPriceResponse>> PricesAsync(int gymId, AccountRole role, CancellationToken cancellationToken)
    { Staff(gymId, role); return mapper.Map<PassPriceResponse[]>(await repository.PricesAsync(gymId, cancellationToken)); }

    /// <summary>Exige administrador y motivo acotado antes de anular el último pago vigente.</summary>
    public Task<bool> VoidAsync(int gymId, AccountRole role, string actorId, int studentId, long paymentId, VoidPaymentRequest request, CancellationToken cancellationToken)
    {
        Staff(gymId, role); if (role != AccountRole.GymAdmin) throw new UnauthorizedAccessException(); Actor(actorId); ArgumentNullException.ThrowIfNull(request);
        if (studentId <= 0 || paymentId <= 0 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500) throw new ArgumentException("A void reason of 1..500 characters is required.");
        return repository.VoidAsync(gymId, studentId, paymentId, actorId, request.Reason.Trim(), time.GetUtcNow(), cancellationToken);
    }

    /// <summary>Calcula el estado para la fecha comercial indicada y separa la tarifa actual del importe histórico.</summary>
    private PaymentStudentResponse Summary(PaymentSnapshot snapshot, DateOnly today)
    {
        var last = snapshot.LastPayment; var through = last?.CoverageEndExclusive.AddDays(-1);
        var status = last is null ? PaymentStatus.NoPayments : today >= last.CoverageEndExclusive ? PaymentStatus.Expired :
            through!.Value.DayNumber - today.DayNumber <= 5 ? PaymentStatus.ExpiringSoon : PaymentStatus.Current;
        return new PaymentStudentResponse(snapshot.Student.Id, snapshot.Student.FirstName + " " + snapshot.Student.LastName, snapshot.Student.IsActive, snapshot.Price?.AmountCents,
            last is null ? null : mapper.Map<PaymentResponse>(last), today, through, last?.CoverageEndExclusive, status);
    }

    /// <summary>Devuelve la tabla paginada del gimnasio con filtros validados independientemente de HTTP.</summary>
    public async Task<PaymentListResponse> ListAsync(int gymId, AccountRole role, PaymentListRequest request, CancellationToken cancellationToken)
    {
        Staff(gymId, role); var skip = Offset(request);
        var today = Today();
        var result = await repository.ListAsync(gymId, string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(), request.Status, today, skip, request.PageSize, cancellationToken);
        return new PaymentListResponse(result.Items.Select(snapshot => Summary(snapshot, today)).ToArray(), request.Page, request.PageSize, result.TotalCount);
    }

    /// <summary>Resuelve la ficha propia exclusivamente por vínculo explícito y rol Student.</summary>
    private Task<Student?> OwnAsync(int gymId, AccountRole role, string accountId, CancellationToken cancellationToken)
    {
        if (gymId <= 0 || role != AccountRole.Student) throw new UnauthorizedAccessException(); Actor(accountId);
        return repository.FindOwnAsync(gymId, accountId, cancellationToken);
    }

    /// <summary>Obtiene el historial paginado y rechaza los filtros exclusivos de la tabla de alumnos.</summary>
    public async Task<IReadOnlyList<PaymentResponse>> HistoryAsync(int gymId, AccountRole role, string actorId, int? studentId, PaymentListRequest request, CancellationToken cancellationToken)
    {
        Actor(actorId); var skip = Offset(request);
        if (request.Search is not null || request.Status.HasValue)
            throw new ArgumentException("Payment history supports pagination only.", nameof(request));
        if (role == AccountRole.Student)
        {
            if (studentId.HasValue) throw new UnauthorizedAccessException();
            studentId = (await OwnAsync(gymId, role, actorId, cancellationToken))?.Id ?? throw new KeyNotFoundException();
        }
        else Staff(gymId, role);
        if (studentId is null or <= 0) throw new ArgumentException("Invalid student identifier.");
        if (await repository.SnapshotAsync(gymId, studentId.Value, Today(), cancellationToken) is null) throw new KeyNotFoundException();
        var rows = await repository.HistoryAsync(gymId, studentId.Value, skip, request.PageSize, cancellationToken);
        var responses = mapper.Map<PaymentResponse[]>(rows);
        if (role != AccountRole.Student)
            for (var index = 0; index < rows.Count; index++)
                responses[index].Audit = new PaymentAuditResponse(rows[index].ActorId, rows[index].CreatedAt, rows[index].VoidedBy, rows[index].VoidedAt, rows[index].VoidReason);
        return responses;
    }

    /// <summary>Devuelve la ficha propia sin inferir identidad a partir del correo de contacto.</summary>
    public async Task<StudentResponse?> OwnProfileAsync(int gymId, AccountRole role, string accountId, CancellationToken cancellationToken)
    { var student = await OwnAsync(gymId, role, accountId, cancellationToken); return student is null ? null : mapper.Map<StudentResponse>(student); }

    /// <summary>Consulta en persistencia el estado de cobertura propio en cada solicitud.</summary>
    public async Task<PaymentStudentResponse?> OwnCoverageAsync(int gymId, AccountRole role, string accountId, CancellationToken cancellationToken)
    {
        var student = await OwnAsync(gymId, role, accountId, cancellationToken);
        if (student is null) return null;
        var snapshot = await repository.SnapshotAsync(gymId, student.Id, Today(), cancellationToken);
        return snapshot is null ? null : Summary(snapshot, Today());
    }

    /// <summary>Exige ficha activa y cobertura persistida vigente sin modificar autenticación ni cookies.</summary>
    public async Task<bool> HasCoverageAsync(int gymId, string accountId, CancellationToken cancellationToken)
    {
        var summary = await OwnCoverageAsync(gymId, AccountRole.Student, accountId, cancellationToken);
        return summary is { Active: true, Status: PaymentStatus.Current or PaymentStatus.ExpiringSoon } && summary.LastPayment!.CoverageStart <= Today();
    }
}
