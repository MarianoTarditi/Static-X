namespace BLL.Services.Interfaces;

public interface IPasswordRecoveryDispatcher
{
    Task CleanupAsync(CancellationToken cancellationToken = default);
    Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default);
}
