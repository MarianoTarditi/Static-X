using BLL;
using Resend;

namespace DAL;

public sealed class ResendActivationEmailSender(IResend resend) : IActivationEmailSender
{
    public async Task<bool> SendAsync(string idempotencyKey, string from, string to, string link,
        CancellationToken cancellationToken)
    {
        var response = await resend.EmailSendAsync(idempotencyKey, new EmailMessage
        {
            From = from, To = to, Subject = "Activate your Static-X account",
            TextBody = "Open this link to set your password: " + link
        }, cancellationToken);
        return response.Success;
    }
}
