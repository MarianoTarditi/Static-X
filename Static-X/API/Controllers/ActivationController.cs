using System.ComponentModel.DataAnnotations;
using BLL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Static_X.API.Controllers;

/// <summary>One-time activation token and the new administrator password.</summary>
public sealed record ActivateAccountRequest
{
    [Required] public required string Token { get; init; }
    [Required] public required string Password { get; init; }
}

[ApiController]
[Route("api/account/activation")]
public sealed class ActivationController(ActivationService activation) : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Activate(ActivateAccountRequest request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!await activation.ActivateAsync(request.Token, request.Password, cancellationToken))
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid activation request." });
        return NoContent();
    }
}
