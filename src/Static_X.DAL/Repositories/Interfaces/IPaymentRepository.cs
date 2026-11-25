using Entity;
using DAL.Repositories.Models;

namespace DAL.Repositories.Interfaces;

public interface IPaymentRepository
{
    Task<PaymentSummary> SummaryAsync(int gymId, DateOnly today, DateOnly soonEnd, DateOnly nextMonth, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentCollection>> CollectionsAsync(int gymId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
    Task<PaymentSnapshot?> SnapshotAsync(int gymId, int studentId, DateOnly paidOn, CancellationToken cancellationToken);
    Task<Alumno?> FindOwnAsync(int gymId, string accountId, CancellationToken cancellationToken);
    Task<Payment?> FindReplayAsync(int gymId, string key, CancellationToken cancellationToken);
    Task<Payment> RegisterAsync(Payment payment, long revision, DateOnly today, CancellationToken cancellationToken);
    Task<bool> VoidAsync(int gymId, int studentId, long paymentId, string actorId, string reason, DateTimeOffset now, CancellationToken cancellationToken);
    Task<GymPassPrice> SetPriceAsync(GymPassPrice price, bool initial, CancellationToken cancellationToken);
    Task<IReadOnlyList<GymPassPrice>> PricesAsync(int gymId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Payment>> HistoryAsync(int gymId, int studentId, int skip, int take, CancellationToken cancellationToken);
    Task<PaymentStudentPage> ListAsync(int gymId, string? search, PaymentStatus? status, DateOnly today, int skip, int take, CancellationToken cancellationToken);
}
