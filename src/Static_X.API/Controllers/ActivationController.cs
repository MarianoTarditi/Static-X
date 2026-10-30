using BLL.DTOs.Activations;
using BLL.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Static_X.API.Controllers;

[ApiController]
[Route("api/account/activation")]
public sealed class ActivationController : ControllerBase
{
    private readonly IActivationService activation;

    public ActivationController(IActivationService activation)
    {
        this.activation = activation;
    }

    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType<ActivationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Activate(
        ActivateAccountRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";

        var response = await activation.ActivateAsync(request, cancellationToken);
        if (response is null)
            return BadRequest(new ProblemDetails
            {
                Status = 400,
                Title = "Invalid activation request."
            });

        return Ok(response);
    }
}
