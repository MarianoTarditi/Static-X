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
[Route("api/student/me")]
[Authorize(Roles = "Student")]
[PaymentApi]
public sealed class StudentPaymentsController : ControllerBase
{
    private readonly IPaymentService payments;
    public StudentPaymentsController(IPaymentService payments) { this.payments = payments; }
    private int Gym => int.Parse(User.FindFirstValue("gym_id")!, CultureInfo.InvariantCulture);
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Returns only the ficha explicitly linked to the authenticated Student account.</summary>
    [AllowWithoutPayment]
    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken)
    {
        var result = await payments.OwnProfileAsync(Gym, AccountRole.Student, Actor, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Returns own live coverage independently of cookie issuance or account enablement.</summary>
    [AllowWithoutPayment]
    [HttpGet("coverage")]
    public async Task<IActionResult> Coverage(CancellationToken cancellationToken)
    {
        var result = await payments.OwnCoverageAsync(Gym, AccountRole.Student, Actor, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>Returns own ledger without internal void reason or operator information.</summary>
    [AllowWithoutPayment]
    [HttpGet("payments")]
    public async Task<IActionResult> History([FromQuery] PaymentListRequest request, CancellationToken cancellationToken) =>
        Ok(await payments.HistoryAsync(Gym, AccountRole.Student, Actor, null, request, cancellationToken));
}
