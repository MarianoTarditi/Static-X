using System.Security.Claims;
using BLL.DTOs.Accounts;
using BLL.Services.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/account")]
public sealed class AccountController : ControllerBase
{
    private readonly IAccountService accounts;
    private readonly IAntiforgery antiforgery;

    public AccountController(IAccountService accounts, IAntiforgery antiforgery)
    {
        this.accounts = accounts;
        this.antiforgery = antiforgery;
    }

    // The antiforgery cookie is HttpOnly; the request token is returned only to same-origin callers.
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult Csrf()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new CsrfResponse(antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AccountResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.SignInAsync(request.GymId, request, cancellationToken);
        if (account is null)
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid credentials." });
        Response.Headers.CacheControl = "no-store";
        return Ok(account);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AccountResponse>> Me(CancellationToken cancellationToken)
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var gym = User.FindFirstValue("gym_id");
        if (!int.TryParse(gym, out var gymId) || id is null)
            return Forbid();
        var account = await accounts.FindByIdAsync(gymId, id, cancellationToken);
        if (account is null || User.FindFirstValue(ClaimTypes.Role) != account.Role.ToString())
            return Forbid();
        Response.Headers.CacheControl = "no-store";
        return Ok(account);
    }
}
