using BLL.DTOs.Payments;
using BLL.DTOs.Students;
using Entity;

namespace BLL.Services.Interfaces;

public interface IPaymentService
{
    Task<PaymentSummaryResponse> SummaryAsync(int gymId, AccountRole role, CancellationToken cancellationToken);
    Task<PaymentCollectionResponse> CollectionsAsync(int gymId, AccountRole role, PaymentCollectionRequest request, CancellationToken cancellationToken);
    Task<PaymentPreviewResponse> PreviewAsync(int gymId, AccountRole role, string actorId, int studentId, PaymentPreviewRequest request, CancellationToken cancellationToken);
    Task<PaymentResponse> RegisterAsync(int gymId, AccountRole role, string actorId, int studentId, RegisterPaymentRequest request, CancellationToken cancellationToken);
    Task<PassPriceResponse> SetPriceAsync(int gymId, AccountRole role, string actorId, SetPassPriceRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<PassPriceResponse>> PricesAsync(int gymId, AccountRole role, CancellationToken cancellationToken);
    Task<bool> VoidAsync(int gymId, AccountRole role, string actorId, int studentId, long paymentId, VoidPaymentRequest request, CancellationToken cancellationToken);
    Task<PaymentListResponse> ListAsync(int gymId, AccountRole role, PaymentListRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentResponse>> HistoryAsync(int gymId, AccountRole role, string actorId, int? studentId, PaymentListRequest request, CancellationToken cancellationToken);
    Task<StudentResponse?> OwnProfileAsync(int gymId, AccountRole role, string accountId, CancellationToken cancellationToken);
    Task<PaymentStudentResponse?> OwnCoverageAsync(int gymId, AccountRole role, string accountId, CancellationToken cancellationToken);
    Task<bool> HasCoverageAsync(int gymId, string accountId, CancellationToken cancellationToken);
}
