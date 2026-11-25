namespace BLL.DTOs.Accounts;

/// <summary>Confirms request acceptance without disclosing account eligibility or delivery.</summary>
public sealed class ForgotPasswordResponse
{
    public string Message { get; }

    public ForgotPasswordResponse()
    {
        Message = "If an eligible account exists, password reset instructions will be sent.";
    }
}
