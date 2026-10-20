namespace BLL;

public interface IActivationEmailSender
{
    Task<bool> SendAsync(string idempotencyKey, string from, string to, string link,
        CancellationToken cancellationToken);
}
