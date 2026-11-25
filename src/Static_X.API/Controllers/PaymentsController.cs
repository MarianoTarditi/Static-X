using System.Globalization;
using System.Security.Claims;
using BLL.DTOs.Payments;
using BLL.Services.Interfaces;
using Entity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Auth;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/payments")]
[Authorize(Roles = "GymAdmin,Trainer")]
[PaymentApi]
public sealed class PaymentsController : ControllerBase
{
    private readonly IPaymentService payments;
    public PaymentsController(IPaymentService payments) { this.payments = payments; }

    private int Gym => int.Parse(User.FindFirstValue("gym_id")!, CultureInfo.InvariantCulture);
    private AccountRole Role => Enum.Parse<AccountRole>(User.FindFirstValue(ClaimTypes.Role)!);
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Returns active same-gym coverage counts and estimated remaining-month renewals, not debt.</summary>
    [HttpGet("summary")]
    [ProducesResponseType<PaymentSummaryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken) =>
        Ok(await payments.SummaryAsync(Gym, Role, cancellationToken));

    /// <summary>Returns inclusive paid-on collections and separate void totals without personal details.</summary>
    [Authorize(Roles = "GymAdmin")]
    [HttpGet("collections")]
    [ProducesResponseType<PaymentCollectionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Collections([FromQuery] PaymentCollectionRequest request, CancellationToken cancellationToken) =>
        Ok(await payments.CollectionsAsync(Gym, Role, request, cancellationToken));

    /// <summary>Lists same-gym student coverage with bounded name and status filters.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] PaymentListRequest request, CancellationToken cancellationToken) =>
        Ok(await payments.ListAsync(Gym, Role, request, cancellationToken));

    /// <summary>Returns immutable effective-date tariffs including tomorrow's pending price.</summary>
    [HttpGet("prices")]
    public async Task<IActionResult> Prices(CancellationToken cancellationToken) => Ok(await payments.PricesAsync(Gym, Role, cancellationToken));

    /// <summary>Sets the initial tariff or schedules tomorrow's immutable tariff in ARS cents.</summary>
    [Authorize(Roles = "GymAdmin")]
    [HttpPost("prices")]
    public async Task<IActionResult> SetPrice(SetPassPriceRequest request, CancellationToken cancellationToken) =>
        Ok(await payments.SetPriceAsync(Gym, Role, Actor, request, cancellationToken));

    /// <summary>Previews trusted price and coverage without writing a payment.</summary>
    [HttpPost("students/{studentId:int}/preview")]
    public async Task<IActionResult> Preview(int studentId, PaymentPreviewRequest request, CancellationToken cancellationToken) =>
        Ok(await payments.PreviewAsync(Gym, Role, Actor, studentId, request, cancellationToken));

    /// <summary>Confirms a protected preview idempotently and returns its current void state.</summary>
    [HttpPost("students/{studentId:int}")]
    public async Task<IActionResult> Register(int studentId, RegisterPaymentRequest request, CancellationToken cancellationToken)
    {
        var result = await payments.RegisterAsync(Gym, Role, Actor, studentId, request, cancellationToken);
        return CreatedAtAction(nameof(History), new { studentId }, result);
    }

    /// <summary>Returns a deterministic page of the student's complete manual payment history.</summary>
    [HttpGet("students/{studentId:int}")]
    public async Task<IActionResult> History(int studentId, [FromQuery] PaymentListRequest request, CancellationToken cancellationToken) =>
        Ok(await payments.HistoryAsync(Gym, Role, Actor, studentId, request, cancellationToken));

    /// <summary>Voids only the latest nonvoid payment while retaining its original ledger terms.</summary>
    [Authorize(Roles = "GymAdmin")]
    [HttpPost("students/{studentId:int}/{paymentId:long}/void")]
    public async Task<IActionResult> Void(int studentId, long paymentId, VoidPaymentRequest request, CancellationToken cancellationToken) =>
        await payments.VoidAsync(Gym, Role, Actor, studentId, paymentId, request, cancellationToken) ? NoContent() : NotFound();
}
