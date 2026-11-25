namespace BLL.Interfaces;

public interface IPasswordRecoveryEmailSender
{
    Task<bool> SendAsync(string idempotencyKey, string from, string to, string link, CancellationToken cancellationToken);
}
