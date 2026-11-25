using System.Security.Claims;
using BLL.DTOs.Accounts;
using BLL.Services.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    [Static_X.API.Auth.AllowWithoutPayment]
    [AllowAnonymous]
    public IActionResult Csrf()
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        return Ok(new CsrfResponse(antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
    }

    [HttpPost("login")]
    [Static_X.API.Auth.AllowWithoutPayment]
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
    [Static_X.API.Auth.AllowWithoutPayment]
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

    [HttpPost("change-password")]
    [Static_X.API.Auth.AllowWithoutPayment]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null || !AccountScope.TryGetGymId(User, out var gymId))
            return Forbid();
        Response.Headers.CacheControl = "no-store";
        if (!await accounts.ChangePasswordAsync(gymId, id, request, cancellationToken))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid password change request." });

        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return NoContent();
    }

    [HttpPost("forgot-password")]
    [Static_X.API.Auth.AllowWithoutPayment]
    [AllowAnonymous]
    [EnableRateLimiting("password-recovery")]
    [ProducesResponseType<ForgotPasswordResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        SecureResponse();
        try { return Accepted(await accounts.ForgotAsync(request, cancellationToken)); }
        catch (ArgumentException) { return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid recovery request." }); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new ProblemDetails { Status = 503, Title = "Password recovery is unavailable." }); }
    }

    [HttpPost("reset-password")]
    [Static_X.API.Auth.AllowWithoutPayment]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        SecureResponse();
        string? accountId;
        try { accountId = await accounts.ResetAsync(request, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new ProblemDetails { Status = 503, Title = "Password recovery is unavailable." }); }
        if (accountId is null)
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid password reset request." });
        // Do not sign out a different account that happens to open this recovery link.
        if (User.FindFirstValue(ClaimTypes.NameIdentifier) == accountId)
            await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return NoContent();
    }

    private void SecureResponse()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    [HttpGet("me")]
    [Static_X.API.Auth.AllowWithoutPayment]
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
