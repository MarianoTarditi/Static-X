using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BLL;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Static_X.API.Controllers;

/// <summary>Credentials for an existing account in one gym.</summary>
public sealed record LoginRequest
{
    [Range(1, int.MaxValue)] public required int GymId { get; init; }
    [Required, EmailAddress] public required string Email { get; init; }
    [Required] public required string Password { get; init; }
}

/// <summary>Authenticated account identity without credential material.</summary>
public sealed record AccountResponse(string Id, int GymId, string Email, AccountRole Role);

[ApiController]
[Route("api/account")]
public sealed class AccountController(AccountService accounts, IAntiforgery antiforgery) : ControllerBase
{
    // The antiforgery cookie is HttpOnly; the request token is returned only to same-origin callers.
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult Csrf()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AccountResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var account = await accounts.SignInAsync(request.GymId, request.Email, request.Password, cancellationToken);
        if (account is null)
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid credentials." });
        Response.Headers.CacheControl = "no-store";
        return Ok(new AccountResponse(account.Id, account.GymId, account.Email, account.Role));
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
        return Ok(new AccountResponse(account.Id, account.GymId, account.Email, account.Role));
    }
}
