using BLL.Interfaces;
using Resend;

namespace Static_X.API.Integrations;

public sealed class ResendPasswordRecoveryEmailSender : IPasswordRecoveryEmailSender
{
    private readonly IResend resend;

    public ResendPasswordRecoveryEmailSender(IResend resend)
    {
        this.resend = resend;
    }

    public async Task<bool> SendAsync(string idempotencyKey, string from, string to, string link, CancellationToken cancellationToken)
    {
        var response = await resend.EmailSendAsync(idempotencyKey, new EmailMessage
        {
            From = from, To = to, Subject = "Reset your Static-X password",
            TextBody = "Open this link to reset your password. It expires in 30 minutes: " + link
        }, cancellationToken);
        return response.Success;
    }
}
