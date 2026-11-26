using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;
using Microsoft.EntityFrameworkCore;

namespace DAL.Repositories.Implementation;

/// <summary>Persiste tarifas y pagos aislados por gimnasio con revisión y exclusión transaccional.</summary>
public sealed class PaymentRepository : IPaymentRepository
{
    private readonly StaticXDbContext context;

    /// <summary>Recibe el contexto de persistencia del alcance actual.</summary>
    public PaymentRepository(StaticXDbContext context) { this.context = context; }

    /// <summary>Compone agregados de cobertura de fichas activas usando el último pago no anulado por secuencia.</summary>
    private IQueryable<PaymentSummary> CoverageSummaryQuery(int gymId, DateOnly today, DateOnly soonEnd)
    {
        var latest = context.Payments.Where(p => p.GymId == gymId && p.VoidedAt == null &&
            !context.Payments.Any(next => next.GymId == gymId && next.StudentId == p.StudentId && next.VoidedAt == null && next.Sequence > p.Sequence));
        var rows = from student in context.Alumnos
                   where student.GymId == gymId && student.IsActive
                   join payment in latest on student.Id equals payment.StudentId into payments
                   from payment in payments.DefaultIfEmpty()
                   select new { End = (DateOnly?)payment.CoverageEndExclusive };
        return rows.GroupBy(r => 1).Select(group => new PaymentSummary
        {
            ActiveStudents = group.Count(),
            NoValidPayment = group.Sum(r => r.End == null ? 1 : 0),
            Expired = group.Sum(r => r.End <= today ? 1 : 0),
            DueSoon = group.Sum(r => r.End > today && r.End <= soonEnd ? 1 : 0),
            Current = group.Sum(r => r.End > soonEnd ? 1 : 0)
        });
    }

    /// <summary>Agrega una renovación por cobertura activa restante del mes y selecciona su tarifa efectiva.</summary>
    private IQueryable<PaymentSummary> RenewalSummaryQuery(int gymId, DateOnly today, DateOnly nextMonth)
    {
        var rows = context.Payments.Where(p => p.GymId == gymId && p.VoidedAt == null &&
            p.CoverageStart <= today && p.CoverageEndExclusive > today && p.CoverageEndExclusive < nextMonth &&
            context.Alumnos.Any(s => s.GymId == gymId && s.Id == p.StudentId && s.IsActive) &&
            !context.Payments.Any(next => next.GymId == gymId && next.StudentId == p.StudentId && next.VoidedAt == null && next.Sequence > p.Sequence))
            .Select(p => new
            {
                Amount = context.GymPassPrices.Where(price => price.GymId == gymId && price.EffectiveFrom <= p.CoverageEndExclusive)
                    .OrderByDescending(price => price.EffectiveFrom).Select(price => (long?)price.AmountCents).FirstOrDefault()
            });
        return rows.GroupBy(r => 1).Select(group => new PaymentSummary
        {
            RenewalCount = group.Count(),
            RenewalCents = group.Sum(r => r.Amount == null ? 1 : 0) > 0 ? null : group.Sum(r => r.Amount)
        });
    }

    /// <summary>Ejecuta agregados independientes sin cargar fichas ni garantizar una instantánea entre consultas.</summary>
    public async Task<PaymentSummary> SummaryAsync(int gymId, DateOnly today, DateOnly soonEnd, DateOnly nextMonth, CancellationToken cancellationToken)
    {
        var result = await CoverageSummaryQuery(gymId, today, soonEnd).SingleOrDefaultAsync(cancellationToken) ?? new PaymentSummary();
        var renewal = await RenewalSummaryQuery(gymId, today, nextMonth).SingleOrDefaultAsync(cancellationToken);
        result.RenewalCount = renewal?.RenewalCount ?? 0;
        result.RenewalCents = renewal is not null ? renewal.RenewalCents :
            await context.GymPassPrices.AnyAsync(p => p.GymId == gymId && p.EffectiveFrom <= today, cancellationToken) ? 0L : null;
        return result;
    }

    /// <summary>Agrupa importes originales por medio y estado de anulación dentro de fechas de pago inclusivas.</summary>
    private IQueryable<PaymentCollection> CollectionsQuery(int gymId, DateOnly from, DateOnly to) =>
        context.Payments.Where(p => p.GymId == gymId && p.PaidOn >= from && p.PaidOn <= to)
            .GroupBy(p => p.Method).Select(group => new PaymentCollection
            {
                Method = group.Key,
                ValidCount = group.Sum(p => p.VoidedAt == null ? 1 : 0),
                ValidCents = group.Sum(p => p.VoidedAt == null ? p.AmountCents : 0L),
                VoidCount = group.Sum(p => p.VoidedAt != null ? 1 : 0),
                VoidCents = group.Sum(p => p.VoidedAt != null ? p.AmountCents : 0L)
            });

    /// <summary>Materializa únicamente los agregados por medio sin excluir fichas inactivas del historial.</summary>
    public async Task<IReadOnlyList<PaymentCollection>> CollectionsAsync(int gymId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await CollectionsQuery(gymId, from, to).ToArrayAsync(cancellationToken);

    /// <summary>Obtiene la ficha, el último pago vigente y la tarifa aplicable a la fecha solicitada.</summary>
    public async Task<PaymentSnapshot?> SnapshotAsync(int gymId, int studentId, DateOnly paidOn, CancellationToken cancellationToken)
    {
        var student = await context.Alumnos.AsNoTracking().SingleOrDefaultAsync(s => s.GymId == gymId && s.Id == studentId, cancellationToken);
        if (student is null) return null;
        var last = await context.Payments.AsNoTracking().Where(p => p.GymId == gymId && p.StudentId == studentId && p.VoidedAt == null)
            .OrderByDescending(p => p.Sequence).FirstOrDefaultAsync(cancellationToken);
        var price = await context.GymPassPrices.AsNoTracking().Where(p => p.GymId == gymId && p.EffectiveFrom <= paidOn)
            .OrderByDescending(p => p.EffectiveFrom).FirstOrDefaultAsync(cancellationToken);
        return new PaymentSnapshot(student, last, price);
    }

    /// <summary>Busca exclusivamente el vínculo explícito entre cuenta y ficha del mismo gimnasio.</summary>
    public Task<Student?> FindOwnAsync(int gymId, string accountId, CancellationToken cancellationToken) =>
        context.Alumnos.AsNoTracking().SingleOrDefaultAsync(s => s.GymId == gymId && s.AccountId == accountId, cancellationToken);

    /// <summary>Busca el resultado original de una clave idempotente incluso después de su anulación.</summary>
    public Task<Payment?> FindReplayAsync(int gymId, string key, CancellationToken cancellationToken) =>
        context.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.GymId == gymId && p.IdempotencyKey == key, cancellationToken);

    /// <summary>Inserta una tarifa inmutable evitando fechas efectivas duplicadas bajo el mutex del gimnasio.</summary>
    public async Task<GymPassPrice> SetPriceAsync(GymPassPrice price, bool initial, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await TenantAccountLock.AcquireAsync(context, price.GymId, cancellationToken) != 1) throw new KeyNotFoundException();
        if (price.AmountCents is <= 0 or > 10000000000 || string.IsNullOrWhiteSpace(price.CreatedBy)) throw new ArgumentException("Invalid price.");
        var exists = await context.GymPassPrices.AnyAsync(p => p.GymId == price.GymId, cancellationToken);
        if (initial == exists || await context.GymPassPrices.AnyAsync(p => p.GymId == price.GymId && p.EffectiveFrom == price.EffectiveFrom, cancellationToken))
            throw new InvalidOperationException("A price already exists for that effective date. Retry on the next business day.");
        context.GymPassPrices.Add(price);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return price;
    }

    /// <summary>Registra una única extensión condicionada a la revisión, tarifa y ficha activas observadas.</summary>
    public async Task<Payment> RegisterAsync(Payment payment, long revision, DateOnly today, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await TenantAccountLock.AcquireAsync(context, payment.GymId, cancellationToken) != 1) throw new KeyNotFoundException();
        var replay = await FindReplayAsync(payment.GymId, payment.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.RequestFingerprint != payment.RequestFingerprint || replay.ActorId != payment.ActorId ||
                replay.StudentId != payment.StudentId || replay.PaidOn != payment.PaidOn || replay.Method != payment.Method)
                throw new InvalidOperationException("Idempotency conflict.");
            return replay;
        }
        var snapshot = await SnapshotAsync(payment.GymId, payment.StudentId, payment.PaidOn, cancellationToken);
        if (snapshot is null || !snapshot.Student.IsActive || snapshot.Student.PaymentRevision != revision ||
            snapshot.Price?.Id != payment.PriceId || snapshot.Price.AmountCents != payment.AmountCents ||
            snapshot.LastPayment?.Id != payment.PreviousPaymentId || payment.PaidOn > today ||
            (snapshot.LastPayment is not null && payment.PaidOn < snapshot.LastPayment.PaidOn) ||
            !Enum.IsDefined(payment.Method) || payment.Sequence != revision + 1 ||
            payment.CoverageEndExclusive != payment.ExtensionStart.AddDays(30) ||
            payment.ExtensionStart != (snapshot.LastPayment is not null && snapshot.LastPayment.CoverageEndExclusive > payment.PaidOn ? snapshot.LastPayment.CoverageEndExclusive : payment.PaidOn) ||
            payment.CoverageStart != (snapshot.LastPayment is not null && snapshot.LastPayment.CoverageEndExclusive >= payment.PaidOn ? snapshot.LastPayment.CoverageStart : payment.PaidOn) ||
            string.IsNullOrWhiteSpace(payment.ActorId) || payment.IdempotencyKey.Length is < 1 or > 128 || payment.RequestFingerprint.Length != 64)
            throw new InvalidOperationException("Payment state changed. Request a new preview.");
        var changed = await context.Alumnos.Where(s => s.GymId == payment.GymId && s.Id == payment.StudentId && s.IsActive && s.PaymentRevision == revision)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PaymentRevision, revision + 1), cancellationToken);
        if (changed != 1) throw new InvalidOperationException("Payment state changed.");
        context.Payments.Add(payment);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return payment;
    }

    /// <summary>Anula únicamente el último pago no anulado y avanza la revisión sin borrar el historial.</summary>
    public async Task<bool> VoidAsync(int gymId, int studentId, long paymentId, string actorId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await TenantAccountLock.AcquireAsync(context, gymId, cancellationToken) != 1) return false;
        var last = await context.Payments.AsNoTracking().Where(p => p.GymId == gymId && p.StudentId == studentId && p.VoidedAt == null)
            .OrderByDescending(p => p.Sequence).FirstOrDefaultAsync(cancellationToken);
        if (last?.Id != paymentId) throw new InvalidOperationException("Only the last nonvoid payment can be voided.");
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ArgumentException("Invalid void audit.");
        if (await context.Payments.Where(p => p.GymId == gymId && p.StudentId == studentId && p.Id == paymentId && p.VoidedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.VoidedAt, now).SetProperty(p => p.VoidedBy, actorId).SetProperty(p => p.VoidReason, reason), cancellationToken) != 1)
            throw new InvalidOperationException("Payment state changed.");
        await context.Alumnos.Where(s => s.GymId == gymId && s.Id == studentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PaymentRevision, x => x.PaymentRevision + 1), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Obtiene el historial de tarifas del gimnasio sin seguimiento de entidades.</summary>
    public async Task<IReadOnlyList<GymPassPrice>> PricesAsync(int gymId, CancellationToken cancellationToken) =>
        await context.GymPassPrices.AsNoTracking().Where(p => p.GymId == gymId).OrderBy(p => p.EffectiveFrom).ToArrayAsync(cancellationToken);

    /// <summary>Obtiene una página determinista del historial completo de pagos del alumno.</summary>
    public async Task<IReadOnlyList<Payment>> HistoryAsync(int gymId, int studentId, int skip, int take, CancellationToken cancellationToken) =>
        await context.Payments.AsNoTracking().Where(p => p.GymId == gymId && p.StudentId == studentId)
            .OrderByDescending(p => p.Sequence).Skip(skip).Take(take).ToArrayAsync(cancellationToken);

    /// <summary>Proyecta y pagina fichas con su último pago y filtra su cobertura sin consultas por fila.</summary>
    public async Task<PaymentStudentPage> ListAsync(int gymId, string? search, PaymentStatus? status, DateOnly today, int skip, int take, CancellationToken cancellationToken)
    {
        var query = context.Alumnos.AsNoTracking().Where(s => s.GymId == gymId);
        if (search is not null) query = query.Where(s => (s.FirstName + " " + s.LastName).Contains(search));
        var rows = query.Select(s => new
        {
            Student = s,
            Last = context.Payments.Where(p => p.GymId == gymId && p.StudentId == s.Id && p.VoidedAt == null).OrderByDescending(p => p.Sequence).FirstOrDefault()
        });
        var soonEnd = today.AddDays(6);
        if (status == PaymentStatus.NoPayments) rows = rows.Where(r => r.Last == null);
        if (status == PaymentStatus.Expired) rows = rows.Where(r => r.Last != null && r.Last.CoverageEndExclusive <= today);
        if (status == PaymentStatus.ExpiringSoon) rows = rows.Where(r => r.Last != null && r.Last.CoverageEndExclusive > today && r.Last.CoverageEndExclusive <= soonEnd);
        if (status == PaymentStatus.Current) rows = rows.Where(r => r.Last != null && r.Last.CoverageEndExclusive > soonEnd);
        var count = await rows.CountAsync(cancellationToken);
        var page = await rows.OrderBy(r => r.Student.LastName).ThenBy(r => r.Student.FirstName).ThenBy(r => r.Student.Id).Skip(skip).Take(take).ToArrayAsync(cancellationToken);
        var price = await context.GymPassPrices.AsNoTracking().Where(p => p.GymId == gymId && p.EffectiveFrom <= today).OrderByDescending(p => p.EffectiveFrom).FirstOrDefaultAsync(cancellationToken);
        return new PaymentStudentPage(page.Select(r => new PaymentSnapshot(r.Student, r.Last, price)).ToArray(), count);
    }
}
