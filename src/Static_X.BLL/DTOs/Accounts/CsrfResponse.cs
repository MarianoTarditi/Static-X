using System.Text.Json.Serialization;

namespace BLL.DTOs.Accounts;

/// <summary>Same-origin antiforgery request token, without its HttpOnly cookie.</summary>
public sealed class CsrfResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; }

    public CsrfResponse(string? token)
    {
        Token = token;
    }
}
