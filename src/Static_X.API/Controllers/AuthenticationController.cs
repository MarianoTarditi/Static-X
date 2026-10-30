using System.Security.Claims;
using BLL.DTOs.Accounts;
using BLL.Services.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Auth;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/account")]
public sealed class AuthenticationController : ControllerBase
{
    private readonly IAccountService accounts;
    private readonly IAntiforgery antiforgery;

    public AuthenticationController(IAccountService accounts, IAntiforgery antiforgery)
    {
        this.accounts = accounts;
        this.antiforgery = antiforgery;
    }

    // The antiforgery cookie is HttpOnly; the request token is returned only to same-origin callers.
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult Csrf()
    {
        Response.Headers.CacheControl = "no-cache, no-store";
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

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        Response.Headers.CacheControl = "no-store";
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AccountResponse>> Me(CancellationToken cancellationToken)
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!AccountScope.TryGetGymId(User, out var gymId) || id is null)
            return Forbid();
        var account = await accounts.FindByIdAsync(gymId, id, cancellationToken);
        if (account is null || User.FindFirstValue(ClaimTypes.Role) != account.Role.ToString())
            return Forbid();
        Response.Headers.CacheControl = "no-store";
        return Ok(account);
    }
}
